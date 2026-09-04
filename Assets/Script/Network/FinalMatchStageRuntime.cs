using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class FinalMatchStageRuntime : MonoBehaviour
{
    [Header("Player Spawn Points")]
    [SerializeField] private Transform spawnPointRoot;

    [Header("Statue Spawn Surfaces")]
    [SerializeField] private LayerMask groundLayerMask = 1 << 8;
    [SerializeField] private string requiredGroundTag = "Ground";
    [Range(0f, 1f)]
    [SerializeField] private float minimumGroundNormalY = 0.7f;
    [Min(0.1f)]
    [SerializeField] private float raycastHeight = 2f;
    [Min(0.1f)]
    [SerializeField] private float raycastDistance = 10f;
    [Min(1)]
    [SerializeField] private int randomPositionAttempts = 64;

    [Header("Spawn Clearance")]
    [SerializeField] private LayerMask blockedSpawnLayerMask = (1 << 0) | (1 << 9);
    [Min(0f)]
    [SerializeField] private float blockedSpawnRadius = 2.4f;
    [Min(0f)]
    [SerializeField] private float blockedSpawnHeight = 1.2f;

    private readonly List<Transform> spawnPoints = new();
    private readonly List<Collider> groundColliders = new();
    private readonly List<float> groundColliderWeights = new();
    private float totalGroundColliderWeight;

    private void Awake()
    {
        // Cache authored spawn markers and valid stage-floor colliders after the prefab is instantiated.
        RefreshStageReferences();
    }

    private void OnValidate()
    {
        // Keep stage sampling settings valid while they are edited in the Inspector.
        minimumGroundNormalY = Mathf.Clamp01(minimumGroundNormalY);
        raycastHeight = Mathf.Max(0.1f, raycastHeight);
        raycastDistance = Mathf.Max(0.1f, raycastDistance);
        randomPositionAttempts = Mathf.Max(1, randomPositionAttempts);
        blockedSpawnRadius = Mathf.Max(0f, blockedSpawnRadius);
        blockedSpawnHeight = Mathf.Max(0f, blockedSpawnHeight);
    }

    public int CopySpawnPoints(List<Transform> destination)
    {
        // Copy every active child marker under SpawnPoint in authored sibling order.
        if (destination == null)
        {
            return 0;
        }

        if (spawnPoints.Count == 0)
        {
            RefreshStageReferences();
        }

        destination.Clear();
        for (int i = 0; i < spawnPoints.Count; i++)
        {
            Transform spawnPoint = spawnPoints[i];
            if (spawnPoint != null && spawnPoint.gameObject.activeInHierarchy)
            {
                destination.Add(spawnPoint);
            }
        }

        return destination.Count;
    }

    public bool TryGetRandomGroundPosition(
        float verticalOffset,
        IReadOnlyList<Vector3> occupiedPositions,
        float minimumSpacing,
        out Vector3 position)
    {
        // Sample only this stage's authored ground meshes and reject walls, obstacles, and crowded points.
        position = default;
        if (groundColliders.Count == 0)
        {
            RefreshStageReferences();
        }

        if (groundColliders.Count == 0 || totalGroundColliderWeight <= 0f)
        {
            return false;
        }

        float spacing = Mathf.Max(0f, minimumSpacing);
        for (int attempt = 0; attempt < randomPositionAttempts; attempt++)
        {
            Collider groundCollider = ChooseWeightedGroundCollider();
            if (groundCollider == null)
            {
                continue;
            }

            Bounds bounds = groundCollider.bounds;
            Vector3 rayOrigin = new(
                UnityEngine.Random.Range(bounds.min.x, bounds.max.x),
                bounds.max.y + raycastHeight,
                UnityEngine.Random.Range(bounds.min.z, bounds.max.z));
            float distance = raycastHeight + bounds.size.y + raycastDistance;
            if (!groundCollider.Raycast(new Ray(rayOrigin, Vector3.down), out RaycastHit hit, distance))
            {
                continue;
            }

            if (hit.normal.y < minimumGroundNormalY)
            {
                continue;
            }

            Vector3 candidate = hit.point + Vector3.up * verticalOffset;
            if (!HasMinimumSpacing(candidate, occupiedPositions, spacing) ||
                HasBlockedSpawnArea(candidate, groundCollider))
            {
                continue;
            }

            position = candidate;
            return true;
        }

        return false;
    }

    private void RefreshStageReferences()
    {
        // Rebuild runtime caches from the instantiated prefab without changing its authored transforms or scale.
        ResolveSpawnPointRoot();

        spawnPoints.Clear();
        if (spawnPointRoot != null)
        {
            for (int i = 0; i < spawnPointRoot.childCount; i++)
            {
                spawnPoints.Add(spawnPointRoot.GetChild(i));
            }
        }

        groundColliders.Clear();
        groundColliderWeights.Clear();
        totalGroundColliderWeight = 0f;

        Collider[] stageColliders = GetComponentsInChildren<Collider>(includeInactive: true);
        for (int i = 0; i < stageColliders.Length; i++)
        {
            Collider stageCollider = stageColliders[i];
            if (!IsValidGroundCollider(stageCollider))
            {
                continue;
            }

            Bounds bounds = stageCollider.bounds;
            float weight = Mathf.Max(0.01f, bounds.size.x * bounds.size.z);
            groundColliders.Add(stageCollider);
            groundColliderWeights.Add(weight);
            totalGroundColliderWeight += weight;
        }
    }

    private void ResolveSpawnPointRoot()
    {
        // Resolve the authored SpawnPoint container when no explicit Inspector reference is assigned.
        if (spawnPointRoot != null)
        {
            return;
        }

        spawnPointRoot = transform.Find("SpawnPoint");
        if (spawnPointRoot != null)
        {
            return;
        }

        Transform[] descendants = GetComponentsInChildren<Transform>(includeInactive: true);
        for (int i = 0; i < descendants.Length; i++)
        {
            if (descendants[i] != null && descendants[i].name == "SpawnPoint")
            {
                spawnPointRoot = descendants[i];
                return;
            }
        }
    }

    private bool IsValidGroundCollider(Collider stageCollider)
    {
        // Accept only enabled, non-trigger colliders explicitly authored as this stage's ground.
        if (stageCollider == null || !stageCollider.enabled || stageCollider.isTrigger)
        {
            return false;
        }

        if (!IsLayerInMask(stageCollider.gameObject.layer, groundLayerMask))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(requiredGroundTag) ||
            string.Equals(stageCollider.tag, requiredGroundTag, StringComparison.Ordinal);
    }

    private Collider ChooseWeightedGroundCollider()
    {
        // Pick a floor collider proportionally to its horizontal bounds area.
        float roll = UnityEngine.Random.Range(0f, totalGroundColliderWeight);
        for (int i = 0; i < groundColliders.Count; i++)
        {
            roll -= groundColliderWeights[i];
            if (roll <= 0f)
            {
                return groundColliders[i];
            }
        }

        return groundColliders.Count > 0 ? groundColliders[^1] : null;
    }

    private bool HasBlockedSpawnArea(Vector3 position, Collider selectedGround)
    {
        // Reject points intersecting stage walls, the jump platform, players, or other configured obstacles.
        if (blockedSpawnRadius <= 0f || blockedSpawnLayerMask.value == 0)
        {
            return false;
        }

        Vector3 center = position + Vector3.up * blockedSpawnHeight;
        Collider[] overlaps = Physics.OverlapSphere(
            center,
            blockedSpawnRadius,
            blockedSpawnLayerMask,
            QueryTriggerInteraction.Collide);
        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider overlap = overlaps[i];
            if (overlap != null && overlap != selectedGround)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMinimumSpacing(
        Vector3 candidate,
        IReadOnlyList<Vector3> occupiedPositions,
        float minimumSpacing)
    {
        // Keep newly sampled positions separate from every already reserved player or statue position.
        if (occupiedPositions == null || minimumSpacing <= 0f)
        {
            return true;
        }

        float minimumSpacingSqr = minimumSpacing * minimumSpacing;
        for (int i = 0; i < occupiedPositions.Count; i++)
        {
            if ((candidate - occupiedPositions[i]).sqrMagnitude < minimumSpacingSqr)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLayerInMask(int layer, LayerMask mask)
    {
        // Test one Unity layer index against an Inspector-authored LayerMask.
        return (mask.value & (1 << layer)) != 0;
    }
}
