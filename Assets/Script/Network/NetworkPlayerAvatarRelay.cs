using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayerAvatarRelay : NetworkBehaviour
{
    private const float MaxProjectileDistance = 120f;
    private const float MinProjectileSpeed = 1f;
    private const float MaxProjectileSpeed = 120f;
    private const float MinProjectileRadius = 0.02f;
    private const float MaxProjectileRadius = 1f;
    private const float MinProjectileLifetime = 0.1f;
    private const float MaxProjectileLifetime = 10f;

    [Header("Local Source")]
    [SerializeField] private float sendInterval = 0.05f;
    [SerializeField] private float positionSendThreshold = 0.02f;
    [SerializeField] private float rotationSendThreshold = 1f;

    [Header("Owner Visual")]
    [SerializeField] private bool hideOwnerVisual = true;

    private ThirdPersonController localController;
    private NetworkPlayerEquipmentState equipmentState;
    private Renderer[] renderers;
    private Collider[] colliders;
    private float nextSendTime;
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation = Quaternion.identity;

    private void Awake()
    {
        // Cache temporary network avatar visuals so the owning client can hide them.
        equipmentState = GetComponent<NetworkPlayerEquipmentState>();
        renderers = GetComponentsInChildren<Renderer>(true);
        colliders = GetComponentsInChildren<Collider>(true);
    }

    public override void OnNetworkSpawn()
    {
        // Hide the temporary network avatar only for the owning client.
        SetOwnerVisualVisible(!(hideOwnerVisual && IsOwner));

        if (IsOwner)
        {
            localController = FindFirstObjectByType<ThirdPersonController>();
            if (localController != null)
            {
                SendTransform(force: true);
            }
        }
    }

    private void Update()
    {
        // Owner clients periodically relay their real test character transform to the server.
        if (!IsOwner || !IsClient || Time.time < nextSendTime)
        {
            return;
        }

        nextSendTime = Time.time + sendInterval;
        SendTransform(force: false);
    }

    public bool RequestProjectileVisual(Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime)
    {
        // Let the owning client ask the server to broadcast a temporary projectile visual.
        if (!IsSpawned || !IsOwner)
        {
            return false;
        }

        if (!TryGetProjectileAttackSettings(requireResolvedEquipment: IsServer, out EquipmentAttackSettings attackSettings))
        {
            return false;
        }

        ApplyProjectileEquipmentSettings(attackSettings, origin, ref targetPoint, ref speed, ref radius, ref lifeTime);

        if (!TrySanitizeProjectile(origin, targetPoint, speed, radius, lifeTime, out ProjectilePacket packet))
        {
            return false;
        }

        if (IsServer)
        {
            SpawnProjectileVisualClientRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
            return true;
        }

        SubmitProjectileVisualServerRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
        return true;
    }

    private void SendTransform(bool force)
    {
        // Read ThirdPersonController and send position changes through server-authoritative paths.
        if (localController == null)
        {
            localController = FindFirstObjectByType<ThirdPersonController>();
            if (localController == null)
            {
                return;
            }
        }

        Vector3 position = localController.transform.position;
        Quaternion rotation = localController.transform.rotation;

        if (!force && !ShouldSend(position, rotation))
        {
            return;
        }

        lastSentPosition = position;
        lastSentRotation = rotation;

        if (IsServer)
        {
            ApplyAvatarTransform(position, rotation);
            return;
        }

        SubmitAvatarTransformServerRpc(position, rotation);
    }

    private bool ShouldSend(Vector3 position, Quaternion rotation)
    {
        // Check whether the position or rotation changed enough to send another update.
        float sqrThreshold = positionSendThreshold * positionSendThreshold;
        bool moved = (position - lastSentPosition).sqrMagnitude >= sqrThreshold;
        bool rotated = Quaternion.Angle(rotation, lastSentRotation) >= rotationSendThreshold;
        return moved || rotated;
    }

    [ServerRpc(RequireOwnership = false)]
    private void SubmitAvatarTransformServerRpc(Vector3 position, Quaternion rotation, ServerRpcParams rpcParams = default)
    {
        // Reject avatar updates that do not come from this PlayerObject owner.
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected avatar update sender={rpcParams.Receive.SenderClientId} owner={OwnerClientId}");
            return;
        }

        ApplyAvatarTransform(position, rotation);
    }

    [ServerRpc(RequireOwnership = false)]
    private void SubmitProjectileVisualServerRpc(Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime, ServerRpcParams rpcParams = default)
    {
        // Validate the owner request before rebroadcasting a projectile visual to clients.
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected projectile sender={rpcParams.Receive.SenderClientId} owner={OwnerClientId}");
            return;
        }

        if (!TryGetProjectileAttackSettings(requireResolvedEquipment: true, out EquipmentAttackSettings attackSettings))
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected projectile without valid projectile equipment sender={rpcParams.Receive.SenderClientId}");
            return;
        }

        ApplyProjectileEquipmentSettings(attackSettings, origin, ref targetPoint, ref speed, ref radius, ref lifeTime);

        if (!TrySanitizeProjectile(origin, targetPoint, speed, radius, lifeTime, out ProjectilePacket packet))
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected invalid projectile sender={rpcParams.Receive.SenderClientId}");
            return;
        }

        SpawnProjectileVisualClientRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
    }

    [ClientRpc]
    private void SpawnProjectileVisualClientRpc(Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime)
    {
        // Spawn the same temporary projectile visual on every connected client.
        SimpleProjectileVisual.Spawn(origin, targetPoint, speed, radius, lifeTime);
    }

    private void ApplyAvatarTransform(Vector3 position, Quaternion rotation)
    {
        // Update the server-authoritative avatar transform for NetworkTransform sync.
        transform.SetPositionAndRotation(position, rotation);
    }

    private void SetOwnerVisualVisible(bool visible)
    {
        // Toggle the temporary avatar renderer and collider visibility for this client.
        foreach (Renderer targetRenderer in renderers)
        {
            if (targetRenderer != null)
            {
                targetRenderer.enabled = visible;
            }
        }

        foreach (Collider targetCollider in colliders)
        {
            if (targetCollider != null)
            {
                targetCollider.enabled = visible;
            }
        }
    }

    private bool TryGetProjectileAttackSettings(bool requireResolvedEquipment, out EquipmentAttackSettings attackSettings)
    {
        // Validate that this player has an attacking projectile-mode equipment state.
        attackSettings = null;
        if (equipmentState == null)
        {
            equipmentState = GetComponent<NetworkPlayerEquipmentState>();
        }

        if (equipmentState == null)
        {
            return !requireResolvedEquipment;
        }

        EquipmentDefinition equipment = equipmentState.CurrentEquipment;
        if (equipment == null)
        {
            return !requireResolvedEquipment;
        }

        if (!equipment.CanAttack ||
            equipment.Attack == null ||
            equipment.Attack.AttackMode != EquipmentAttackMode.Projectile)
        {
            return false;
        }

        attackSettings = equipment.Attack;
        return true;
    }

    private static void ApplyProjectileEquipmentSettings(EquipmentAttackSettings attackSettings, Vector3 origin, ref Vector3 targetPoint, ref float speed, ref float radius, ref float lifeTime)
    {
        // Prefer server-known equipment projectile settings over client-provided visual values.
        if (attackSettings == null)
        {
            return;
        }

        if (attackSettings.Range > 0f)
        {
            Vector3 toTarget = targetPoint - origin;
            if (toTarget.sqrMagnitude > 0.0001f && toTarget.magnitude > attackSettings.Range)
            {
                targetPoint = origin + toTarget.normalized * attackSettings.Range;
            }
        }

        speed = attackSettings.ProjectileSpeed > 0f ? attackSettings.ProjectileSpeed : speed;
        radius = attackSettings.ProjectileRadius > 0f ? attackSettings.ProjectileRadius : radius;
        lifeTime = attackSettings.ProjectileLifeTime > 0f ? attackSettings.ProjectileLifeTime : lifeTime;
    }

    private static bool TrySanitizeProjectile(Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime, out ProjectilePacket packet)
    {
        // Clamp projectile visual data so clients cannot request unbounded visuals.
        packet = default;
        if (!IsFinite(origin) || !IsFinite(targetPoint) || !IsFinite(speed) || !IsFinite(radius) || !IsFinite(lifeTime))
        {
            return false;
        }

        Vector3 toTarget = targetPoint - origin;
        if (toTarget.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        float distance = Mathf.Min(toTarget.magnitude, MaxProjectileDistance);
        packet = new ProjectilePacket
        {
            Origin = origin,
            TargetPoint = origin + toTarget.normalized * distance,
            Speed = Mathf.Clamp(speed, MinProjectileSpeed, MaxProjectileSpeed),
            Radius = Mathf.Clamp(radius, MinProjectileRadius, MaxProjectileRadius),
            LifeTime = Mathf.Clamp(lifeTime, MinProjectileLifetime, MaxProjectileLifetime)
        };

        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        // Check each vector component without relying on newer float helper APIs.
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        // Reject NaN and infinite values from client-provided projectile data.
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private struct ProjectilePacket
    {
        public Vector3 Origin;
        public Vector3 TargetPoint;
        public float Speed;
        public float Radius;
        public float LifeTime;
    }
}
