using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
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
    [SerializeField] private bool hideOwnerVisual = false;

    [Header("Damage")]
    [SerializeField] private float fallbackAttackDamage = 20f;

    [Header("Server Attack Rules")]
    [SerializeField] private bool requireActiveMatchForAttack = true;
    [SerializeField] private float fallbackServerShotsPerSecond = 5f;
    [SerializeField] private float serverAimOriginTolerance = 4f;
    [SerializeField] private float fallbackTargetRadius = 1.125f;
    [SerializeField] private float fallbackTargetHeight = 1.65f;

    private ThirdPersonController localController;
    private NetworkPlayerEquipmentState equipmentState;
    private NetworkTransform networkTransform;
    private Renderer[] renderers;
    private Collider[] colliders;
    private float nextSendTime;
    private float nextServerAttackTime;
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation = Quaternion.identity;

    private void Awake()
    {
        // Cache network player components used for owner relay and visual visibility.
        localController = GetComponent<ThirdPersonController>();
        equipmentState = GetComponent<NetworkPlayerEquipmentState>();
        networkTransform = GetComponent<NetworkTransform>();
        renderers = GetComponentsInChildren<Renderer>(true);
        colliders = GetComponentsInChildren<Collider>(true);
    }

    public override void OnNetworkSpawn()
    {
        // Apply optional owner visual hiding for legacy split-player tests.
        SetOwnerVisualVisible(!(hideOwnerVisual && IsOwner));

        if (IsOwner)
        {
            localController = ResolveLocalController();
            if (localController != null && UsesServerAuthoritativeTransformRelay())
            {
                SendTransform(force: true);
            }
        }
    }

    private void Update()
    {
        // Owner clients relay transforms only when the prefab still uses server-authoritative movement.
        if (!IsOwner || !IsClient || !UsesServerAuthoritativeTransformRelay() || Time.time < nextSendTime)
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

            StartServerProjectileDamage(packet, attackSettings);
            ApplyAttackFacingFromProjectile(packet);
            PlayShootAnimationClientRpc(transform.rotation);
            SpawnProjectileVisualClientRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
            return true;
        }

        SubmitProjectileVisualServerRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
        return true;
    }

    private void SendTransform(bool force)
    {
        // Read the owned ThirdPersonController and send position changes through the legacy server-authoritative path.
        localController = ResolveLocalController();
        if (localController == null)
        {
            return;
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

    private bool UsesServerAuthoritativeTransformRelay()
    {
        // Use manual transform relay only for legacy server-authoritative NetworkTransform prefabs.
        if (networkTransform == null)
        {
            networkTransform = GetComponent<NetworkTransform>();
        }

        return networkTransform == null || networkTransform.AuthorityMode == NetworkTransform.AuthorityModes.Server;
    }

    private ThirdPersonController ResolveLocalController()
    {
        // Prefer the controller on this Network PlayerPrefab, falling back to legacy split-scene tests.
        if (localController != null && localController.HasLocalControl)
        {
            return localController;
        }

        ThirdPersonController ownController = GetComponent<ThirdPersonController>();
        if (ownController != null && ownController.HasLocalControl)
        {
            localController = ownController;
            return localController;
        }

        ThirdPersonController[] controllers = FindObjectsByType<ThirdPersonController>(FindObjectsSortMode.None);
        for (int i = 0; i < controllers.Length; i++)
        {
            if (controllers[i] != null && controllers[i].HasLocalControl)
            {
                localController = controllers[i];
                return localController;
            }
        }

        return null;
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

        StartServerProjectileDamage(packet, attackSettings);
        ApplyAttackFacingFromProjectile(packet);
        PlayShootAnimationClientRpc(transform.rotation);
        SpawnProjectileVisualClientRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
    }

    public void PlayHookAnimation()
    {
        // Play the hook action on this network avatar visual.
        PlayAnimationDriverAction(AnimationActionKind.Hook);
    }

    public static void TryPlayHookAnimationForClient(ulong clientId)
    {
        // Find the network avatar owned by a client and play its hook action locally.
        NetworkPlayerAvatarRelay[] relays = FindObjectsByType<NetworkPlayerAvatarRelay>(FindObjectsSortMode.None);
        for (int i = 0; i < relays.Length; i++)
        {
            NetworkPlayerAvatarRelay relay = relays[i];
            if (relay != null && relay.OwnerClientId == clientId)
            {
                relay.PlayHookAnimation();
                return;
            }
        }
    }

    [ClientRpc]
    private void SpawnProjectileVisualClientRpc(Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime)
    {
        // Spawn the same temporary projectile visual on every connected client.
        SimpleProjectileVisual.Spawn(origin, targetPoint, speed, radius, lifeTime);
    }

    [ClientRpc]
    private void PlayShootAnimationClientRpc(Quaternion facingRotation)
    {
        // Align the network avatar to the approved shot direction before playing the shoot action.
        transform.rotation = facingRotation;
        PlayAnimationDriverAction(AnimationActionKind.Shoot);
    }

    private void ApplyAttackFacingFromProjectile(ProjectilePacket packet)
    {
        // Rotate the server avatar to the horizontal projectile direction for attack animation sync.
        Vector3 direction = packet.TargetPoint - packet.Origin;
        direction.y = 0f;
        if (direction.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
    }

    private void PlayAnimationDriverAction(AnimationActionKind actionKind)
    {
        // Resolve the animation driver and play a supported one-shot action.
        PlayableCharacterAnimationDriver animationDriver = GetComponent<PlayableCharacterAnimationDriver>();
        if (animationDriver == null)
        {
            return;
        }

        if (actionKind == AnimationActionKind.Shoot)
        {
            animationDriver.TriggerShoot();
            return;
        }

        if (actionKind == AnimationActionKind.Hook)
        {
            animationDriver.TriggerHook();
        }
    }

    private void ApplyAvatarTransform(Vector3 position, Quaternion rotation)
    {
        // Update the server-authoritative avatar transform for NetworkTransform sync.
        transform.SetPositionAndRotation(position, rotation);
    }

    private void SetOwnerVisualVisible(bool visible)
    {
        // Toggle this player object's renderers and colliders when legacy owner hiding is enabled.
        RefreshVisualComponents();

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

    private void RefreshVisualComponents()
    {
        // Re-scan runtime-created character visuals before changing owner visibility.
        renderers = GetComponentsInChildren<Renderer>(true);
        colliders = GetComponentsInChildren<Collider>(true);
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
        // Resolve server-side fire rate and apply collected FireRate stacks.
        float equipmentFireRate = attackSettings != null && attackSettings.ShotsPerSecondOverride > 0f
            ? attackSettings.ShotsPerSecondOverride
            : fallbackServerShotsPerSecond;
        float collectedFireRate = PlayerStatsState.ApplyCollectedStatBonus(OwnerClientId, PlayerStatType.FireRate, equipmentFireRate);
        return Mathf.Max(0.1f, collectedFireRate);
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

    private void StartServerProjectileDamage(ProjectilePacket packet, EquipmentAttackSettings attackSettings)
    {
        // Resolve projectile damage over travel time so projectile weapons do not behave like hitscan attacks.
        if (!IsServer)
        {
            return;
        }

        StartCoroutine(ResolveProjectileDamageOverTravel(packet, attackSettings));
    }

    private IEnumerator ResolveProjectileDamageOverTravel(ProjectilePacket packet, EquipmentAttackSettings attackSettings)
    {
        // Advance the authoritative projectile in short segments and apply damage only when a segment intersects a target.
        Vector3 toTarget = packet.TargetPoint - packet.Origin;
        float maxDistance = toTarget.magnitude;
        if (maxDistance <= 0.001f)
        {
            yield break;
        }

        Vector3 direction = toTarget / maxDistance;
        float speed = Mathf.Max(MinProjectileSpeed, packet.Speed);
        float remainingLifetime = Mathf.Max(0f, packet.LifeTime);
        float traveledDistance = 0f;
        Vector3 segmentOrigin = packet.Origin;

        while (IsServer && IsSpawned && remainingLifetime > 0f && traveledDistance < maxDistance)
        {
            float deltaTime = Mathf.Max(Time.deltaTime, 0.001f);
            float stepDistance = Mathf.Min(speed * deltaTime, maxDistance - traveledDistance);
            Vector3 segmentTarget = segmentOrigin + direction * stepDistance;

            ProjectilePacket segmentPacket = packet;
            segmentPacket.Origin = segmentOrigin;
            segmentPacket.TargetPoint = segmentTarget;

            if (TryApplyProjectileDamage(segmentPacket, attackSettings))
            {
                yield break;
            }

            traveledDistance += stepDistance;
            remainingLifetime -= deltaTime;
            segmentOrigin = segmentTarget;
            yield return null;
        }
    }

    private bool TryApplyProjectileDamage(ProjectilePacket packet, EquipmentAttackSettings attackSettings)
    {
        // Server resolves the nearest damageable target, including players and destructible boxes.
        if (!IsServer)
        {
            return false;
        }

        float damageMultiplier = attackSettings != null ? Mathf.Max(0f, attackSettings.DamageMultiplier) : 1f;
        float equipmentDamage = fallbackAttackDamage * damageMultiplier;
        float damage = PlayerStatsState.ApplyCollectedStatBonus(OwnerClientId, PlayerStatType.AttackPower, equipmentDamage);
        if (damage <= 0f)
        {
            return false;
        }

        bool hitPlayer = TryFindProjectileTarget(packet, out NetworkPlayerCombatState targetCombatState, out Vector3 playerHitPoint, out float playerHitDistance);
        bool hitBox = TryFindProjectileBoxTarget(packet, out int boxSlotId, out Vector3 boxHitPoint, out float boxHitDistance);
        bool hitEquipment = TryFindProjectileEquipmentTarget(packet, out int equipmentSlotId, out Vector3 equipmentHitPoint, out float equipmentHitDistance);
        int nearestPickupKind = 0;
        float nearestPickupDistance = float.MaxValue;
        if (hitBox)
        {
            nearestPickupKind = 1;
            nearestPickupDistance = boxHitDistance;
        }

        if (hitEquipment && equipmentHitDistance < nearestPickupDistance)
        {
            nearestPickupKind = 2;
            nearestPickupDistance = equipmentHitDistance;
        }

        if (nearestPickupKind != 0 && (!hitPlayer || nearestPickupDistance <= playerHitDistance))
        {
            return ApplyProjectilePickupDamage(nearestPickupKind, boxSlotId, boxHitPoint, equipmentSlotId, equipmentHitPoint, damage);
        }

        if (!hitPlayer)
        {
            return false;
        }

        Vector3 hitDirection = (packet.TargetPoint - packet.Origin).normalized;
        if (targetCombatState.ApplyDamage(damage, OwnerClientId, playerHitPoint, hitDirection))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Projectile hit attacker={OwnerClientId} target={targetCombatState.OwnerClientId} point={playerHitPoint}");
            return true;
        }

        return false;
    }

    private bool ApplyProjectilePickupDamage(int pickupKind, int boxSlotId, Vector3 boxHitPoint, int equipmentSlotId, Vector3 equipmentHitPoint, float damage)
    {
        // Apply damage to the nearest server-managed pickup target hit by the projectile path.
        GameplayPickupManager pickupManager = GameplayPickupManager.Instance;
        if (pickupManager == null)
        {
            return false;
        }

        if (pickupKind == 1 && pickupManager.TryApplyBoxDamage(boxSlotId, damage, OwnerClientId))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Projectile hit box attacker={OwnerClientId} slot={boxSlotId} point={boxHitPoint}");
            return true;
        }

        if (pickupKind == 2 && pickupManager.TryApplyEquipmentDamage(equipmentSlotId, damage, OwnerClientId))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Projectile hit equipment attacker={OwnerClientId} slot={equipmentSlotId} point={equipmentHitPoint}");
            return true;
        }

        return false;
    }

    private bool TryFindProjectileTarget(ProjectilePacket packet, out NetworkPlayerCombatState targetCombatState, out Vector3 hitPoint, out float hitDistance)
    {
        // Find the nearest damageable network player before any blocking non-player collider.
        targetCombatState = null;
        hitPoint = default;
        hitDistance = float.MaxValue;
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
            if (hit.collider == null)
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
            hitDistance = nearestTargetDistance;
            return true;
        }

        if (targetCombatState != null && nearestTargetDistance <= nearestBlockDistance)
        {
            hitPoint = nearestTargetPoint;
            hitDistance = nearestTargetDistance;
            return true;
        }

        return false;
    }

    private bool TryFindProjectileBoxTarget(ProjectilePacket packet, out int boxSlotId, out Vector3 hitPoint, out float hitDistance)
    {
        // Ask the gameplay manager for the nearest destructible box along this projectile path.
        boxSlotId = -1;
        hitPoint = default;
        hitDistance = float.MaxValue;
        GameplayPickupManager pickupManager = GameplayPickupManager.Instance;
        if (pickupManager == null)
        {
            return false;
        }

        Vector3 direction = packet.TargetPoint - packet.Origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        return pickupManager.TryFindDamageableBox(packet.Origin, direction.normalized, distance, packet.Radius, out boxSlotId, out hitDistance, out hitPoint);
    }

    private bool TryFindProjectileEquipmentTarget(ProjectilePacket packet, out int equipmentSlotId, out Vector3 hitPoint, out float hitDistance)
    {
        // Ask the gameplay manager for the nearest damageable field equipment along this projectile path.
        equipmentSlotId = -1;
        hitPoint = default;
        hitDistance = float.MaxValue;
        GameplayPickupManager pickupManager = GameplayPickupManager.Instance;
        if (pickupManager == null)
        {
            return false;
        }

        Vector3 direction = packet.TargetPoint - packet.Origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        return pickupManager.TryFindDamageableEquipment(packet.Origin, direction.normalized, distance, packet.Radius, out equipmentSlotId, out hitDistance, out hitPoint);
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

    private enum AnimationActionKind
    {
        Shoot,
        Hook
    }
}
