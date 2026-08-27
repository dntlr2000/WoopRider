using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;

public static class JumpPlatformPrefabGenerator
{
    private const string ModelPath = "Assets/Resources/fbx/Structures/JumpPlatform.fbx";
    private const string OutputFolder = "Assets/Resources/Prefabs/Stages";
    private const string ControllerPath = OutputFolder + "/JumpPlatform.controller";
    private const string PrefabPath = OutputFolder + "/JumpPlatform.prefab";
    private const string InactivePosePath = OutputFolder + "/JumpPlatform_InActivate.anim";
    private const string OriginPosePath = OutputFolder + "/JumpPlatform_origin.anim";
    private const string ActivateParameter = "Activate";
    private const string InactivateParameter = "InActivate";
    private const string OriginParameter = "Origin";

    private static readonly string[] RequiredStateNames =
    {
        "origin",
        "Activate",
        "InActivate"
    };

    [MenuItem("Tools/WoopRider/Generate Jump Platform Prefab")]
    public static void Generate()
    {
        // Generate Unity-safe clips, an AnimatorController, URP materials, and the JumpPlatform prefab.
        EnsureOutputFolder();
        ConfigureImportedClipLoops();

        Dictionary<string, AnimationClip> importedClips = ResolveImportedClips();
        Dictionary<string, AnimationClip> stateMotions = BuildStateMotions(importedClips);
        AnimatorController controller = GenerateAnimatorController(stateMotions);
        GameObject prefab = GeneratePrefab(controller);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        ValidateGeneratedAssets(prefab, controller, stateMotions);
        Debug.Log($"[JumpPlatformPrefabGenerator] Generated JumpPlatform assets at {OutputFolder}.");
    }

    private static void EnsureOutputFolder()
    {
        // Create each Resources output folder segment when it does not already exist.
        EnsureFolder("Assets", "Resources");
        EnsureFolder("Assets/Resources", "Prefabs");
        EnsureFolder("Assets/Resources/Prefabs", "Stages");
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

    private static void ConfigureImportedClipLoops()
    {
        // Loop the cyclic Activate clip while keeping both static pose takes non-looping.
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
            bool shouldLoop = IsNamedClip(clipAnimation.name, "Activate");
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

    private static Dictionary<string, AnimationClip> ResolveImportedClips()
    {
        // Resolve the three named FBX takes even when Unity prefixes them with the armature name.
        AnimationClip[] availableClips = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
            .OfType<AnimationClip>()
            .Where(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
            .ToArray();
        Dictionary<string, AnimationClip> resolved = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < RequiredStateNames.Length; i++)
        {
            string requiredName = RequiredStateNames[i];
            AnimationClip clip = availableClips.FirstOrDefault(candidate => IsNamedClip(candidate.name, requiredName));
            if (clip == null)
            {
                string availableNames = string.Join(", ", availableClips.Select(candidate => candidate.name));
                throw new InvalidOperationException(
                    $"Could not resolve JumpPlatform clip '{requiredName}'. Available clips: {availableNames}");
            }

            resolved[requiredName] = clip;
        }

        Debug.Log(
            "[JumpPlatformPrefabGenerator] Resolved clips: " +
            string.Join(", ", resolved.Select(pair => $"{pair.Key}={pair.Value.name} ({pair.Value.length:0.###}s)")));
        return resolved;
    }

    private static bool IsNamedClip(string importedName, string requiredName)
    {
        // Match the exact terminal FBX take name so InActivate cannot be mistaken for Activate.
        int pipeIndex = importedName.LastIndexOf('|');
        int colonIndex = importedName.LastIndexOf(':');
        int separatorIndex = Math.Max(pipeIndex, colonIndex);
        string takeName = separatorIndex >= 0 ? importedName.Substring(separatorIndex + 1) : importedName;
        string normalizedImportedName = NormalizeName(takeName);
        string normalizedRequiredName = NormalizeName(requiredName);
        return normalizedImportedName == normalizedRequiredName;
    }

    private static string NormalizeName(string value)
    {
        // Normalize Blender and Unity separators for stable clip and material matching.
        return value
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("|", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static Dictionary<string, AnimationClip> BuildStateMotions(
        IReadOnlyDictionary<string, AnimationClip> importedClips)
    {
        // Replace Unity's zero-length pose takes with generated one-frame clips derived from FBX curves.
        AnimationClip activateClip = importedClips["Activate"];
        AnimationClip inactivePose = CreateOrUpdatePoseClip(
            importedClips["InActivate"],
            activateClip,
            InactivePosePath,
            "JumpPlatform_InActivate",
            true);
        AnimationClip originPose = CreateOrUpdatePoseClip(
            importedClips["origin"],
            activateClip,
            OriginPosePath,
            "JumpPlatform_origin",
            false);

        return new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase)
        {
            { "origin", originPose },
            { "Activate", activateClip },
            { "InActivate", inactivePose }
        };
    }

    private static AnimationClip CreateOrUpdatePoseClip(
        AnimationClip sourcePose,
        AnimationClip fallbackSource,
        string outputPath,
        string outputName,
        bool hideGlowingCenter)
    {
        // Bake a static FBX pose into a short standalone clip that Unity can evaluate reliably.
        AnimationClip poseClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
        if (poseClip == null)
        {
            poseClip = new AnimationClip
            {
                name = outputName,
                legacy = false
            };
            AssetDatabase.CreateAsset(poseClip, outputPath);
        }
        else
        {
            poseClip.ClearCurves();
            poseClip.name = outputName;
            poseClip.legacy = false;
        }

        float frameRate = fallbackSource.frameRate > 0f ? fallbackSource.frameRate : 24f;
        float poseDuration = 1f / frameRate;
        poseClip.frameRate = frameRate;

        EditorCurveBinding[] sourceBindings = AnimationUtility.GetCurveBindings(sourcePose);
        AnimationClip curveSource = sourceBindings.Length > 0 ? sourcePose : fallbackSource;
        sourceBindings = AnimationUtility.GetCurveBindings(curveSource);
        CopyStaticFloatCurves(curveSource, sourceBindings, poseClip, poseDuration);
        CopyStaticObjectCurves(curveSource, poseClip, poseDuration);

        if (hideGlowingCenter)
        {
            ForceGlowingCenterScale(poseClip, fallbackSource, poseDuration, 0f);
        }

        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(poseClip);
        settings.loopTime = false;
        settings.loopBlend = false;
        AnimationUtility.SetAnimationClipSettings(poseClip, settings);
        AnimationUtility.SetAnimationEvents(poseClip, Array.Empty<AnimationEvent>());
        EditorUtility.SetDirty(poseClip);

        Debug.Log(
            $"[JumpPlatformPrefabGenerator] Baked {outputName} from {curveSource.name} with " +
            $"{AnimationUtility.GetCurveBindings(poseClip).Length} curves.");
        return poseClip;
    }

    private static void CopyStaticFloatCurves(
        AnimationClip source,
        IReadOnlyList<EditorCurveBinding> bindings,
        AnimationClip destination,
        float duration)
    {
        // Copy every float binding as a two-key constant curve sampled at the source pose time.
        for (int i = 0; i < bindings.Count; i++)
        {
            EditorCurveBinding binding = bindings[i];
            AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(source, binding);
            if (sourceCurve == null || sourceCurve.length == 0)
            {
                continue;
            }

            float sampledValue = sourceCurve.Evaluate(0f);
            AnimationCurve poseCurve = AnimationCurve.Constant(0f, duration, sampledValue);
            AnimationUtility.SetEditorCurve(destination, binding, poseCurve);
        }
    }

    private static void CopyStaticObjectCurves(AnimationClip source, AnimationClip destination, float duration)
    {
        // Copy object-reference bindings as held two-key curves when the FBX take contains any.
        EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(source);
        for (int i = 0; i < objectBindings.Length; i++)
        {
            EditorCurveBinding binding = objectBindings[i];
            ObjectReferenceKeyframe[] sourceKeys = AnimationUtility.GetObjectReferenceCurve(source, binding);
            if (sourceKeys == null || sourceKeys.Length == 0)
            {
                continue;
            }

            UnityEngine.Object sampledValue = sourceKeys[0].value;
            ObjectReferenceKeyframe[] poseKeys =
            {
                new() { time = 0f, value = sampledValue },
                new() { time = duration, value = sampledValue }
            };
            AnimationUtility.SetObjectReferenceCurve(destination, binding, poseKeys);
        }
    }

    private static void ForceGlowingCenterScale(
        AnimationClip poseClip,
        AnimationClip bindingSource,
        float duration,
        float scale)
    {
        // Force all GlowingCenter local-scale axes to the hidden or visible pose value.
        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(poseClip)
            .Where(IsGlowingCenterScaleBinding)
            .ToArray();
        if (bindings.Length == 0)
        {
            bindings = AnimationUtility.GetCurveBindings(bindingSource)
                .Where(IsGlowingCenterScaleBinding)
                .ToArray();
        }

        if (bindings.Length == 0)
        {
            throw new InvalidOperationException("Could not find GlowingCenter scale bindings in JumpPlatform clips.");
        }

        for (int i = 0; i < bindings.Length; i++)
        {
            AnimationUtility.SetEditorCurve(
                poseClip,
                bindings[i],
                AnimationCurve.Constant(0f, duration, scale));
        }
    }

    private static bool IsGlowingCenterScaleBinding(EditorCurveBinding binding)
    {
        // Identify the three Transform scale channels belonging to the GlowingCenter bone.
        return binding.path.Contains("GlowingCenter", StringComparison.OrdinalIgnoreCase) &&
               binding.propertyName.Contains("m_LocalScale", StringComparison.Ordinal);
    }

    private static AnimatorController GenerateAnimatorController(
        IReadOnlyDictionary<string, AnimationClip> stateMotions)
    {
        // Build a deterministic controller with one selectable state per FBX animation take.
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null)
        {
            AssetDatabase.DeleteAsset(ControllerPath);
        }

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter(ActivateParameter, AnimatorControllerParameterType.Trigger);
        controller.AddParameter(InactivateParameter, AnimatorControllerParameterType.Trigger);
        controller.AddParameter(OriginParameter, AnimatorControllerParameterType.Trigger);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        AnimatorState originState = AddState(stateMachine, "origin", stateMotions["origin"], 220f, 0f);
        AnimatorState activateState = AddState(stateMachine, "Activate", stateMotions["Activate"], 500f, 0f);
        AnimatorState inactivateState = AddState(stateMachine, "InActivate", stateMotions["InActivate"], 360f, 180f);
        stateMachine.defaultState = originState;

        AddAnyStateTriggerTransition(stateMachine, inactivateState, InactivateParameter);
        AddAnyStateTriggerTransition(stateMachine, activateState, ActivateParameter);
        AddAnyStateTriggerTransition(stateMachine, originState, OriginParameter);

        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static AnimatorState AddState(
        AnimatorStateMachine stateMachine,
        string stateName,
        Motion motion,
        float x,
        float y)
    {
        // Add one named Animator state at a stable graph position with its resolved motion.
        AnimatorState state = stateMachine.AddState(stateName, new Vector3(x, y, 0f));
        state.motion = motion;
        return state;
    }

    private static void AddAnyStateTriggerTransition(
        AnimatorStateMachine stateMachine,
        AnimatorState targetState,
        string triggerName)
    {
        // Allow gameplay to select and restart any platform animation state immediately.
        AnimatorStateTransition transition = stateMachine.AddAnyStateTransition(targetState);
        transition.hasExitTime = false;
        transition.exitTime = 0f;
        transition.hasFixedDuration = true;
        transition.duration = 0.05f;
        transition.offset = 0f;
        transition.canTransitionToSelf = true;
        transition.interruptionSource = TransitionInterruptionSource.None;
        transition.orderedInterruption = true;
        transition.AddCondition(AnimatorConditionMode.If, 0f, triggerName);
    }

    private static GameObject GeneratePrefab(RuntimeAnimatorController controller)
    {
        // Instantiate the FBX beneath an editable prefab root and attach controller and URP materials.
        GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (modelAsset == null)
        {
            throw new InvalidOperationException($"Model asset not found at {ModelPath}.");
        }

        GameObject prefabRoot = new("JumpPlatform");
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

            ApplyUrpMaterials(modelInstance);
            ConfigureAnimator(modelInstance, controller);
            ConfigureGameplay(prefabRoot);

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

    private static void ApplyUrpMaterials(GameObject modelInstance)
    {
        // Replace imported FBX materials with persistent URP materials while retaining slot assignments.
        Dictionary<Material, Material> replacements = new();
        Renderer[] renderers = modelInstance.GetComponentsInChildren<Renderer>(true);
        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            Material[] materials = renderer.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material sourceMaterial = materials[materialIndex];
                if (sourceMaterial == null)
                {
                    continue;
                }

                if (!replacements.TryGetValue(sourceMaterial, out Material replacement))
                {
                    replacement = CreateOrUpdateUrpMaterial(sourceMaterial);
                    replacements[sourceMaterial] = replacement;
                }

                materials[materialIndex] = replacement;
            }

            renderer.sharedMaterials = materials;
        }

        Debug.Log($"[JumpPlatformPrefabGenerator] Assigned {replacements.Count} URP material(s).");
    }

    private static Material CreateOrUpdateUrpMaterial(Material sourceMaterial)
    {
        // Create one URP/Lit material with source color, smoothness, transparency, and emission intent.
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            throw new InvalidOperationException("Universal Render Pipeline/Lit shader was not found.");
        }

        string normalizedName = NormalizeName(sourceMaterial.name);
        string outputPath = GetMaterialOutputPath(normalizedName);
        Material material = AssetDatabase.LoadAssetAtPath<Material>(outputPath);
        if (material == null)
        {
            material = new Material(shader)
            {
                name = System.IO.Path.GetFileNameWithoutExtension(outputPath)
            };
            AssetDatabase.CreateAsset(material, outputPath);
        }
        else
        {
            material.shader = shader;
        }

        Color baseColor = ReadBaseColor(sourceMaterial);
        bool isEffect = normalizedName == "effect";
        bool usesEmission = normalizedName == "baseglow" || isEffect;
        if (isEffect && baseColor.a >= 0.99f)
        {
            baseColor.a = 0.1f;
        }

        SetMaterialColor(material, baseColor);
        SetMaterialSmoothness(material, ReadSmoothness(sourceMaterial));
        ConfigureSurface(material, isEffect);
        ConfigureEmission(material, sourceMaterial, baseColor, usesEmission);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static string GetMaterialOutputPath(string normalizedName)
    {
        // Map known FBX material names to stable, readable Unity asset paths.
        return normalizedName switch
        {
            "innnerbase" => OutputFolder + "/JumpPlatform_InnerBase.mat",
            "base" => OutputFolder + "/JumpPlatform_Base.mat",
            "baseglow" => OutputFolder + "/JumpPlatform_BaseGlow.mat",
            "baseglowdisactivate" => OutputFolder + "/JumpPlatform_BaseInactive.mat",
            "effect" => OutputFolder + "/JumpPlatform_Effect.mat",
            _ => OutputFolder + "/JumpPlatform_" + normalizedName + ".mat"
        };
    }

    private static Color ReadBaseColor(Material sourceMaterial)
    {
        // Read the imported material base color across Standard and URP shader property names.
        if (sourceMaterial.HasProperty("_BaseColor"))
        {
            return sourceMaterial.GetColor("_BaseColor");
        }

        if (sourceMaterial.HasProperty("_Color"))
        {
            return sourceMaterial.GetColor("_Color");
        }

        return Color.white;
    }

    private static float ReadSmoothness(Material sourceMaterial)
    {
        // Read source smoothness while accounting for Standard shader naming.
        if (sourceMaterial.HasProperty("_Smoothness"))
        {
            return sourceMaterial.GetFloat("_Smoothness");
        }

        if (sourceMaterial.HasProperty("_Glossiness"))
        {
            return sourceMaterial.GetFloat("_Glossiness");
        }

        return 0.2f;
    }

    private static void SetMaterialColor(Material material, Color color)
    {
        // Assign base color through every base-map color property exposed by the target shader.
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }
    }

    private static void SetMaterialSmoothness(Material material, float smoothness)
    {
        // Apply a clamped smoothness value to the URP Lit material.
        if (material.HasProperty("_Smoothness"))
        {
            material.SetFloat("_Smoothness", Mathf.Clamp01(smoothness));
        }
    }

    private static void ConfigureSurface(Material material, bool transparent)
    {
        // Configure the URP render state for opaque platform pieces or the transparent rising effect.
        if (transparent)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
            return;
        }

        material.SetOverrideTag("RenderType", "Opaque");
        material.SetFloat("_Surface", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.One);
        material.SetFloat("_DstBlend", (float)BlendMode.Zero);
        material.SetFloat("_ZWrite", 1f);
        material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.renderQueue = (int)RenderQueue.Geometry;
    }

    private static void ConfigureEmission(
        Material material,
        Material sourceMaterial,
        Color baseColor,
        bool enabled)
    {
        // Preserve glowing platform parts through URP emission properties and keywords.
        if (!material.HasProperty("_EmissionColor"))
        {
            return;
        }

        if (!enabled)
        {
            material.SetColor("_EmissionColor", Color.black);
            material.DisableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            return;
        }

        Color emissionColor = sourceMaterial.HasProperty("_EmissionColor")
            ? sourceMaterial.GetColor("_EmissionColor")
            : baseColor;
        if (emissionColor.maxColorComponent <= 0.001f)
        {
            emissionColor = baseColor;
        }

        emissionColor.a = 1f;
        material.SetColor("_EmissionColor", emissionColor * 1.5f);
        material.EnableKeyword("_EMISSION");
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
    }

    private static void ConfigureAnimator(GameObject modelInstance, RuntimeAnimatorController controller)
    {
        // Reuse or add the model Animator and configure it for script-driven platform animation.
        Animator animator = modelInstance.GetComponentInChildren<Animator>(true) ?? modelInstance.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.updateMode = AnimatorUpdateMode.Normal;
        EditorUtility.SetDirty(animator);
    }

    private static void ConfigureGameplay(GameObject prefabRoot)
    {
        // Add the trigger and launch behaviour required by generated jump-platform prefabs.
        BoxCollider launchTrigger = prefabRoot.GetComponent<BoxCollider>() ?? prefabRoot.AddComponent<BoxCollider>();
        launchTrigger.isTrigger = true;
        launchTrigger.center = Vector3.zero;
        launchTrigger.size = new Vector3(2f, 0.5f, 2f);

        if (prefabRoot.GetComponent<JumpPlatform>() == null)
        {
            prefabRoot.AddComponent<JumpPlatform>();
        }
    }

    private static void ValidateGeneratedAssets(
        GameObject prefab,
        AnimatorController controller,
        IReadOnlyDictionary<string, AnimationClip> stateMotions)
    {
        // Verify controller states, clips, parameters, materials, and prefab references after generation.
        if (prefab == null || controller == null)
        {
            throw new InvalidOperationException("JumpPlatform prefab or controller was not generated.");
        }

        Animator animator = prefab.GetComponentInChildren<Animator>(true);
        if (animator == null || !IsSameAsset(animator.runtimeAnimatorController, controller))
        {
            throw new InvalidOperationException("JumpPlatform prefab does not reference its generated controller.");
        }

        Collider launchTrigger = prefab.GetComponent<Collider>();
        if (prefab.GetComponent<JumpPlatform>() == null || launchTrigger == null || !launchTrigger.isTrigger)
        {
            throw new InvalidOperationException("JumpPlatform prefab is missing its launch behaviour or trigger collider.");
        }

        Dictionary<string, AnimatorState> statesByName = controller.layers[0].stateMachine.states
            .Select(childState => childState.state)
            .ToDictionary(state => state.name, StringComparer.Ordinal);
        string[] invalidStates = RequiredStateNames
            .Where(name => !statesByName.TryGetValue(name, out AnimatorState state) ||
                           !IsSameAsset(state.motion, stateMotions[name]))
            .ToArray();
        if (invalidStates.Length > 0)
        {
            throw new InvalidOperationException(
                "JumpPlatform controller has missing or mismatched states: " + string.Join(", ", invalidStates));
        }

        bool parametersValid = HasTrigger(controller, ActivateParameter) &&
                               HasTrigger(controller, InactivateParameter) &&
                               HasTrigger(controller, OriginParameter);
        if (!parametersValid)
        {
            throw new InvalidOperationException("JumpPlatform controller trigger parameters are incomplete.");
        }

        if (stateMotions["InActivate"].length <= 0f || stateMotions["origin"].length <= 0f)
        {
            throw new InvalidOperationException("Generated JumpPlatform pose clips still have zero duration.");
        }

        AnimationClipSettings activateSettings = AnimationUtility.GetAnimationClipSettings(stateMotions["Activate"]);
        if (!activateSettings.loopTime)
        {
            throw new InvalidOperationException("JumpPlatform Activate clip is not configured to loop.");
        }

        Material[] prefabMaterials = prefab.GetComponentsInChildren<Renderer>(true)
            .SelectMany(renderer => renderer.sharedMaterials)
            .Where(material => material != null)
            .ToArray();
        if (prefabMaterials.Length == 0 || prefabMaterials.Any(
                material => !AssetDatabase.GetAssetPath(material).StartsWith(OutputFolder, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("JumpPlatform prefab contains missing or non-generated materials.");
        }

        Material effectMaterial = AssetDatabase.LoadAssetAtPath<Material>(
            OutputFolder + "/JumpPlatform_Effect.mat");
        if (effectMaterial == null ||
            !effectMaterial.HasProperty("_Surface") ||
            effectMaterial.GetFloat("_Surface") < 0.5f)
        {
            throw new InvalidOperationException("JumpPlatform transparent effect material is not configured correctly.");
        }

        Debug.Log(
            "[JumpPlatformPrefabGenerator] Validation passed: prefab, three states, non-empty pose clips, " +
            "looping Activate motion, controller triggers, launch behaviour, and URP materials are linked.");
    }

    private static bool IsSameAsset(UnityEngine.Object first, UnityEngine.Object second)
    {
        // Compare persistent Unity assets by GUID and local file ID across AssetDatabase refreshes.
        if (first == null || second == null)
        {
            return false;
        }

        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(first, out string firstGuid, out long firstLocalId) ||
            !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(second, out string secondGuid, out long secondLocalId))
        {
            return first == second;
        }

        return firstGuid == secondGuid && firstLocalId == secondLocalId;
    }

    private static bool HasTrigger(AnimatorController controller, string parameterName)
    {
        // Check that the generated controller contains one named Trigger parameter.
        return controller.parameters.Any(
            parameter => parameter.name == parameterName &&
                         parameter.type == AnimatorControllerParameterType.Trigger);
    }
}
