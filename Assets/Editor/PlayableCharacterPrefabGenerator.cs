using System;
using System.Text;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class PlayableCharacterPrefabGenerator
{
    private const string OutputFolder = "Assets/Resources/PlayableCharacters";
    private const string SourceNetworkPlayerPrefabPath = "Assets/Resources/Prefabs/NetworkObject_Test.prefab";
    private const string NetworkPlayerPrefabPath = "Assets/Resources/PlayableCharacters/NetworkPlayer_Bangae.prefab";
    private const string BangaeVisualPrefabPath = "Assets/Resources/PlayableCharacters/Bangae_PlayableCharacter.prefab";
    private const string WoopVisualPrefabPath = "Assets/Resources/PlayableCharacters/Woop_WoopRiders_PlayableCharacter.prefab";
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
    private const string DefaultNetworkPrefabsPath = "Assets/DefaultNetworkPrefabs.asset";
    private const string LowHealthSparkPrefabPath = "Assets/Resources/Effects/Hovl Studio/Magic effects pack/Prefabs/Sparks/Sparks red.prefab";
    private const string DamageHitEffectPrefabPath = "Assets/Resources/Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Green hit.prefab";
    private const string BreakExplosionEffectPrefabPath = "Assets/Resources/Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Explosion.prefab";

    [MenuItem("Tools/WoopRider/Generate Playable Character Prefabs")]
    public static void GeneratePlayableCharacterPrefabs()
    {
        // Build visual character prefabs, create the editable Bangae network player, and wire the scene to use it.
        EnsureOutputFolder();

        CreateVisualCharacterPrefab(new CharacterPrefabDefinition(
            "Bangae_PlayableCharacter",
            "Assets/Resources/fbx/Bangae_Playable.fbx",
            "Assets/Resources/PlayableCharacters/Bangae_Playable.controller",
            BangaeVisualPrefabPath,
            new Vector3(0f, -0.5f, 0f),
            new Vector3(1.5f, 1.5f, 1.5f),
            new Vector3(0f, 0.346f, 0f)));

        CreateVisualCharacterPrefab(new CharacterPrefabDefinition(
            "Woop_WoopRiders_PlayableCharacter",
            "Assets/Resources/fbx/Woop_WoopRiders.fbx",
            "Assets/Resources/PlayableCharacters/Woop_WoopRiders.controller",
            WoopVisualPrefabPath,
            Vector3.zero,
            Vector3.one,
            new Vector3(0f, 0.795f, 0f)));

        GameObject networkPlayerPrefab = CreateNetworkPlayerPrefab();
        UpdateDefaultNetworkPrefabList(networkPlayerPrefab);
        UpdateSampleScenePlayerPrefab(networkPlayerPrefab);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PlayableCharacterPrefabGenerator] Generated playable character prefabs and updated NetworkManager PlayerPrefab.");
    }

    private static GameObject CreateVisualCharacterPrefab(CharacterPrefabDefinition definition)
    {
        // Create a reusable visual-only prefab from one FBX model and its generated AnimatorController.
        GameObject root = new(definition.PrefabName);
        GameObject modelInstance = CreateConfiguredModelInstance(definition, root.transform);
        modelInstance.name = "Model";
        CreateChild(root.transform, "EquipPoint", Vector3.zero);
        CreateChild(root.transform, "EffectPoint_Spark", definition.SparkLocalPosition);

        GameObject savedPrefab = SavePrefab(root, definition.OutputPrefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        return savedPrefab;
    }

    private static GameObject CreateNetworkPlayerPrefab()
    {
        // Duplicate the current working network player prefab, replace runtime visual loading with an editable Bangae child, and assign anchor references.
        GameObject sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SourceNetworkPlayerPrefabPath);
        if (sourcePrefab == null)
        {
            throw new InvalidOperationException($"Source network player prefab not found at {SourceNetworkPlayerPrefabPath}.");
        }

        GameObject playerRoot = PrefabUtility.InstantiatePrefab(sourcePrefab) as GameObject;
        if (playerRoot == null)
        {
            throw new InvalidOperationException($"Could not instantiate source network player prefab at {SourceNetworkPlayerPrefabPath}.");
        }

        PrefabUtility.UnpackPrefabInstance(playerRoot, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        playerRoot.name = "NetworkPlayer_Bangae";
        RemoveRuntimeVisualLoader(playerRoot);
        DestroyChildIfExists(playerRoot.transform, "VisualRoot");
        DestroyChildIfExists(playerRoot.transform, "Anchors");
        DestroyChildIfExists(playerRoot.transform, "BangaePlayableVisual");

        Transform visualRoot = CreateChild(playerRoot.transform, "VisualRoot", Vector3.zero);
        CharacterPrefabDefinition bangaeDefinition = new(
            "Bangae_PlayableCharacter",
            "Assets/Resources/fbx/Bangae_Playable.fbx",
            "Assets/Resources/PlayableCharacters/Bangae_Playable.controller",
            BangaeVisualPrefabPath,
            new Vector3(0f, -0.5f, 0f),
            new Vector3(1.5f, 1.5f, 1.5f),
            new Vector3(0f, 0.346f, 0f));
        GameObject modelInstance = CreateConfiguredModelInstance(bangaeDefinition, visualRoot);
        modelInstance.name = "BangaePlayableVisual";
        Transform lowHealthSparkPoint = CreateChild(visualRoot, "EffectPoint_Spark", bangaeDefinition.SparkLocalPosition);

        Transform anchorsRoot = CreateChild(playerRoot.transform, "Anchors", Vector3.zero);
        Transform projectileMuzzle = CreateChild(anchorsRoot, "ProjectileMuzzle", new Vector3(0.25f, 0.75f, 0.35f));
        Transform hookMuzzle = CreateChild(anchorsRoot, "HookMuzzle", new Vector3(0.15f, 0.65f, 0.3f));
        CreateChild(anchorsRoot, "HitEffectPoint", new Vector3(0f, 1f, 0f));

        AssignPrefabReferences(playerRoot, projectileMuzzle, hookMuzzle, lowHealthSparkPoint, modelInstance);

        GameObject savedPrefab = SavePrefab(playerRoot, NetworkPlayerPrefabPath);
        RefreshNetworkObjectHash(savedPrefab);
        UnityEngine.Object.DestroyImmediate(playerRoot);
        return savedPrefab;
    }

    private static GameObject CreateConfiguredModelInstance(CharacterPrefabDefinition definition, Transform parent)
    {
        // Instantiate a model under a prefab root and configure its Animator for gameplay-driven animation.
        GameObject modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(definition.ModelPath);
        if (modelPrefab == null)
        {
            throw new InvalidOperationException($"Model prefab not found at {definition.ModelPath}.");
        }

        RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(definition.AnimatorControllerPath);
        if (controller == null)
        {
            throw new InvalidOperationException($"AnimatorController not found at {definition.AnimatorControllerPath}.");
        }

        GameObject modelInstance = PrefabUtility.InstantiatePrefab(modelPrefab) as GameObject;
        if (modelInstance == null)
        {
            throw new InvalidOperationException($"Could not instantiate model prefab at {definition.ModelPath}.");
        }

        modelInstance.transform.SetParent(parent, false);
        modelInstance.transform.localPosition = definition.LocalPosition;
        modelInstance.transform.localRotation = Quaternion.identity;
        modelInstance.transform.localScale = definition.LocalScale;

        Animator animator = ResolveOrAddAnimator(modelInstance);
        animator.runtimeAnimatorController = controller;
        AssignAvatarIfNeeded(animator, definition.ModelPath);
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        return modelInstance;
    }

    private static Animator ResolveOrAddAnimator(GameObject modelInstance)
    {
        // Reuse an imported Animator when present, otherwise add one to the model root.
        Animator animator = modelInstance.GetComponentInChildren<Animator>(true);
        if (animator != null)
        {
            return animator;
        }

        return modelInstance.AddComponent<Animator>();
    }

    private static void AssignAvatarIfNeeded(Animator animator, string modelPath)
    {
        // Assign the FBX Avatar sub-asset if the instantiated model did not already carry it.
        if (animator == null || animator.avatar != null)
        {
            return;
        }

        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(modelPath);
        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] is Avatar avatar)
            {
                animator.avatar = avatar;
                return;
            }
        }
    }

    private static void AssignPrefabReferences(
        GameObject playerRoot,
        Transform projectileMuzzle,
        Transform hookMuzzle,
        Transform lowHealthSparkPoint,
        GameObject modelInstance)
    {
        // Wire gameplay scripts to the editable prefab anchors and direct child Animator.
        PlayerProjectileShooter projectileShooter = playerRoot.GetComponent<PlayerProjectileShooter>();
        AssignObjectReference(projectileShooter, "muzzleTransform", projectileMuzzle);

        PlayerEquipmentHookShooter hookShooter = playerRoot.GetComponent<PlayerEquipmentHookShooter>();
        AssignObjectReference(hookShooter, "muzzleTransform", hookMuzzle);

        NetworkPlayerCombatState combatState = playerRoot.GetComponent<NetworkPlayerCombatState>();
        AssignObjectReference(combatState, "lowHealthSparkPrefab", LoadParticleSystemPrefab(LowHealthSparkPrefabPath));
        AssignObjectReference(combatState, "lowHealthSparkAnchor", lowHealthSparkPoint);
        AssignObjectReference(combatState, "damageHitEffectPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(DamageHitEffectPrefabPath));
        AssignObjectReference(combatState, "equipmentBreakExplosionPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(BreakExplosionEffectPrefabPath));

        PlayableCharacterAnimationDriver animationDriver = playerRoot.GetComponent<PlayableCharacterAnimationDriver>();
        AssignObjectReference(animationDriver, "animator", modelInstance.GetComponentInChildren<Animator>(true));
    }

    private static void RemoveRuntimeVisualLoader(GameObject playerRoot)
    {
        // Remove the runtime Resources visual loader because the generated prefab now owns its editable visual hierarchy.
        PlayableCharacterVisualLoader visualLoader = playerRoot.GetComponent<PlayableCharacterVisualLoader>();
        if (visualLoader != null)
        {
            UnityEngine.Object.DestroyImmediate(visualLoader);
        }
    }

    private static void AssignObjectReference(UnityEngine.Object target, string propertyName, UnityEngine.Object value)
    {
        // Assign private serialized object references without changing script field visibility.
        if (target == null)
        {
            Debug.LogWarning($"[PlayableCharacterPrefabGenerator] Cannot assign {propertyName} because the target component is missing.");
            return;
        }

        SerializedObject serializedObject = new(target);
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        if (property == null)
        {
            Debug.LogWarning($"[PlayableCharacterPrefabGenerator] Serialized property '{propertyName}' not found on {target.name}.");
            return;
        }

        property.objectReferenceValue = value;
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(target);
    }

    private static ParticleSystem LoadParticleSystemPrefab(string prefabPath)
    {
        // Resolve a particle-system component from a prefab asset so VFX references survive prefab regeneration.
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        return prefab != null ? prefab.GetComponentInChildren<ParticleSystem>(true) : null;
    }

    private static Transform CreateChild(Transform parent, string name, Vector3 localPosition)
    {
        // Create a named transform child with predictable local placement for later prefab tuning.
        GameObject child = new(name);
        child.transform.SetParent(parent, false);
        child.transform.localPosition = localPosition;
        child.transform.localRotation = Quaternion.identity;
        child.transform.localScale = Vector3.one;
        return child.transform;
    }

    private static void DestroyChildIfExists(Transform parent, string childName)
    {
        // Remove an old generated child before rebuilding a deterministic prefab hierarchy.
        Transform child = parent.Find(childName);
        if (child != null)
        {
            UnityEngine.Object.DestroyImmediate(child.gameObject);
        }
    }

    private static GameObject SavePrefab(GameObject root, string prefabPath)
    {
        // Save a prefab asset from a temporary hierarchy, replacing the previous generated asset if present.
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
        {
            AssetDatabase.DeleteAsset(prefabPath);
        }

        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        if (savedPrefab == null)
        {
            throw new InvalidOperationException($"Failed to save prefab at {prefabPath}.");
        }

        return savedPrefab;
    }

    private static void RefreshNetworkObjectHash(GameObject networkPlayerPrefab)
    {
        // Force Netcode's prefab hash to match the generated asset path instead of the duplicated source prefab.
        if (networkPlayerPrefab == null || !networkPlayerPrefab.TryGetComponent(out NetworkObject networkObject))
        {
            return;
        }

        GlobalObjectId globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(networkObject);
        if (globalObjectId.identifierType == 0)
        {
            return;
        }

        SerializedObject serializedObject = new(networkObject);
        SerializedProperty hashProperty = serializedObject.FindProperty("GlobalObjectIdHash");
        if (hashProperty == null)
        {
            Debug.LogWarning("[PlayableCharacterPrefabGenerator] NetworkObject GlobalObjectIdHash property was not found.");
            return;
        }

        hashProperty.longValue = ComputeNetcodeHash32(globalObjectId.ToString());
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(networkObject);
        EditorUtility.SetDirty(networkPlayerPrefab);
    }

    private static uint ComputeNetcodeHash32(string value)
    {
        // Match Netcode's XXHash32 string hashing without depending on its internal hashing helper.
        return ComputeXxHash32(Encoding.UTF8.GetBytes(value));
    }

    private static uint ComputeXxHash32(byte[] data, uint seed = 0)
    {
        // Calculate the same 32-bit XXHash variant used by Netcode for GlobalObjectIdHash values.
        const uint prime1 = 2654435761u;
        const uint prime2 = 2246822519u;
        const uint prime3 = 3266489917u;
        const uint prime4 = 668265263u;
        const uint prime5 = 374761393u;

        int length = data != null ? data.Length : 0;
        int index = 0;
        uint hash;

        if (length >= 16)
        {
            uint value1 = seed + prime1 + prime2;
            uint value2 = seed + prime2;
            uint value3 = seed;
            uint value4 = seed - prime1;
            int limit = length - 16;

            do
            {
                value1 = RoundXxHash(value1, ReadUInt32(data, index));
                index += 4;
                value2 = RoundXxHash(value2, ReadUInt32(data, index));
                index += 4;
                value3 = RoundXxHash(value3, ReadUInt32(data, index));
                index += 4;
                value4 = RoundXxHash(value4, ReadUInt32(data, index));
                index += 4;
            }
            while (index <= limit);

            hash = RotateLeft(value1, 1) +
                RotateLeft(value2, 7) +
                RotateLeft(value3, 12) +
                RotateLeft(value4, 18);
        }
        else
        {
            hash = seed + prime5;
        }

        hash += (uint)length;

        while (index <= length - 4)
        {
            hash += ReadUInt32(data, index) * prime3;
            hash = RotateLeft(hash, 17) * prime4;
            index += 4;
        }

        while (index < length)
        {
            hash += data[index] * prime5;
            hash = RotateLeft(hash, 11) * prime1;
            index++;
        }

        hash ^= hash >> 15;
        hash *= prime2;
        hash ^= hash >> 13;
        hash *= prime3;
        hash ^= hash >> 16;
        return hash;
    }

    private static uint RoundXxHash(uint hash, uint input)
    {
        // Mix one 32-bit lane of input into the running XXHash accumulator.
        const uint prime1 = 2654435761u;
        const uint prime2 = 2246822519u;

        hash += input * prime2;
        hash = RotateLeft(hash, 13);
        hash *= prime1;
        return hash;
    }

    private static uint ReadUInt32(byte[] data, int index)
    {
        // Read a little-endian 32-bit value from a managed byte array.
        return (uint)data[index] |
            ((uint)data[index + 1] << 8) |
            ((uint)data[index + 2] << 16) |
            ((uint)data[index + 3] << 24);
    }

    private static uint RotateLeft(uint value, int count)
    {
        // Rotate bits left using the same wraparound behavior as the Netcode hashing helper.
        return (value << count) | (value >> (32 - count));
    }

    private static void UpdateDefaultNetworkPrefabList(GameObject networkPlayerPrefab)
    {
        // Keep the default network prefab list in sync with the NetworkManager PlayerPrefab reference.
        UnityEngine.Object prefabList = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(DefaultNetworkPrefabsPath);
        if (prefabList == null)
        {
            Debug.LogWarning($"[PlayableCharacterPrefabGenerator] Default network prefab list not found at {DefaultNetworkPrefabsPath}.");
            return;
        }

        SerializedObject serializedObject = new(prefabList);
        SerializedProperty listProperty = serializedObject.FindProperty("List");
        if (listProperty == null)
        {
            Debug.LogWarning("[PlayableCharacterPrefabGenerator] Default network prefab list has no serialized List property.");
            return;
        }

        if (listProperty.arraySize == 0)
        {
            listProperty.InsertArrayElementAtIndex(0);
        }

        while (listProperty.arraySize > 1)
        {
            listProperty.DeleteArrayElementAtIndex(listProperty.arraySize - 1);
        }

        SerializedProperty element = listProperty.GetArrayElementAtIndex(0);
        AssignRelativeBool(element, "Override", false);
        AssignRelativeObject(element, "Prefab", networkPlayerPrefab);
        AssignRelativeObject(element, "SourcePrefabToOverride", null);
        AssignRelativeObject(element, "OverridingTargetPrefab", null);
        AssignRelativeLong(element, "SourceHashToOverride", 0);

        serializedObject.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(prefabList);
    }

    private static void UpdateSampleScenePlayerPrefab(GameObject networkPlayerPrefab)
    {
        // Open SampleScene and point its NetworkManager at the generated editable network player prefab.
        Scene scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        NetworkManager networkManager = UnityEngine.Object.FindFirstObjectByType<NetworkManager>();
        if (networkManager == null)
        {
            throw new InvalidOperationException($"NetworkManager not found in {SampleScenePath}.");
        }

        SerializedObject serializedObject = new(networkManager);
        SerializedProperty playerPrefabProperty = serializedObject.FindProperty("NetworkConfig.PlayerPrefab");
        if (playerPrefabProperty == null)
        {
            throw new InvalidOperationException("NetworkManager serialized PlayerPrefab property was not found.");
        }

        playerPrefabProperty.objectReferenceValue = networkPlayerPrefab;
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(networkManager);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static void AssignRelativeBool(SerializedProperty parentProperty, string relativeName, bool value)
    {
        // Assign a bool child property when it exists on the network prefab list entry.
        SerializedProperty property = parentProperty.FindPropertyRelative(relativeName);
        if (property != null)
        {
            property.boolValue = value;
        }
    }

    private static void AssignRelativeObject(SerializedProperty parentProperty, string relativeName, UnityEngine.Object value)
    {
        // Assign an object reference child property when it exists on the network prefab list entry.
        SerializedProperty property = parentProperty.FindPropertyRelative(relativeName);
        if (property != null)
        {
            property.objectReferenceValue = value;
        }
    }

    private static void AssignRelativeLong(SerializedProperty parentProperty, string relativeName, long value)
    {
        // Assign an integer child property when it exists on the network prefab list entry.
        SerializedProperty property = parentProperty.FindPropertyRelative(relativeName);
        if (property != null)
        {
            property.longValue = value;
        }
    }

    private static void EnsureOutputFolder()
    {
        // Create the Resources playable-character output path when it does not already exist.
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
        {
            AssetDatabase.CreateFolder("Assets", "Resources");
        }

        if (!AssetDatabase.IsValidFolder(OutputFolder))
        {
            AssetDatabase.CreateFolder("Assets/Resources", "PlayableCharacters");
        }
    }

    private readonly struct CharacterPrefabDefinition
    {
        public readonly string PrefabName;
        public readonly string ModelPath;
        public readonly string AnimatorControllerPath;
        public readonly string OutputPrefabPath;
        public readonly Vector3 LocalPosition;
        public readonly Vector3 LocalScale;
        public readonly Vector3 SparkLocalPosition;

        public CharacterPrefabDefinition(
            string prefabName,
            string modelPath,
            string animatorControllerPath,
            string outputPrefabPath,
            Vector3 localPosition,
            Vector3 localScale,
            Vector3 sparkLocalPosition)
        {
            // Store the source assets and transform defaults needed to generate one character prefab.
            PrefabName = prefabName;
            ModelPath = modelPath;
            AnimatorControllerPath = animatorControllerPath;
            OutputPrefabPath = outputPrefabPath;
            LocalPosition = localPosition;
            LocalScale = localScale;
            SparkLocalPosition = sparkLocalPosition;
        }
    }
}
