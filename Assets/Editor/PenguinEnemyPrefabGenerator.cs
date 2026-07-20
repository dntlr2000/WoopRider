using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class PenguinEnemyPrefabGenerator
{
    private const string ModelPath = "Assets/Resources/fbx/Penguin_enemy.fbx";
    private const string BodyTexturePath = "Assets/Resources/fbx/penguin_bodyColor.png";
    private const string OutputFolder = "Assets/Resources/Prefabs/Enemys";
    private const string ControllerPath = OutputFolder + "/Penguin_enemy.controller";
    private const string BodyMaterialPath = OutputFolder + "/Penguin_body.mat";
    private const string PrefabPath = OutputFolder + "/Penguin_enemy.prefab";
    private const string MoveSpeedParameter = "MoveSpeed";
    private const string IsRunningParameter = "IsRunning";
    private const string IsDeadParameter = "IsDead";
    private const float MoveThreshold = 0.1f;

    private static readonly string[] RequiredClipNames =
    {
        "idle",
        "walk",
        "Roll_L",
        "death"
    };

    [MenuItem("Tools/WoopRider/Generate Penguin Enemy Prefab")]
    public static void Generate()
    {
        // Generate the Penguin enemy controller, material, and editable prefab from the source FBX.
        EnsureOutputFolder();
        ConfigureClipLoops();

        Dictionary<string, AnimationClip> clips = ResolveRequiredClips();
        AnimatorController controller = GenerateAnimatorController(clips);
        GameObject prefab = GeneratePrefab(controller);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        ValidateGeneratedAssets(prefab, controller);
        Debug.Log($"[PenguinEnemyPrefabGenerator] Generated Penguin enemy assets at {OutputFolder}.");
    }

    private static void EnsureOutputFolder()
    {
        // Create each output folder segment when the target enemy folder does not exist yet.
        EnsureFolder("Assets", "Resources");
        EnsureFolder("Assets/Resources", "Prefabs");
        EnsureFolder("Assets/Resources/Prefabs", "Enemys");
    }

    private static void EnsureFolder(string parentFolder, string childFolderName)
    {
        // Create one AssetDatabase folder beneath a known parent when it is missing.
        string childPath = parentFolder + "/" + childFolderName;
        if (!AssetDatabase.IsValidFolder(childPath))
        {
            AssetDatabase.CreateFolder(parentFolder, childFolderName);
        }
    }

    private static void ConfigureClipLoops()
    {
        // Persist looping for idle, walk, and the running Roll_L clip while leaving death as a one-shot.
        ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"Model importer not found at {ModelPath}.");
        }

        ModelImporterClipAnimation[] clipAnimations = importer.clipAnimations;
        if (clipAnimations == null || clipAnimations.Length == 0)
        {
            clipAnimations = importer.defaultClipAnimations;
        }

        bool changed = false;
        for (int i = 0; i < clipAnimations.Length; i++)
        {
            ModelImporterClipAnimation clipAnimation = clipAnimations[i];
            bool shouldLoop = IsNamedClip(clipAnimation.name, "idle") ||
                              IsNamedClip(clipAnimation.name, "walk") ||
                              IsNamedClip(clipAnimation.name, "Roll_L");
            if (clipAnimation.loopTime == shouldLoop && clipAnimation.loopPose == shouldLoop)
            {
                continue;
            }

            clipAnimation.loopTime = shouldLoop;
            clipAnimation.loopPose = shouldLoop;
            clipAnimations[i] = clipAnimation;
            changed = true;
        }

        if (changed)
        {
            importer.clipAnimations = clipAnimations;
            importer.SaveAndReimport();
        }
    }

    private static Dictionary<string, AnimationClip> ResolveRequiredClips()
    {
        // Resolve every requested controller state to an embedded FBX animation clip.
        AnimationClip[] availableClips = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
            .OfType<AnimationClip>()
            .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
            .ToArray();
        Dictionary<string, AnimationClip> resolvedClips = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < RequiredClipNames.Length; i++)
        {
            string requiredName = RequiredClipNames[i];
            AnimationClip clip = availableClips.FirstOrDefault(candidate => IsNamedClip(candidate.name, requiredName));
            if (clip == null)
            {
                string availableNames = string.Join(", ", availableClips.Select(candidate => candidate.name));
                throw new InvalidOperationException(
                    $"Could not resolve Penguin clip '{requiredName}'. Available clips: {availableNames}");
            }

            resolvedClips[requiredName] = clip;
        }

        Debug.Log(
            "[PenguinEnemyPrefabGenerator] Resolved clips: " +
            string.Join(", ", resolvedClips.Select(pair => $"{pair.Key}={pair.Value.name}")));
        return resolvedClips;
    }

    private static bool IsNamedClip(string importedName, string requiredName)
    {
        // Match direct names and FBX-prefixed names after removing common separators.
        string normalizedImportedName = NormalizeName(importedName);
        string normalizedRequiredName = NormalizeName(requiredName);
        return normalizedImportedName == normalizedRequiredName ||
               normalizedImportedName.EndsWith(normalizedRequiredName, StringComparison.Ordinal);
    }

    private static string NormalizeName(string value)
    {
        // Normalize Blender and Unity naming separators for reliable clip matching.
        return value
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("|", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static AnimatorController GenerateAnimatorController(Dictionary<string, AnimationClip> clips)
    {
        // Build a deterministic AnimatorController for locomotion, rolling, and terminal death playback.
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
        {
            AssetDatabase.DeleteAsset(ControllerPath);
        }

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        AddControllerParameters(controller);
        BuildStateMachine(controller.layers[0].stateMachine, clips);
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static void AddControllerParameters(AnimatorController controller)
    {
        // Expose stable gameplay parameters for movement speed, running state, and persistent death state.
        controller.AddParameter(new AnimatorControllerParameter
        {
            name = MoveSpeedParameter,
            type = AnimatorControllerParameterType.Float,
            defaultFloat = 0f
        });
        controller.AddParameter(new AnimatorControllerParameter
        {
            name = IsRunningParameter,
            type = AnimatorControllerParameterType.Bool,
            defaultBool = false
        });
        controller.AddParameter(new AnimatorControllerParameter
        {
            name = IsDeadParameter,
            type = AnimatorControllerParameterType.Bool,
            defaultBool = false
        });
    }

    private static void BuildStateMachine(
        AnimatorStateMachine stateMachine,
        IReadOnlyDictionary<string, AnimationClip> clips)
    {
        // Connect idle, walk, running, and death states with gameplay-friendly transition conditions.
        AnimatorState idleState = AddState(stateMachine, "idle", clips["idle"], 220f, 0f);
        AnimatorState walkState = AddState(stateMachine, "walk", clips["walk"], 500f, 0f);
        AnimatorState rollState = AddState(stateMachine, "Roll_L", clips["Roll_L"], 360f, 180f);
        AnimatorState deathState = AddState(stateMachine, "death", clips["death"], 640f, 180f);
        stateMachine.defaultState = idleState;

        AddLocomotionTransition(idleState, walkState, AnimatorConditionMode.Greater);
        AddLocomotionTransition(walkState, idleState, AnimatorConditionMode.Less);
        AddRunningReturnTransition(rollState, idleState, AnimatorConditionMode.Less);
        AddRunningReturnTransition(rollState, walkState, AnimatorConditionMode.Greater);

        AnimatorStateTransition deathTransition = stateMachine.AddAnyStateTransition(deathState);
        ConfigureTransition(deathTransition, false, 0f, 0.05f);
        deathTransition.canTransitionToSelf = false;
        deathTransition.AddCondition(AnimatorConditionMode.If, 0f, IsDeadParameter);

        AddRunningEntryTransition(stateMachine, rollState);
    }

    private static AnimatorState AddState(
        AnimatorStateMachine stateMachine,
        string stateName,
        Motion motion,
        float x,
        float y)
    {
        // Add one named state with a stable graph position and its resolved FBX motion.
        AnimatorState state = stateMachine.AddState(stateName, new Vector3(x, y, 0f));
        state.motion = motion;
        return state;
    }

    private static void AddLocomotionTransition(
        AnimatorState fromState,
        AnimatorState toState,
        AnimatorConditionMode speedCondition)
    {
        // Switch between idle and walk immediately when speed crosses the movement threshold.
        AnimatorStateTransition transition = fromState.AddTransition(toState);
        ConfigureTransition(transition, false, 0f, 0.1f);
        transition.AddCondition(speedCondition, MoveThreshold, MoveSpeedParameter);
        transition.AddCondition(AnimatorConditionMode.IfNot, 0f, IsDeadParameter);
    }

    private static void AddRunningReturnTransition(
        AnimatorState runningState,
        AnimatorState locomotionState,
        AnimatorConditionMode speedCondition)
    {
        // Leave the looping running state immediately after IsRunning is disabled.
        AnimatorStateTransition transition = runningState.AddTransition(locomotionState);
        ConfigureTransition(transition, false, 0f, 0.08f);
        transition.AddCondition(speedCondition, MoveThreshold, MoveSpeedParameter);
        transition.AddCondition(AnimatorConditionMode.IfNot, 0f, IsRunningParameter);
        transition.AddCondition(AnimatorConditionMode.IfNot, 0f, IsDeadParameter);
    }

    private static void AddRunningEntryTransition(AnimatorStateMachine stateMachine, AnimatorState runningState)
    {
        // Enter Roll_L from any living state while the persistent running flag is enabled.
        AnimatorStateTransition transition = stateMachine.AddAnyStateTransition(runningState);
        ConfigureTransition(transition, false, 0f, 0.05f);
        transition.canTransitionToSelf = false;
        transition.AddCondition(AnimatorConditionMode.If, 0f, IsRunningParameter);
        transition.AddCondition(AnimatorConditionMode.IfNot, 0f, IsDeadParameter);
    }

    private static void ConfigureTransition(
        AnimatorStateTransition transition,
        bool hasExitTime,
        float exitTime,
        float duration)
    {
        // Apply consistent transition timing without delaying gameplay-driven state changes.
        transition.hasExitTime = hasExitTime;
        transition.exitTime = exitTime;
        transition.hasFixedDuration = true;
        transition.duration = duration;
        transition.offset = 0f;
        transition.interruptionSource = TransitionInterruptionSource.None;
        transition.orderedInterruption = true;
    }

    private static GameObject GeneratePrefab(RuntimeAnimatorController controller)
    {
        // Instantiate the FBX beneath an editable prefab root and attach the generated controller.
        GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (modelAsset == null)
        {
            throw new InvalidOperationException($"Model asset not found at {ModelPath}.");
        }

        Texture2D bodyTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(BodyTexturePath);
        if (bodyTexture == null)
        {
            throw new InvalidOperationException($"Body texture not found at {BodyTexturePath}.");
        }

        GameObject prefabRoot = new("Penguin_enemy");
        try
        {
            GameObject modelInstance = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
            if (modelInstance == null)
            {
                throw new InvalidOperationException($"Could not instantiate model asset at {ModelPath}.");
            }

            modelInstance.name = "Model";
            modelInstance.transform.SetParent(prefabRoot.transform, false);
            modelInstance.transform.localPosition = Vector3.zero;
            modelInstance.transform.localRotation = Quaternion.identity;
            modelInstance.transform.localScale = Vector3.one;

            ApplyBodyMaterial(modelInstance, bodyTexture);
            ConfigureAnimator(modelInstance, controller);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                AssetDatabase.DeleteAsset(PrefabPath);
            }

            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(prefabRoot, PrefabPath);
            if (savedPrefab == null)
            {
                throw new InvalidOperationException($"Failed to save prefab at {PrefabPath}.");
            }

            return savedPrefab;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(prefabRoot);
        }
    }

    private static void ApplyBodyMaterial(GameObject modelInstance, Texture2D bodyTexture)
    {
        // Identify the body material slot and explicitly bind the supplied body texture to it.
        List<MaterialSlot> bodySlots = FindBodyMaterialSlots(modelInstance, bodyTexture);
        Material sourceMaterial = bodySlots[0].Material;
        bool textureWasAlreadyAssigned = bodySlots.Any(slot => MaterialUsesTexture(slot.Material, bodyTexture));
        Material bodyMaterial = CreateOrUpdateBodyMaterial(sourceMaterial, bodyTexture);

        for (int i = 0; i < bodySlots.Count; i++)
        {
            MaterialSlot slot = bodySlots[i];
            Material[] materials = slot.Renderer.sharedMaterials;
            materials[slot.MaterialIndex] = bodyMaterial;
            slot.Renderer.sharedMaterials = materials;
        }

        string status = textureWasAlreadyAssigned ? "already referenced the texture" : "required a texture override";
        Debug.Log(
            $"[PenguinEnemyPrefabGenerator] Penguin body source material {status}; " +
            $"assigned {bodyTexture.name} to {bodySlots.Count} body material slot(s).");
    }

    private static List<MaterialSlot> FindBodyMaterialSlots(GameObject modelInstance, Texture2D bodyTexture)
    {
        // Resolve body slots by exact texture reference, material name, renderer name, or an unambiguous sole slot.
        List<MaterialSlot> allSlots = CollectMaterialSlots(modelInstance);
        List<MaterialSlot> matchedSlots = allSlots
            .Where(slot => MaterialUsesTexture(slot.Material, bodyTexture))
            .ToList();

        if (matchedSlots.Count == 0)
        {
            matchedSlots = allSlots
                .Where(slot => slot.Material.name.Contains("body", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matchedSlots.Count == 0)
        {
            matchedSlots = allSlots
                .Where(slot => slot.Renderer.name.Contains("body", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (matchedSlots.Count == 0 && allSlots.Count == 1)
        {
            matchedSlots.Add(allSlots[0]);
        }

        if (matchedSlots.Count == 0)
        {
            string slotDescription = string.Join(", ", allSlots.Select(
                slot => $"{slot.Renderer.name}[{slot.MaterialIndex}]={slot.Material.name}"));
            throw new InvalidOperationException(
                "Could not identify Penguin body material slot. " +
                $"Available renderer materials: {slotDescription}");
        }

        return matchedSlots;
    }

    private static List<MaterialSlot> CollectMaterialSlots(GameObject modelInstance)
    {
        // Collect every non-null renderer material slot beneath the instantiated FBX.
        List<MaterialSlot> slots = new();
        Renderer[] renderers = modelInstance.GetComponentsInChildren<Renderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            Material[] materials = renderer.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                if (materials[materialIndex] != null)
                {
                    slots.Add(new MaterialSlot(renderer, materialIndex, materials[materialIndex]));
                }
            }
        }

        return slots;
    }

    private static bool MaterialUsesTexture(Material material, Texture texture)
    {
        // Check common Standard and URP base-map properties for the exact imported texture asset.
        if (material == null || texture == null)
        {
            return false;
        }

        return material.mainTexture == texture ||
               (material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") == texture) ||
               (material.HasProperty("_MainTex") && material.GetTexture("_MainTex") == texture);
    }

    private static Material CreateOrUpdateBodyMaterial(Material sourceMaterial, Texture2D bodyTexture)
    {
        // Create a persistent URP-compatible body material and preserve useful source surface values.
        Shader targetShader = Shader.Find("Universal Render Pipeline/Lit") ?? sourceMaterial.shader;
        Material bodyMaterial = AssetDatabase.LoadAssetAtPath<Material>(BodyMaterialPath);

        if (bodyMaterial == null)
        {
            bodyMaterial = new Material(targetShader)
            {
                name = "Penguin_body"
            };
            AssetDatabase.CreateAsset(bodyMaterial, BodyMaterialPath);
        }
        else
        {
            bodyMaterial.shader = targetShader;
        }

        CopySurfaceProperties(sourceMaterial, bodyMaterial);
        if (bodyMaterial.HasProperty("_BaseMap"))
        {
            bodyMaterial.SetTexture("_BaseMap", bodyTexture);
        }

        if (bodyMaterial.HasProperty("_MainTex"))
        {
            bodyMaterial.SetTexture("_MainTex", bodyTexture);
        }

        bodyMaterial.mainTexture = bodyTexture;
        EditorUtility.SetDirty(bodyMaterial);
        return bodyMaterial;
    }

    private static void CopySurfaceProperties(Material sourceMaterial, Material targetMaterial)
    {
        // Transfer base color and smoothness without depending on the FBX material shader family.
        Color sourceColor = Color.white;
        if (sourceMaterial.HasProperty("_BaseColor"))
        {
            sourceColor = sourceMaterial.GetColor("_BaseColor");
        }
        else if (sourceMaterial.HasProperty("_Color"))
        {
            sourceColor = sourceMaterial.GetColor("_Color");
        }

        if (targetMaterial.HasProperty("_BaseColor"))
        {
            targetMaterial.SetColor("_BaseColor", sourceColor);
        }

        if (targetMaterial.HasProperty("_Color"))
        {
            targetMaterial.SetColor("_Color", sourceColor);
        }

        float smoothness = 0.5f;
        if (sourceMaterial.HasProperty("_Smoothness"))
        {
            smoothness = sourceMaterial.GetFloat("_Smoothness");
        }
        else if (sourceMaterial.HasProperty("_Glossiness"))
        {
            smoothness = sourceMaterial.GetFloat("_Glossiness");
        }

        if (targetMaterial.HasProperty("_Smoothness"))
        {
            targetMaterial.SetFloat("_Smoothness", smoothness);
        }
    }

    private static void ConfigureAnimator(GameObject modelInstance, RuntimeAnimatorController controller)
    {
        // Reuse the imported Animator, assign its Avatar when needed, and disable root-motion movement.
        Animator animator = modelInstance.GetComponentInChildren<Animator>(true) ?? modelInstance.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        if (animator.avatar == null)
        {
            animator.avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
        }

        EditorUtility.SetDirty(animator);
    }

    private static void ValidateGeneratedAssets(GameObject prefab, AnimatorController controller)
    {
        // Verify the saved prefab references the controller, body texture, and all requested state motions.
        if (prefab == null || controller == null)
        {
            throw new InvalidOperationException("Penguin prefab or AnimatorController was not generated.");
        }

        Animator animator = prefab.GetComponentInChildren<Animator>(true);
        if (animator == null || animator.runtimeAnimatorController != controller)
        {
            throw new InvalidOperationException("Generated Penguin prefab does not reference its AnimatorController.");
        }

        Texture2D bodyTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(BodyTexturePath);
        bool bodyTextureAssigned = prefab.GetComponentsInChildren<Renderer>(true)
            .SelectMany(renderer => renderer.sharedMaterials)
            .Any(material => MaterialUsesTexture(material, bodyTexture));
        if (!bodyTextureAssigned)
        {
            throw new InvalidOperationException("Generated Penguin prefab does not reference penguin_bodyColor.png.");
        }

        Dictionary<string, AnimatorState> statesByName = controller.layers[0].stateMachine.states
            .Select(childState => childState.state)
            .ToDictionary(state => state.name, StringComparer.Ordinal);
        string[] missingStates = RequiredClipNames
            .Where(requiredName => !statesByName.TryGetValue(requiredName, out AnimatorState state) || state.motion == null)
            .ToArray();
        if (missingStates.Length > 0)
        {
            throw new InvalidOperationException(
                "Generated Penguin controller is missing states or motions: " + string.Join(", ", missingStates));
        }

        bool parametersValid = controller.parameters.Any(
                                   parameter => parameter.name == MoveSpeedParameter &&
                                                parameter.type == AnimatorControllerParameterType.Float) &&
                               controller.parameters.Any(
                                   parameter => parameter.name == IsRunningParameter &&
                                                parameter.type == AnimatorControllerParameterType.Bool) &&
                               controller.parameters.Any(
                                   parameter => parameter.name == IsDeadParameter &&
                                                parameter.type == AnimatorControllerParameterType.Bool);
        if (!parametersValid)
        {
            throw new InvalidOperationException("Generated Penguin controller parameters do not match the gameplay contract.");
        }

        Debug.Log(
            "[PenguinEnemyPrefabGenerator] Validation passed: controller, four FBX motions, parameters, " +
            "and penguin_bodyColor texture are linked to the generated prefab.");
    }

    private readonly struct MaterialSlot
    {
        public readonly Renderer Renderer;
        public readonly int MaterialIndex;
        public readonly Material Material;

        public MaterialSlot(Renderer renderer, int materialIndex, Material material)
        {
            // Store one renderer material location so the prefab override can be applied precisely.
            Renderer = renderer;
            MaterialIndex = materialIndex;
            Material = material;
        }
    }
}
