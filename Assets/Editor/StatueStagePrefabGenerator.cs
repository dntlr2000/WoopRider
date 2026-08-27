using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class StatueStagePrefabGenerator
{
    private const string ModelPath = "Assets/Resources/fbx/Structures/StatueStage.fbx";
    private const string OutputFolder = "Assets/Resources/Stages";
    private const string CollisionMeshFolder = OutputFolder + "/CollisionMeshes";
    private const string PrefabPath = OutputFolder + "/StatueStage.prefab";
    private const string VisualRootName = "Visual";
    private const string CollisionRootName = "Collision";
    private const string GroundRootName = "Ground";
    private const string WallRootName = "Wall";
    private const string GroundLayerName = "StageGround";
    private const string WallLayerName = "StageWall";
    private const string GroundTag = "Ground";
    private const string UntaggedTag = "Untagged";
    private const float MaxGroundAngle = 55f;

    private enum SurfaceKind
    {
        Ground,
        Wall
    }

    private static readonly string[] ColliderSourceNames =
    {
        "Base_1F",
        "Base_2F"
    };

    private static readonly string[] ExcludedObjectNames =
    {
        "Cube",
        "Camera",
        "Light"
    };

    [MenuItem("Tools/WoopRider/Generate Statue Stage Prefab")]
    public static void Generate()
    {
        // Generate a StatueStage prefab with separately layered Ground and Wall collision meshes.
        EnsureOutputFolders();
        int groundLayer = GetRequiredLayer(GroundLayerName);
        int wallLayer = GetRequiredLayer(WallLayerName);
        bool restoreModelReadability = ConfigureModelImporterForGeneration();

        GameObject prefabRoot = null;
        try
        {
            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (modelAsset == null)
            {
                throw new InvalidOperationException($"Model asset not found at {ModelPath}.");
            }

            prefabRoot = new GameObject("StatueStage");
            GameObject visualRoot = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
            if (visualRoot == null)
            {
                throw new InvalidOperationException($"Could not instantiate model asset at {ModelPath}.");
            }

            visualRoot.name = VisualRootName;
            visualRoot.transform.SetParent(prefabRoot.transform, false);
            ResetLocalTransform(visualRoot.transform);
            RemoveExcludedObjects(visualRoot);

            GameObject collisionRoot = CreateHierarchyRoot(
                prefabRoot.transform,
                CollisionRootName,
                0,
                UntaggedTag);
            GameObject groundRoot = CreateHierarchyRoot(
                collisionRoot.transform,
                GroundRootName,
                groundLayer,
                GroundTag);
            GameObject wallRoot = CreateHierarchyRoot(
                collisionRoot.transform,
                WallRootName,
                wallLayer,
                UntaggedTag);

            for (int i = 0; i < ColliderSourceNames.Length; i++)
            {
                Transform sourceTransform = FindDescendant(visualRoot.transform, ColliderSourceNames[i]);
                if (sourceTransform == null)
                {
                    throw new InvalidOperationException(
                        $"Could not find collider source '{ColliderSourceNames[i]}' in {ModelPath}.");
                }

                MeshFilter sourceMeshFilter = GetRequiredMeshFilter(sourceTransform);
                Mesh groundMesh = CreateOrUpdateSurfaceMesh(
                    sourceMeshFilter.sharedMesh,
                    sourceTransform,
                    SurfaceKind.Ground);
                Mesh wallMesh = CreateOrUpdateSurfaceMesh(
                    sourceMeshFilter.sharedMesh,
                    sourceTransform,
                    SurfaceKind.Wall);

                CreateCollisionObject(
                    sourceTransform,
                    visualRoot.transform,
                    groundRoot.transform,
                    groundMesh,
                    SurfaceKind.Ground,
                    groundLayer,
                    GroundTag);
                CreateCollisionObject(
                    sourceTransform,
                    visualRoot.transform,
                    wallRoot.transform,
                    wallMesh,
                    SurfaceKind.Wall,
                    wallLayer,
                    UntaggedTag);
            }

            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(prefabRoot, PrefabPath);
            if (savedPrefab == null)
            {
                throw new InvalidOperationException($"Failed to save prefab at {PrefabPath}.");
            }

            AssetDatabase.SaveAssets();
        }
        finally
        {
            if (prefabRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(prefabRoot);
            }

            RestoreModelReadability(restoreModelReadability);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        ValidateGeneratedPrefab();
        Debug.Log(
            $"[StatueStagePrefabGenerator] Generated layered collider prefab at {PrefabPath} " +
            $"with a {MaxGroundAngle:0.#}-degree ground threshold.");
    }

    private static void EnsureOutputFolders()
    {
        // Create the requested Resources and collision-mesh output folders when they are missing.
        EnsureFolder("Assets", "Resources");
        EnsureFolder("Assets/Resources", "Stages");
        EnsureFolder(OutputFolder, "CollisionMeshes");
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

    private static int GetRequiredLayer(string layerName)
    {
        // Resolve one required physics layer and fail clearly when the project layer is absent.
        int layer = LayerMask.NameToLayer(layerName);
        if (layer < 0)
        {
            throw new InvalidOperationException(
                $"Required Unity layer '{layerName}' is not configured in ProjectSettings/TagManager.asset.");
        }

        return layer;
    }

    private static bool ConfigureModelImporterForGeneration()
    {
        // Prepare the FBX for editor-time mesh splitting while keeping unused imports disabled.
        ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"Model importer not found at {ModelPath}.");
        }

        bool restoreReadability = !importer.isReadable;
        bool changed = false;
        if (importer.importCameras)
        {
            importer.importCameras = false;
            changed = true;
        }

        if (importer.importLights)
        {
            importer.importLights = false;
            changed = true;
        }

        if (importer.importAnimation)
        {
            importer.importAnimation = false;
            changed = true;
        }

        if (importer.addCollider)
        {
            importer.addCollider = false;
            changed = true;
        }

        if (!importer.isReadable)
        {
            importer.isReadable = true;
            changed = true;
        }

        if (changed)
        {
            importer.SaveAndReimport();
        }

        return restoreReadability;
    }

    private static void RestoreModelReadability(bool shouldRestore)
    {
        // Restore the FBX Read/Write setting after persistent collision meshes have been generated.
        if (!shouldRestore)
        {
            return;
        }

        ModelImporter importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer != null && importer.isReadable)
        {
            importer.isReadable = false;
            importer.SaveAndReimport();
        }
    }

    private static GameObject CreateHierarchyRoot(
        Transform parent,
        string objectName,
        int layer,
        string tagName)
    {
        // Create one named hierarchy node with a stable local transform, physics layer, and tag.
        GameObject hierarchyRoot = new GameObject(objectName)
        {
            layer = layer,
            tag = tagName
        };
        hierarchyRoot.transform.SetParent(parent, false);
        ResetLocalTransform(hierarchyRoot.transform);
        return hierarchyRoot;
    }

    private static void ResetLocalTransform(Transform target)
    {
        // Reset one generated hierarchy transform without changing its parent relationship.
        target.localPosition = Vector3.zero;
        target.localRotation = Quaternion.identity;
        target.localScale = Vector3.one;
    }

    private static void RemoveExcludedObjects(GameObject visualRoot)
    {
        // Remove Blender reference objects that are not part of the playable stage visual.
        Transform[] descendants = visualRoot.GetComponentsInChildren<Transform>(true);
        for (int i = descendants.Length - 1; i >= 0; i--)
        {
            Transform descendant = descendants[i];
            if (descendant == visualRoot.transform ||
                !ExcludedObjectNames.Contains(descendant.name, StringComparer.Ordinal))
            {
                continue;
            }

            UnityEngine.Object.DestroyImmediate(descendant.gameObject);
        }
    }

    private static Transform FindDescendant(Transform root, string objectName)
    {
        // Find one active or inactive descendant by its exact imported FBX object name.
        return root.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(candidate => candidate != root && candidate.name == objectName);
    }

    private static MeshFilter GetRequiredMeshFilter(Transform sourceTransform)
    {
        // Resolve the readable source mesh used to generate collision-only surface assets.
        MeshFilter sourceMeshFilter = sourceTransform.GetComponent<MeshFilter>();
        if (sourceMeshFilter == null || sourceMeshFilter.sharedMesh == null)
        {
            throw new InvalidOperationException(
                $"Collider source '{sourceTransform.name}' does not contain a valid MeshFilter.");
        }

        return sourceMeshFilter;
    }

    private static Mesh CreateOrUpdateSurfaceMesh(
        Mesh sourceMesh,
        Transform sourceTransform,
        SurfaceKind surfaceKind)
    {
        // Split source triangles into Ground or Wall geometry using their transformed face normals.
        Vector3[] sourceVertices = sourceMesh.vertices;
        int[] sourceTriangles = sourceMesh.triangles;
        Dictionary<int, int> vertexRemap = new();
        List<Vector3> surfaceVertices = new();
        List<int> surfaceTriangles = new();

        for (int triangleIndex = 0; triangleIndex < sourceTriangles.Length; triangleIndex += 3)
        {
            int firstIndex = sourceTriangles[triangleIndex];
            int secondIndex = sourceTriangles[triangleIndex + 1];
            int thirdIndex = sourceTriangles[triangleIndex + 2];
            Vector3 worldNormal = CalculateWorldNormal(
                sourceVertices[firstIndex],
                sourceVertices[secondIndex],
                sourceVertices[thirdIndex],
                sourceTransform);
            bool isGround = IsGroundNormal(worldNormal);
            if ((surfaceKind == SurfaceKind.Ground) != isGround)
            {
                continue;
            }

            for (int vertexOffset = 0; vertexOffset < 3; vertexOffset++)
            {
                int sourceIndex = sourceTriangles[triangleIndex + vertexOffset];
                if (!vertexRemap.TryGetValue(sourceIndex, out int surfaceIndex))
                {
                    surfaceIndex = surfaceVertices.Count;
                    vertexRemap[sourceIndex] = surfaceIndex;
                    surfaceVertices.Add(sourceVertices[sourceIndex]);
                }

                surfaceTriangles.Add(surfaceIndex);
            }
        }

        if (surfaceTriangles.Count == 0)
        {
            throw new InvalidOperationException(
                $"Generated {surfaceKind} surface for '{sourceTransform.name}' contains no triangles.");
        }

        string outputPath = GetCollisionMeshPath(sourceTransform.name, surfaceKind);
        Mesh surfaceMesh = AssetDatabase.LoadAssetAtPath<Mesh>(outputPath);
        if (surfaceMesh == null)
        {
            surfaceMesh = new Mesh();
            AssetDatabase.CreateAsset(surfaceMesh, outputPath);
        }
        else
        {
            surfaceMesh.Clear();
        }

        surfaceMesh.name = System.IO.Path.GetFileNameWithoutExtension(outputPath);
        surfaceMesh.indexFormat = surfaceVertices.Count > ushort.MaxValue
            ? IndexFormat.UInt32
            : IndexFormat.UInt16;
        surfaceMesh.SetVertices(surfaceVertices);
        surfaceMesh.SetTriangles(surfaceTriangles, 0, true);
        surfaceMesh.RecalculateNormals();
        surfaceMesh.RecalculateBounds();
        EditorUtility.SetDirty(surfaceMesh);

        Debug.Log(
            $"[StatueStagePrefabGenerator] Generated {surfaceMesh.name}: " +
            $"{surfaceTriangles.Count / 3} triangles, {surfaceVertices.Count} vertices.");
        return surfaceMesh;
    }

    private static string GetCollisionMeshPath(string sourceName, SurfaceKind surfaceKind)
    {
        // Return the stable asset path for one generated stage collision surface.
        return $"{CollisionMeshFolder}/StatueStage_{sourceName}_{surfaceKind}.asset";
    }

    private static Vector3 CalculateWorldNormal(
        Vector3 first,
        Vector3 second,
        Vector3 third,
        Transform surfaceTransform)
    {
        // Calculate one geometric triangle normal in stage space without relying on smoothed normals.
        Vector3 localNormal = Vector3.Cross(second - first, third - first);
        if (localNormal.sqrMagnitude <= Mathf.Epsilon)
        {
            throw new InvalidOperationException("StatueStage contains a degenerate collision triangle.");
        }

        Matrix4x4 normalMatrix = surfaceTransform.localToWorldMatrix.inverse.transpose;
        return normalMatrix.MultiplyVector(localNormal.normalized).normalized;
    }

    private static bool IsGroundNormal(Vector3 worldNormal)
    {
        // Treat upward-facing surfaces at or below the configured slope angle as playable ground.
        float minimumGroundDot = Mathf.Cos(MaxGroundAngle * Mathf.Deg2Rad);
        return Vector3.Dot(worldNormal, Vector3.up) >= minimumGroundDot;
    }

    private static void CreateCollisionObject(
        Transform sourceTransform,
        Transform visualRoot,
        Transform surfaceRoot,
        Mesh collisionMesh,
        SurfaceKind surfaceKind,
        int layer,
        string tagName)
    {
        // Create one non-rendered MeshCollider object aligned with its imported visual source.
        if (sourceTransform.parent != visualRoot)
        {
            throw new InvalidOperationException(
                $"Collider source '{sourceTransform.name}' is not a direct child of the FBX root.");
        }

        GameObject collisionObject = new GameObject(sourceTransform.name + "_" + surfaceKind)
        {
            layer = layer,
            tag = tagName
        };
        collisionObject.transform.SetParent(surfaceRoot, false);
        CopyLocalTransform(sourceTransform, collisionObject.transform);

        MeshCollider meshCollider = collisionObject.AddComponent<MeshCollider>();
        meshCollider.sharedMesh = collisionMesh;
        meshCollider.convex = false;
        meshCollider.isTrigger = false;
    }

    private static void CopyLocalTransform(Transform source, Transform destination)
    {
        // Copy the imported mesh transform so collision geometry aligns with its visual source.
        destination.localPosition = source.localPosition;
        destination.localRotation = source.localRotation;
        destination.localScale = source.localScale;
    }

    private static void ValidateGeneratedPrefab()
    {
        // Verify visual cleanup, surface layers, triangle partitioning, slopes, and collider alignment.
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            throw new InvalidOperationException($"Generated prefab was not found at {PrefabPath}.");
        }

        int groundLayer = GetRequiredLayer(GroundLayerName);
        int wallLayer = GetRequiredLayer(WallLayerName);
        Transform visualRoot = prefab.transform.Find(VisualRootName);
        Transform collisionRoot = prefab.transform.Find(CollisionRootName);
        Transform groundRoot = collisionRoot != null ? collisionRoot.Find(GroundRootName) : null;
        Transform wallRoot = collisionRoot != null ? collisionRoot.Find(WallRootName) : null;
        if (visualRoot == null || collisionRoot == null || groundRoot == null || wallRoot == null)
        {
            throw new InvalidOperationException(
                "StatueStage prefab is missing its Visual, Collision, Ground, or Wall root.");
        }

        if (groundRoot.gameObject.layer != groundLayer ||
            wallRoot.gameObject.layer != wallLayer ||
            !groundRoot.CompareTag(GroundTag))
        {
            throw new InvalidOperationException("StatueStage Ground or Wall hierarchy layer is incorrect.");
        }

        if (prefab.GetComponentsInChildren<Camera>(true).Length > 0 ||
            prefab.GetComponentsInChildren<Light>(true).Length > 0 ||
            ExcludedObjectNames.Any(name => FindDescendant(visualRoot, name) != null))
        {
            throw new InvalidOperationException("StatueStage prefab still contains excluded FBX objects.");
        }

        MeshFilter[] visualMeshes = visualRoot.GetComponentsInChildren<MeshFilter>(true);
        if (visualMeshes.Length != ColliderSourceNames.Length ||
            visualMeshes.Any(meshFilter =>
                !ColliderSourceNames.Contains(meshFilter.gameObject.name, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "StatueStage visual hierarchy contains an unexpected mesh such as the Blender reference Cube.");
        }

        if (prefab.GetComponentInChildren<Rigidbody>(true) != null)
        {
            throw new InvalidOperationException("Static StatueStage prefab must not contain a Rigidbody.");
        }

        MeshCollider[] colliders = prefab.GetComponentsInChildren<MeshCollider>(true);
        int expectedColliderCount = ColliderSourceNames.Length * 2;
        if (colliders.Length != expectedColliderCount ||
            colliders.Any(collider => !collider.transform.IsChildOf(collisionRoot)))
        {
            throw new InvalidOperationException(
                $"Expected {expectedColliderCount} layered colliders but found {colliders.Length}.");
        }

        int totalSlopedGroundTriangles = 0;
        List<string> triangleSummaries = new();
        for (int i = 0; i < ColliderSourceNames.Length; i++)
        {
            string sourceName = ColliderSourceNames[i];
            Transform sourceTransform = FindDescendant(visualRoot, sourceName);
            MeshFilter sourceMeshFilter = sourceTransform != null
                ? sourceTransform.GetComponent<MeshFilter>()
                : null;
            if (sourceMeshFilter == null || sourceMeshFilter.sharedMesh == null)
            {
                throw new InvalidOperationException($"Visual source '{sourceName}' is missing its mesh.");
            }

            Mesh groundMesh = AssetDatabase.LoadAssetAtPath<Mesh>(
                GetCollisionMeshPath(sourceName, SurfaceKind.Ground));
            Mesh wallMesh = AssetDatabase.LoadAssetAtPath<Mesh>(
                GetCollisionMeshPath(sourceName, SurfaceKind.Wall));
            MeshCollider groundCollider = ValidateSurfaceCollider(
                sourceTransform,
                groundRoot,
                groundMesh,
                SurfaceKind.Ground,
                groundLayer,
                GroundTag);
            MeshCollider wallCollider = ValidateSurfaceCollider(
                sourceTransform,
                wallRoot,
                wallMesh,
                SurfaceKind.Wall,
                wallLayer,
                UntaggedTag);

            ulong sourceIndexCount = GetTotalIndexCount(sourceMeshFilter.sharedMesh);
            ulong groundIndexCount = GetTotalIndexCount(groundMesh);
            ulong wallIndexCount = GetTotalIndexCount(wallMesh);
            if (sourceIndexCount != groundIndexCount + wallIndexCount)
            {
                throw new InvalidOperationException(
                    $"Ground and Wall meshes for '{sourceName}' do not preserve every source triangle.");
            }

            totalSlopedGroundTriangles += ValidateSurfaceTriangles(
                groundMesh,
                groundCollider.transform,
                SurfaceKind.Ground);
            ValidateSurfaceTriangles(wallMesh, wallCollider.transform, SurfaceKind.Wall);
            triangleSummaries.Add(
                $"{sourceName}: ground={groundIndexCount / 3}, wall={wallIndexCount / 3}");
        }

        if (totalSlopedGroundTriangles == 0)
        {
            throw new InvalidOperationException("No StatueStage ramp triangles were classified as Ground.");
        }

        Debug.Log(
            "[StatueStagePrefabGenerator] Validation passed: " +
            string.Join(", ", triangleSummaries) +
            $", sloped ground triangles={totalSlopedGroundTriangles}, no Rigidbody.");
    }

    private static MeshCollider ValidateSurfaceCollider(
        Transform sourceTransform,
        Transform surfaceRoot,
        Mesh expectedMesh,
        SurfaceKind surfaceKind,
        int expectedLayer,
        string expectedTag)
    {
        // Validate one generated Ground or Wall collider and return it for surface checks.
        Transform colliderTransform = surfaceRoot.Find(sourceTransform.name + "_" + surfaceKind);
        MeshCollider meshCollider = colliderTransform != null
            ? colliderTransform.GetComponent<MeshCollider>()
            : null;
        if (expectedMesh == null || meshCollider == null ||
            !IsSameAsset(expectedMesh, meshCollider.sharedMesh) ||
            meshCollider.convex || meshCollider.isTrigger ||
            meshCollider.gameObject.layer != expectedLayer ||
            !meshCollider.CompareTag(expectedTag))
        {
            throw new InvalidOperationException(
                $"StatueStage {surfaceKind} collider for '{sourceTransform.name}' is configured incorrectly.");
        }

        if (sourceTransform.localPosition != colliderTransform.localPosition ||
            sourceTransform.localRotation != colliderTransform.localRotation ||
            sourceTransform.localScale != colliderTransform.localScale)
        {
            throw new InvalidOperationException(
                $"StatueStage {surfaceKind} collider for '{sourceTransform.name}' is not aligned.");
        }

        return meshCollider;
    }

    private static ulong GetTotalIndexCount(Mesh mesh)
    {
        // Sum every submesh index count without requiring source mesh Read/Write access.
        if (mesh == null)
        {
            return 0;
        }

        ulong total = 0;
        for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
        {
            total += mesh.GetIndexCount(subMeshIndex);
        }

        return total;
    }

    private static int ValidateSurfaceTriangles(
        Mesh surfaceMesh,
        Transform surfaceTransform,
        SurfaceKind expectedKind)
    {
        // Verify every generated triangle remains on its expected side of the slope threshold.
        Vector3[] vertices = surfaceMesh.vertices;
        int[] triangles = surfaceMesh.triangles;
        int slopedGroundTriangles = 0;
        for (int triangleIndex = 0; triangleIndex < triangles.Length; triangleIndex += 3)
        {
            Vector3 normal = CalculateWorldNormal(
                vertices[triangles[triangleIndex]],
                vertices[triangles[triangleIndex + 1]],
                vertices[triangles[triangleIndex + 2]],
                surfaceTransform);
            bool isGround = IsGroundNormal(normal);
            if ((expectedKind == SurfaceKind.Ground) != isGround)
            {
                throw new InvalidOperationException(
                    $"Generated {expectedKind} mesh contains a misclassified triangle.");
            }

            float upDot = Vector3.Dot(normal, Vector3.up);
            if (isGround && upDot < 0.999f)
            {
                slopedGroundTriangles++;
            }
        }

        return slopedGroundTriangles;
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
}
