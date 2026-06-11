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

    [Header("Damage")]
    [SerializeField] private float fallbackAttackDamage = 20f;

    [Header("Server Attack Rules")]
    [SerializeField] private bool requireActiveMatchForAttack = true;
    [SerializeField] private float fallbackServerShotsPerSecond = 5f;
    [SerializeField] private float serverAimOriginTolerance = 4f;
    [SerializeField] private float fallbackTargetRadius = 0.75f;
    [SerializeField] private float fallbackTargetHeight = 1.1f;

    private ThirdPersonController localController;
    private NetworkPlayerEquipmentState equipmentState;
    private Renderer[] renderers;
    private Collider[] colliders;
    private float nextSendTime;
    private float nextServerAttackTime;
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
            if (!TryApproveServerProjectile(ref packet, attackSettings, "local-server"))
            {
                return false;
            }

            TryApplyProjectileDamage(packet, attackSettings);
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

        if (!TryApproveServerProjectile(ref packet, attackSettings, $"client={rpcParams.Receive.SenderClientId}"))
        {
            return;
        }

        TryApplyProjectileDamage(packet, attackSettings);
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

        if (!equipmentState.CanAttack ||
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

    private bool TryApproveServerProjectile(ref ProjectilePacket packet, EquipmentAttackSettings attackSettings, string requesterLabel)
    {
        // Server-side gate for match state, attack rate, and client-provided aim origin.
        if (!IsServer)
        {
            return false;
        }

        if (!CanAttackInCurrentMatchState(out NetworkMatchState currentState))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Rejected projectile outside attack state owner={OwnerClientId} requester={requesterLabel} state={currentState}");
            return false;
        }

        if (!TryConsumeServerAttackCooldown(attackSettings, out float cooldownRemaining))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Rejected projectile cooldown owner={OwnerClientId} requester={requesterLabel} remaining={cooldownRemaining:0.00}s");
            return false;
        }

        ApplyServerAimOriginCorrection(ref packet);
        return true;
    }

    private bool CanAttackInCurrentMatchState(out NetworkMatchState currentState)
    {
        // Restrict PvP attacks to active gameplay states unless this rule is disabled for testing.
        currentState = NetworkMatchState.Lobby;
        if (!requireActiveMatchForAttack)
        {
            return true;
        }

        MatchStateController controller = MatchStateController.Instance;
        if (controller == null || !controller.IsSpawned)
        {
            return false;
        }

        currentState = controller.State.Value;
        return currentState == NetworkMatchState.MatchMain ||
            currentState == NetworkMatchState.FinalMatch;
    }

    private bool TryConsumeServerAttackCooldown(EquipmentAttackSettings attackSettings, out float cooldownRemaining)
    {
        // Use server time to prevent clients from bypassing the effective fire-rate limit.
        cooldownRemaining = nextServerAttackTime - Time.time;
        if (cooldownRemaining > 0f)
        {
            return false;
        }

        float shotsPerSecond = GetServerShotsPerSecond(attackSettings);
        nextServerAttackTime = Time.time + 1f / shotsPerSecond;
        cooldownRemaining = 0f;
        return true;
    }

    private float GetServerShotsPerSecond(EquipmentAttackSettings attackSettings)
    {
        // Resolve the server-side fire rate from equipment data with a safe fallback.
        if (attackSettings != null && attackSettings.ShotsPerSecondOverride > 0f)
        {
            return Mathf.Max(0.1f, attackSettings.ShotsPerSecondOverride);
        }

        return Mathf.Max(0.1f, fallbackServerShotsPerSecond);
    }

    private void ApplyServerAimOriginCorrection(ref ProjectilePacket packet)
    {
        // Pull suspicious muzzle origins back near the server-known player position while preserving aim direction.
        float tolerance = Mathf.Max(0f, serverAimOriginTolerance);
        Vector3 serverOrigin = ResolveServerProjectileOrigin();
        if (tolerance > 0f && (packet.Origin - serverOrigin).sqrMagnitude <= tolerance * tolerance)
        {
            return;
        }

        Vector3 direction = packet.TargetPoint - packet.Origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return;
        }

        packet.Origin = serverOrigin;
        packet.TargetPoint = serverOrigin + direction.normalized * distance;
    }

    private Vector3 ResolveServerProjectileOrigin()
    {
        // Estimate the authoritative muzzle point from the network avatar transform.
        return transform.position + Vector3.up * Mathf.Max(0f, fallbackTargetHeight);
    }

    private void TryApplyProjectileDamage(ProjectilePacket packet, EquipmentAttackSettings attackSettings)
    {
        // Server resolves a simple projectile-line hit so equipment health can break during PvP tests.
        if (!IsServer || !TryFindProjectileTarget(packet, out NetworkPlayerCombatState targetCombatState, out Vector3 hitPoint))
        {
            return;
        }

        float damageMultiplier = attackSettings != null ? Mathf.Max(0f, attackSettings.DamageMultiplier) : 1f;
        float damage = fallbackAttackDamage * damageMultiplier;
        if (damage <= 0f)
        {
            return;
        }

        if (targetCombatState.ApplyDamage(damage, OwnerClientId))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Projectile hit attacker={OwnerClientId} target={targetCombatState.OwnerClientId} point={hitPoint}");
        }
    }

    private bool TryFindProjectileTarget(ProjectilePacket packet, out NetworkPlayerCombatState targetCombatState, out Vector3 hitPoint)
    {
        // Find the nearest damageable network player before any blocking non-player collider.
        targetCombatState = null;
        hitPoint = default;
        Vector3 direction = packet.TargetPoint - packet.Origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        RaycastHit[] hits = Physics.SphereCastAll(
            packet.Origin,
            packet.Radius,
            direction.normalized,
            distance,
            ~0,
            QueryTriggerInteraction.Ignore);

        float nearestTargetDistance = float.MaxValue;
        float nearestBlockDistance = float.MaxValue;
        Vector3 nearestTargetPoint = default;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || hit.collider.GetComponentInParent<ThirdPersonController>() != null)
            {
                continue;
            }

            NetworkPlayerCombatState hitCombatState = hit.collider.GetComponentInParent<NetworkPlayerCombatState>();
            if (hitCombatState != null)
            {
                if (hitCombatState.OwnerClientId == OwnerClientId)
                {
                    continue;
                }

                if (hit.distance < nearestTargetDistance)
                {
                    nearestTargetDistance = hit.distance;
                    nearestTargetPoint = hit.point;
                    targetCombatState = hitCombatState;
                }

                continue;
            }

            if (hit.distance < nearestBlockDistance)
            {
                nearestBlockDistance = hit.distance;
            }
        }

        if (TryFindFallbackTransformTarget(packet, direction.normalized, distance, nearestBlockDistance, ref targetCombatState, ref nearestTargetDistance, ref nearestTargetPoint))
        {
            hitPoint = nearestTargetPoint;
            return true;
        }

        if (targetCombatState != null && nearestTargetDistance <= nearestBlockDistance)
        {
            hitPoint = nearestTargetPoint;
            return true;
        }

        return false;
    }

    private bool TryFindFallbackTransformTarget(ProjectilePacket packet, Vector3 direction, float distance, float nearestBlockDistance, ref NetworkPlayerCombatState targetCombatState, ref float nearestTargetDistance, ref Vector3 nearestTargetPoint)
    {
        // Also test network avatar positions so host-hidden colliders or missing colliders can still be hit.
        NetworkPlayerCombatState[] combatStates = FindObjectsByType<NetworkPlayerCombatState>(FindObjectsSortMode.None);
        float radius = Mathf.Max(packet.Radius, fallbackTargetRadius);
        for (int i = 0; i < combatStates.Length; i++)
        {
            NetworkPlayerCombatState candidate = combatStates[i];
            if (candidate == null || !candidate.IsSpawned || candidate.OwnerClientId == OwnerClientId)
            {
                continue;
            }

            if (!TryIntersectTargetCapsule(packet.Origin, direction, distance, candidate.transform.position, radius, out float candidateDistance, out Vector3 candidatePoint))
            {
                continue;
            }

            if (candidateDistance < nearestTargetDistance && candidateDistance <= nearestBlockDistance)
            {
                nearestTargetDistance = candidateDistance;
                nearestTargetPoint = candidatePoint;
                targetCombatState = candidate;
            }
        }

        return targetCombatState != null && nearestTargetDistance <= nearestBlockDistance;
    }

    private bool TryIntersectTargetCapsule(Vector3 origin, Vector3 direction, float distance, Vector3 targetPosition, float radius, out float hitDistance, out Vector3 hitPoint)
    {
        // Approximate a player body with a short vertical capsule for collider-independent server hit checks.
        hitDistance = 0f;
        hitPoint = default;

        Vector3 bottom = targetPosition;
        Vector3 top = targetPosition + Vector3.up * Mathf.Max(0f, fallbackTargetHeight);
        float bottomDistance = DistanceFromRaySegment(origin, direction, bottom, distance, out float bottomAlongRay);
        float topDistance = DistanceFromRaySegment(origin, direction, top, distance, out float topAlongRay);

        if (bottomDistance > radius && topDistance > radius)
        {
            return false;
        }

        hitDistance = bottomDistance <= topDistance ? bottomAlongRay : topAlongRay;
        hitPoint = origin + direction * hitDistance;
        return true;
    }

    private static float DistanceFromRaySegment(Vector3 origin, Vector3 direction, Vector3 point, float maxDistance, out float alongRay)
    {
        // Measure the shortest distance from a point to the finite projectile path.
        alongRay = Mathf.Clamp(Vector3.Dot(point - origin, direction), 0f, maxDistance);
        Vector3 closestPoint = origin + direction * alongRay;
        return Vector3.Distance(point, closestPoint);
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
