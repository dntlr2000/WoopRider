using System.Collections;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayerAvatarRelay : NetworkBehaviour
{
    private const string DefaultCannonExplosionEffectResourcePath = "Effects/CustomEffects/Shoot_ExplosionA Variant";
    private const float MaxProjectileDistance = 120f;
    private const float MinProjectileSpeed = 1f;
    private const float MaxProjectileSpeed = 120f;
    private const float MinProjectileRadius = 0.02f;
    private const float MaxProjectileRadius = 1f;
    private const float MinProjectileLifetime = 0.1f;
    private const float MaxProjectileLifetime = 10f;
    private static ulong nextServerProjectileVisualId;

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
    private GameObject resolvedDefaultCannonExplosionEffectPrefab;
    private float nextSendTime;
    private float nextServerAttackTime;
    private Vector3 lastSentPosition;
    private Quaternion lastSentRotation = Quaternion.identity;
    private bool triedLoadDefaultCannonExplosionEffectPrefab;

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

            ulong projectileVisualId = AllocateServerProjectileVisualId();
            ApplyAttackFacingFromProjectile(packet);
            PlayShootAnimationClientRpc(transform.rotation);
            SpawnProjectileVisualClientRpc(projectileVisualId, packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
            StartServerProjectileDamage(projectileVisualId, packet, attackSettings);
            return true;
        }

        SubmitProjectileVisualServerRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
        return true;
    }

    public bool RequestHitscanAttack(Vector3 origin, Vector3 targetPoint)
    {
        // Let the owning client ask the server to resolve an instant hitscan attack.
        if (!IsSpawned || !IsOwner)
        {
            return false;
        }

        if (!TryGetHitscanAttackSettings(requireResolvedEquipment: IsServer, out EquipmentAttackSettings attackSettings))
        {
            return false;
        }

        if (!TrySanitizeHitscan(origin, targetPoint, attackSettings, out HitscanPacket packet))
        {
            return false;
        }

        if (IsServer)
        {
            if (!TryApproveServerHitscan(ref packet, attackSettings, "local-server"))
            {
                return false;
            }

            TryApplyHitscanDamage(packet, attackSettings);
            ApplyAttackFacingFromHitscan(packet);
            PlayShootAnimationClientRpc(transform.rotation);
            SpawnHitscanVisualClientRpc(packet.Origin, packet.TargetPoint);
            return true;
        }

        SubmitHitscanAttackServerRpc(packet.Origin, packet.TargetPoint);
        return true;
    }

    public bool RequestCannonAttack(Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime)
    {
        // Let the owning client ask the server to resolve a gravity-driven cannon attack.
        if (!IsSpawned || !IsOwner)
        {
            return false;
        }

        if (!TryGetCannonAttackSettings(requireResolvedEquipment: IsServer, out EquipmentAttackSettings attackSettings))
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
            if (!TryApproveServerCannon(ref packet, attackSettings, "local-server"))
            {
                return false;
            }

            ulong projectileVisualId = AllocateServerProjectileVisualId();
            ApplyAttackFacingFromProjectile(packet);
            PlayShootAnimationClientRpc(transform.rotation);
            SpawnCannonProjectileVisualClientRpc(
                projectileVisualId,
                packet.Origin,
                packet.TargetPoint,
                packet.Speed,
                packet.Radius,
                packet.LifeTime,
                ResolveProjectileGravity(attackSettings));
            StartServerCannonDamage(projectileVisualId, packet, attackSettings);
            return true;
        }

        SubmitCannonAttackServerRpc(packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
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

        ulong projectileVisualId = AllocateServerProjectileVisualId();
        ApplyAttackFacingFromProjectile(packet);
        PlayShootAnimationClientRpc(transform.rotation);
        SpawnProjectileVisualClientRpc(projectileVisualId, packet.Origin, packet.TargetPoint, packet.Speed, packet.Radius, packet.LifeTime);
        StartServerProjectileDamage(projectileVisualId, packet, attackSettings);
    }

    [ServerRpc(RequireOwnership = false)]
    private void SubmitHitscanAttackServerRpc(Vector3 origin, Vector3 targetPoint, ServerRpcParams rpcParams = default)
    {
        // Validate the owner request before resolving hitscan damage and tracer feedback.
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected hitscan sender={rpcParams.Receive.SenderClientId} owner={OwnerClientId}");
            return;
        }

        if (!TryGetHitscanAttackSettings(requireResolvedEquipment: true, out EquipmentAttackSettings attackSettings))
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected hitscan without valid hitscan equipment sender={rpcParams.Receive.SenderClientId}");
            return;
        }

        if (!TrySanitizeHitscan(origin, targetPoint, attackSettings, out HitscanPacket packet))
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected invalid hitscan sender={rpcParams.Receive.SenderClientId}");
            return;
        }

        if (!TryApproveServerHitscan(ref packet, attackSettings, $"client={rpcParams.Receive.SenderClientId}"))
        {
            return;
        }

        TryApplyHitscanDamage(packet, attackSettings);
        ApplyAttackFacingFromHitscan(packet);
        PlayShootAnimationClientRpc(transform.rotation);
        SpawnHitscanVisualClientRpc(packet.Origin, packet.TargetPoint);
    }

    [ServerRpc(RequireOwnership = false)]
    private void SubmitCannonAttackServerRpc(Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime, ServerRpcParams rpcParams = default)
    {
        // Validate the owner request before resolving cannon travel, explosion, and splash damage.
        if (rpcParams.Receive.SenderClientId != OwnerClientId)
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected cannon sender={rpcParams.Receive.SenderClientId} owner={OwnerClientId}");
            return;
        }

        if (!TryGetCannonAttackSettings(requireResolvedEquipment: true, out EquipmentAttackSettings attackSettings))
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected cannon without valid cannon equipment sender={rpcParams.Receive.SenderClientId}");
            return;
        }

        ApplyProjectileEquipmentSettings(attackSettings, origin, ref targetPoint, ref speed, ref radius, ref lifeTime);

        if (!TrySanitizeProjectile(origin, targetPoint, speed, radius, lifeTime, out ProjectilePacket packet))
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Rejected invalid cannon sender={rpcParams.Receive.SenderClientId}");
            return;
        }

        if (!TryApproveServerCannon(ref packet, attackSettings, $"client={rpcParams.Receive.SenderClientId}"))
        {
            return;
        }

        ulong projectileVisualId = AllocateServerProjectileVisualId();
        ApplyAttackFacingFromProjectile(packet);
        PlayShootAnimationClientRpc(transform.rotation);
        SpawnCannonProjectileVisualClientRpc(
            projectileVisualId,
            packet.Origin,
            packet.TargetPoint,
            packet.Speed,
            packet.Radius,
            packet.LifeTime,
            ResolveProjectileGravity(attackSettings));
        StartServerCannonDamage(projectileVisualId, packet, attackSettings);
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
    private void SpawnProjectileVisualClientRpc(ulong projectileVisualId, Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime)
    {
        // Spawn the approved projectile visual on every connected client using the shooter's equipment data.
        SimpleProjectileVisual.Spawn(
            origin,
            targetPoint,
            speed,
            radius,
            lifeTime,
            ResolveProjectileVisualPrefab(),
            ResolveProjectileVisualResourcePath(),
            projectileVisualId);
    }

    [ClientRpc]
    private void SpawnHitscanVisualClientRpc(Vector3 origin, Vector3 targetPoint)
    {
        // Spawn a short tracer line on every connected client for instant hitscan feedback.
        SimpleHitscanVisual.Spawn(origin, targetPoint);
    }

    [ClientRpc]
    private void SpawnCannonProjectileVisualClientRpc(ulong projectileVisualId, Vector3 origin, Vector3 targetPoint, float speed, float radius, float lifeTime, float gravity)
    {
        // Spawn a gravity-driven cannon projectile visual on every connected client.
        SimpleProjectileVisual.SpawnBallistic(
            origin,
            targetPoint,
            speed,
            radius,
            lifeTime,
            gravity,
            ResolveProjectileVisualPrefab(),
            ResolveProjectileVisualResourcePath(),
            projectileVisualId);
    }

    [ClientRpc]
    private void StopProjectileVisualClientRpc(ulong projectileVisualId, Vector3 impactPoint)
    {
        // Remove the matching projectile visual at the server-authoritative impact point on every client.
        SimpleProjectileVisual.StopNetworkVisual(projectileVisualId, impactPoint);
    }

    [ClientRpc]
    private void SpawnCannonExplosionClientRpc(Vector3 position, float gameplayRadius, float effectScale, FixedString512Bytes effectResourcePath)
    {
        // Spawn the cannon explosion effect at the server-approved impact point.
        PlayCannonExplosionEffect(position, gameplayRadius, effectScale, effectResourcePath.ToString());
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
        ApplyAttackFacingFromPoints(packet.Origin, packet.TargetPoint);
    }

    private void ApplyAttackFacingFromHitscan(HitscanPacket packet)
    {
        // Rotate the server avatar to the horizontal hitscan direction for attack animation sync.
        ApplyAttackFacingFromPoints(packet.Origin, packet.TargetPoint);
    }

    private void ApplyAttackFacingFromPoints(Vector3 origin, Vector3 targetPoint)
    {
        // Rotate this avatar toward a server-approved attack direction.
        Vector3 direction = targetPoint - origin;
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

    private bool TryGetHitscanAttackSettings(bool requireResolvedEquipment, out EquipmentAttackSettings attackSettings)
    {
        // Validate that this player has an attacking hitscan-mode equipment state.
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
            equipment.Attack.AttackMode != EquipmentAttackMode.Hitscan)
        {
            return false;
        }

        attackSettings = equipment.Attack;
        return true;
    }

    private bool TryGetCannonAttackSettings(bool requireResolvedEquipment, out EquipmentAttackSettings attackSettings)
    {
        // Validate that this player has an attacking cannon-mode equipment state.
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
            equipment.Attack.AttackMode != EquipmentAttackMode.Cannon)
        {
            return false;
        }

        attackSettings = equipment.Attack;
        return true;
    }

    private GameObject ResolveProjectileVisualPrefab()
    {
        // Resolve the firing equipment's configured projectile prefab for client-side visuals.
        EquipmentAttackSettings attackSettings = ResolveCurrentAttackSettings();
        return attackSettings != null ? attackSettings.ProjectileVisualPrefab : null;
    }

    private string ResolveProjectileVisualResourcePath()
    {
        // Resolve the firing equipment's configured Resources path for client-side projectile visuals.
        EquipmentAttackSettings attackSettings = ResolveCurrentAttackSettings();
        return attackSettings != null ? attackSettings.ProjectileVisualResourcePath : string.Empty;
    }

    private GameObject ResolveCannonExplosionEffectPrefab(string resourcePath)
    {
        // Resolve a shot-specific Resources explosion prefab, falling back to the shared cannon effect.
        if (!string.IsNullOrWhiteSpace(resourcePath))
        {
            GameObject configuredPrefab = Resources.Load<GameObject>(resourcePath.Trim());
            if (configuredPrefab != null)
            {
                return configuredPrefab;
            }
        }

        return ResolveDefaultCannonExplosionEffectPrefab();
    }

    private static string ResolveExplosionEffectResourcePath(EquipmentAttackSettings attackSettings)
    {
        // Resolve the Resources path that should travel with this cannon shot.
        return attackSettings != null && !string.IsNullOrWhiteSpace(attackSettings.ExplosionEffectResourcePath)
            ? attackSettings.ExplosionEffectResourcePath.Trim()
            : DefaultCannonExplosionEffectResourcePath;
    }

    private GameObject ResolveDefaultCannonExplosionEffectPrefab()
    {
        // Cache the default explosion effect so missing per-equipment data still has feedback.
        if (triedLoadDefaultCannonExplosionEffectPrefab)
        {
            return resolvedDefaultCannonExplosionEffectPrefab;
        }

        triedLoadDefaultCannonExplosionEffectPrefab = true;
        resolvedDefaultCannonExplosionEffectPrefab = Resources.Load<GameObject>(DefaultCannonExplosionEffectResourcePath);
        if (resolvedDefaultCannonExplosionEffectPrefab == null)
        {
            Debug.LogWarning($"[NetworkPlayerAvatarRelay] Cannon explosion effect prefab not found path={DefaultCannonExplosionEffectResourcePath}");
        }

        return resolvedDefaultCannonExplosionEffectPrefab;
    }

    private static float ResolveExplosionEffectScale(EquipmentAttackSettings attackSettings)
    {
        // Resolve visual-only explosion scale separately from gameplay radius for easier tuning.
        return Mathf.Max(0.01f, attackSettings != null ? attackSettings.ExplosionEffectScale : 1f);
    }

    private void PlayCannonExplosionEffect(Vector3 position, float gameplayRadius, float effectScale, string effectResourcePath)
    {
        // Instantiate the configured cannon explosion effect or a simple fallback flash.
        GameObject explosionPrefab = ResolveCannonExplosionEffectPrefab(effectResourcePath);
        if (explosionPrefab == null)
        {
            CreateFallbackCannonExplosion(position, gameplayRadius, effectScale);
            return;
        }

        GameObject explosion = Instantiate(explosionPrefab, position, Quaternion.identity);
        explosion.transform.localScale *= Mathf.Max(0.01f, effectScale);
        Destroy(explosion, 4f);
    }

    private static void CreateFallbackCannonExplosion(Vector3 position, float gameplayRadius, float effectScale)
    {
        // Keep cannon feedback visible even if the configured Resources effect is missing.
        GameObject explosion = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        explosion.name = "FallbackCannonExplosion";
        explosion.transform.position = position;
        explosion.transform.localScale = Vector3.one * Mathf.Max(0.05f, gameplayRadius * 2f * effectScale);

        if (explosion.TryGetComponent(out Collider explosionCollider))
        {
            explosionCollider.enabled = false;
        }

        if (explosion.TryGetComponent(out Renderer explosionRenderer))
        {
            explosionRenderer.material.color = new Color(1f, 0.45f, 0.05f, 0.65f);
        }

        Destroy(explosion, 0.35f);
    }

    private EquipmentAttackSettings ResolveCurrentAttackSettings()
    {
        // Read the currently replicated equipment definition without enforcing attack validity.
        if (equipmentState == null)
        {
            equipmentState = GetComponent<NetworkPlayerEquipmentState>();
        }

        EquipmentDefinition equipment = equipmentState != null ? equipmentState.CurrentEquipment : null;
        return equipment != null ? equipment.Attack : null;
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

    private static float ResolveProjectileGravity(EquipmentAttackSettings attackSettings)
    {
        // Resolve cannon gravity from equipment data with a conservative fallback.
        return Mathf.Max(0f, attackSettings != null && attackSettings.ProjectileGravity > 0f ? attackSettings.ProjectileGravity : 18f);
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

    private bool TryApproveServerHitscan(ref HitscanPacket packet, EquipmentAttackSettings attackSettings, string requesterLabel)
    {
        // Server-side gate for match state, attack rate, and client-provided hitscan origin.
        if (!IsServer)
        {
            return false;
        }

        if (!CanAttackInCurrentMatchState(out NetworkMatchState currentState))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Rejected hitscan outside attack state owner={OwnerClientId} requester={requesterLabel} state={currentState}");
            return false;
        }

        if (!TryConsumeServerAttackCooldown(attackSettings, out float cooldownRemaining))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Rejected hitscan cooldown owner={OwnerClientId} requester={requesterLabel} remaining={cooldownRemaining:0.00}s");
            return false;
        }

        ApplyServerAimOriginCorrection(ref packet);
        return true;
    }

    private bool TryApproveServerCannon(ref ProjectilePacket packet, EquipmentAttackSettings attackSettings, string requesterLabel)
    {
        // Server-side gate for match state, attack rate, and client-provided cannon aim origin.
        if (!IsServer)
        {
            return false;
        }

        if (!CanAttackInCurrentMatchState(out NetworkMatchState currentState))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Rejected cannon outside attack state owner={OwnerClientId} requester={requesterLabel} state={currentState}");
            return false;
        }

        if (!TryConsumeServerAttackCooldown(attackSettings, out float cooldownRemaining))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] Rejected cannon cooldown owner={OwnerClientId} requester={requesterLabel} remaining={cooldownRemaining:0.00}s");
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

    private void ApplyServerAimOriginCorrection(ref HitscanPacket packet)
    {
        // Pull suspicious hitscan origins back near the server-known player position while preserving aim direction.
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

    private void StartServerProjectileDamage(ulong projectileVisualId, ProjectilePacket packet, EquipmentAttackSettings attackSettings)
    {
        // Resolve projectile damage over travel time so projectile weapons do not behave like hitscan attacks.
        if (!IsServer)
        {
            return;
        }

        StartCoroutine(ResolveProjectileDamageOverTravel(projectileVisualId, packet, attackSettings));
    }

    private void StartServerCannonDamage(ulong projectileVisualId, ProjectilePacket packet, EquipmentAttackSettings attackSettings)
    {
        // Resolve cannon travel on the server so splash damage uses an authoritative impact point.
        if (!IsServer)
        {
            return;
        }

        StartCoroutine(ResolveCannonDamageOverTravel(projectileVisualId, packet, attackSettings));
    }

    private IEnumerator ResolveProjectileDamageOverTravel(ulong projectileVisualId, ProjectilePacket packet, EquipmentAttackSettings attackSettings)
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

            if (TryApplyProjectileDamage(segmentPacket, attackSettings, out Vector3 impactPoint))
            {
                StopProjectileVisualClientRpc(projectileVisualId, impactPoint);
                yield break;
            }

            if (TryFindWorldProjectileBlock(segmentPacket, out impactPoint, out _))
            {
                StopProjectileVisualClientRpc(projectileVisualId, impactPoint);
                yield break;
            }

            traveledDistance += stepDistance;
            remainingLifetime -= deltaTime;
            segmentOrigin = segmentTarget;
            yield return null;
        }
    }

    private IEnumerator ResolveCannonDamageOverTravel(ulong projectileVisualId, ProjectilePacket packet, EquipmentAttackSettings attackSettings)
    {
        // Advance the cannon projectile with gravity and explode only after a server-side collision.
        Vector3 toTarget = packet.TargetPoint - packet.Origin;
        if (toTarget.sqrMagnitude <= 0.0001f)
        {
            yield break;
        }

        Vector3 velocity = toTarget.normalized * Mathf.Max(MinProjectileSpeed, packet.Speed);
        float gravity = ResolveProjectileGravity(attackSettings);
        float remainingLifetime = Mathf.Max(0f, packet.LifeTime);
        float traveledDistance = 0f;
        Vector3 segmentOrigin = packet.Origin;

        while (IsServer && IsSpawned && remainingLifetime > 0f && traveledDistance < MaxProjectileDistance)
        {
            float deltaTime = Mathf.Min(Mathf.Max(Time.deltaTime, 0.001f), remainingLifetime);
            velocity += Vector3.down * gravity * deltaTime;
            Vector3 segmentTarget = segmentOrigin + velocity * deltaTime;

            ProjectilePacket segmentPacket = packet;
            segmentPacket.Origin = segmentOrigin;
            segmentPacket.TargetPoint = segmentTarget;

            if (TryFindCannonImpact(segmentPacket, out Vector3 impactPoint))
            {
                StopProjectileVisualClientRpc(projectileVisualId, impactPoint);
                ExplodeCannonAt(impactPoint, attackSettings);
                yield break;
            }

            traveledDistance += Vector3.Distance(segmentOrigin, segmentTarget);
            remainingLifetime -= deltaTime;
            segmentOrigin = segmentTarget;
            yield return null;
        }
    }

    private bool TryApplyProjectileDamage(ProjectilePacket packet, EquipmentAttackSettings attackSettings, out Vector3 impactPoint)
    {
        // Server resolves projectile damage against the nearest damageable target.
        return TryApplyLineAttackDamage(packet, attackSettings, "Projectile", out impactPoint);
    }

    private bool TryApplyHitscanDamage(HitscanPacket packet, EquipmentAttackSettings attackSettings)
    {
        // Server resolves hitscan damage immediately along the approved attack line.
        ProjectilePacket linePacket = new()
        {
            Origin = packet.Origin,
            TargetPoint = packet.TargetPoint,
            Radius = packet.Radius,
            Speed = MaxProjectileSpeed,
            LifeTime = MinProjectileLifetime
        };

        return TryApplyLineAttackDamage(linePacket, attackSettings, "Hitscan", out _);
    }

    private bool TryApplyLineAttackDamage(ProjectilePacket packet, EquipmentAttackSettings attackSettings, string attackLabel, out Vector3 impactPoint)
    {
        // Server resolves the nearest damageable target, including players and destructible pickups.
        impactPoint = default;
        if (!IsServer)
        {
            return false;
        }

        float damage = ResolveOutgoingAttackDamage(attackSettings);
        if (damage <= 0f)
        {
            return false;
        }

        bool hitPlayer = TryFindProjectileTarget(packet, out NetworkPlayerCombatState targetCombatState, out Vector3 playerHitPoint, out float playerHitDistance);
        bool hitBox = TryFindProjectileBoxTarget(packet, out int boxSlotId, out Vector3 boxHitPoint, out float boxHitDistance);
        bool hitEquipment = TryFindProjectileEquipmentTarget(packet, out int equipmentSlotId, out Vector3 equipmentHitPoint, out float equipmentHitDistance);
        bool hitPenguin = TryFindProjectilePenguinTarget(packet, out int penguinSlotId, out Vector3 penguinHitPoint, out float penguinHitDistance);
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

        if (hitPenguin && penguinHitDistance < nearestPickupDistance)
        {
            nearestPickupKind = 3;
            nearestPickupDistance = penguinHitDistance;
        }

        if (nearestPickupKind != 0 && (!hitPlayer || nearestPickupDistance <= playerHitDistance))
        {
            bool appliedDamage = ApplyLineAttackPickupDamage(
                attackLabel,
                nearestPickupKind,
                boxSlotId,
                boxHitPoint,
                equipmentSlotId,
                equipmentHitPoint,
                penguinSlotId,
                penguinHitPoint,
                damage,
                (packet.TargetPoint - packet.Origin).normalized);
            if (appliedDamage)
            {
                impactPoint = nearestPickupKind == 1
                    ? boxHitPoint
                    : nearestPickupKind == 2
                        ? equipmentHitPoint
                        : penguinHitPoint;
            }

            return appliedDamage;
        }

        if (!hitPlayer)
        {
            return false;
        }

        Vector3 hitDirection = (packet.TargetPoint - packet.Origin).normalized;
        if (targetCombatState.ApplyDamage(damage, OwnerClientId, playerHitPoint, hitDirection))
        {
            impactPoint = playerHitPoint;
            if (targetCombatState.OwnerClientId != OwnerClientId)
            {
                PlayOwnerPlayerHitConfirmation();
            }

            Debug.Log($"[NetworkPlayerAvatarRelay] {attackLabel} hit attacker={OwnerClientId} target={targetCombatState.OwnerClientId} point={playerHitPoint}");
            return true;
        }

        return false;
    }

    private static ulong AllocateServerProjectileVisualId()
    {
        // Allocate a nonzero identifier shared by the server damage path and all client visual instances.
        nextServerProjectileVisualId++;
        if (nextServerProjectileVisualId == 0)
        {
            nextServerProjectileVisualId++;
        }

        return nextServerProjectileVisualId;
    }

    private float ResolveOutgoingAttackDamage(EquipmentAttackSettings attackSettings)
    {
        // Resolve base equipment damage, collected attack stat stacks, and temporary outgoing buffs.
        float damageMultiplier = attackSettings != null ? Mathf.Max(0f, attackSettings.DamageMultiplier) : 1f;
        float equipmentDamage = fallbackAttackDamage * damageMultiplier;
        float damage = PlayerStatsState.ApplyCollectedStatBonus(OwnerClientId, PlayerStatType.AttackPower, equipmentDamage);
        return NetworkPlayerCombatState.ApplyOutgoingDamageMultiplier(OwnerClientId, damage);
    }

    private bool TryFindCannonImpact(ProjectilePacket packet, out Vector3 impactPoint)
    {
        // Find the nearest player, pickup, or world collision along one server cannon segment.
        impactPoint = default;
        float nearestDistance = float.MaxValue;
        bool foundImpact = false;

        if (TryFindProjectileTarget(packet, out _, out Vector3 playerHitPoint, out float playerHitDistance))
        {
            nearestDistance = playerHitDistance;
            impactPoint = playerHitPoint;
            foundImpact = true;
        }

        if (TryFindProjectileBoxTarget(packet, out _, out Vector3 boxHitPoint, out float boxHitDistance) &&
            boxHitDistance < nearestDistance)
        {
            nearestDistance = boxHitDistance;
            impactPoint = boxHitPoint;
            foundImpact = true;
        }

        if (TryFindProjectileEquipmentTarget(packet, out _, out Vector3 equipmentHitPoint, out float equipmentHitDistance) &&
            equipmentHitDistance < nearestDistance)
        {
            nearestDistance = equipmentHitDistance;
            impactPoint = equipmentHitPoint;
            foundImpact = true;
        }

        if (TryFindProjectilePenguinTarget(packet, out _, out Vector3 penguinHitPoint, out float penguinHitDistance) &&
            penguinHitDistance < nearestDistance)
        {
            nearestDistance = penguinHitDistance;
            impactPoint = penguinHitPoint;
            foundImpact = true;
        }

        if (TryFindWorldProjectileBlock(packet, out Vector3 worldHitPoint, out float worldHitDistance) &&
            worldHitDistance < nearestDistance)
        {
            impactPoint = worldHitPoint;
            foundImpact = true;
        }

        return foundImpact;
    }

    private bool TryFindWorldProjectileBlock(ProjectilePacket packet, out Vector3 hitPoint, out float hitDistance)
    {
        // Find the nearest non-player collider that should make a cannon shell explode.
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

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || ShouldIgnoreWorldProjectileBlock(hit.collider))
            {
                continue;
            }

            if (hit.distance < hitDistance)
            {
                hitDistance = hit.distance;
                hitPoint = hit.point;
            }
        }

        return hitDistance < float.MaxValue;
    }

    private bool ShouldIgnoreWorldProjectileBlock(Collider targetCollider)
    {
        // Ignore player bodies in the generic world pass because player collisions are handled separately.
        if (targetCollider == null)
        {
            return true;
        }

        Transform targetTransform = targetCollider.transform;
        if (targetTransform == transform || targetTransform.IsChildOf(transform))
        {
            return true;
        }

        return targetCollider.GetComponentInParent<NetworkPlayerCombatState>() != null;
    }

    private void ExplodeCannonAt(Vector3 impactPoint, EquipmentAttackSettings attackSettings)
    {
        // Apply splash damage around the impact point and replicate the explosion feedback.
        float radius = ResolveExplosionRadius(attackSettings);
        float damage = ResolveOutgoingAttackDamage(attackSettings);
        int playerHitCount = 0;
        int opposingPlayerHitCount = 0;
        int pickupHitCount = 0;
        int penguinHitCount = 0;

        if (damage > 0f)
        {
            playerHitCount = NetworkPlayerCombatState.ApplySplashDamage(
                impactPoint,
                radius,
                damage,
                OwnerClientId,
                ResolveSplashMinimumDamageMultiplier(attackSettings),
                ResolveSelfSplashDamageMultiplier(attackSettings),
                out opposingPlayerHitCount);

            GameplayPickupManager pickupManager = GameplayPickupManager.Instance;
            if (pickupManager != null)
            {
                pickupHitCount = pickupManager.ApplySplashDamage(
                    impactPoint,
                    radius,
                    damage,
                    OwnerClientId,
                    ResolveSplashMinimumDamageMultiplier(attackSettings),
                    out penguinHitCount);
            }

            if (opposingPlayerHitCount > 0 || penguinHitCount > 0)
            {
                PlayOwnerPlayerHitConfirmation();
            }
        }

        SpawnCannonExplosionClientRpc(
            impactPoint,
            radius,
            ResolveExplosionEffectScale(attackSettings),
            new FixedString512Bytes(ResolveExplosionEffectResourcePath(attackSettings)));
        Debug.Log($"[NetworkPlayerAvatarRelay] Cannon exploded attacker={OwnerClientId} point={impactPoint} radius={radius:0.00} playerHits={playerHitCount} pickupHits={pickupHitCount} penguinHits={penguinHitCount}");
    }

    private void PlayOwnerPlayerHitConfirmation()
    {
        // Send one successful player-or-event-enemy hit cue only to this attacker's owning client.
        if (!IsServer || NetworkManager.Singleton == null ||
            !NetworkManager.Singleton.ConnectedClients.ContainsKey(OwnerClientId))
        {
            return;
        }

        ClientRpcParams rpcParams = new()
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        };
        PlayOwnerPlayerHitConfirmationClientRpc(rpcParams);
    }

    [ClientRpc]
    private void PlayOwnerPlayerHitConfirmationClientRpc(ClientRpcParams rpcParams = default)
    {
        // Play local successful-hit confirmation without exposing the attacker's feedback to other clients.
        SoundManager.Instance?.PlaySuccessfulPlayerHitSfx();
    }

    private static float ResolveExplosionRadius(EquipmentAttackSettings attackSettings)
    {
        // Resolve cannon gameplay radius from equipment data with a safe fallback.
        return Mathf.Max(0.01f, attackSettings != null && attackSettings.ExplosionRadius > 0f ? attackSettings.ExplosionRadius : 0.5f);
    }

    private static float ResolveSplashMinimumDamageMultiplier(EquipmentAttackSettings attackSettings)
    {
        // Resolve the outer-edge splash damage ratio.
        return Mathf.Clamp01(attackSettings != null ? attackSettings.SplashMinimumDamageMultiplier : 0.4f);
    }

    private static float ResolveSelfSplashDamageMultiplier(EquipmentAttackSettings attackSettings)
    {
        // Resolve the self-damage reduction multiplier for cannon splash.
        return Mathf.Clamp01(attackSettings != null ? attackSettings.SelfSplashDamageMultiplier : 0.5f);
    }

    private bool ApplyLineAttackPickupDamage(
        string attackLabel,
        int pickupKind,
        int boxSlotId,
        Vector3 boxHitPoint,
        int equipmentSlotId,
        Vector3 equipmentHitPoint,
        int penguinSlotId,
        Vector3 penguinHitPoint,
        float damage,
        Vector3 hitDirection)
    {
        // Apply damage to the nearest server-managed pickup target hit by a line attack path.
        GameplayPickupManager pickupManager = GameplayPickupManager.Instance;
        if (pickupManager == null)
        {
            return false;
        }

        if (pickupKind == 1 && pickupManager.TryApplyBoxDamage(boxSlotId, damage, OwnerClientId, boxHitPoint))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] {attackLabel} hit box attacker={OwnerClientId} slot={boxSlotId} point={boxHitPoint}");
            return true;
        }

        if (pickupKind == 2 && pickupManager.TryApplyEquipmentDamage(
                equipmentSlotId,
                damage,
                OwnerClientId,
                equipmentHitPoint,
                hitDirection))
        {
            Debug.Log($"[NetworkPlayerAvatarRelay] {attackLabel} hit equipment attacker={OwnerClientId} slot={equipmentSlotId} point={equipmentHitPoint}");
            return true;
        }

        if (pickupKind == 3 && pickupManager.TryApplyPenguinDamage(penguinSlotId, damage, OwnerClientId, penguinHitPoint, hitDirection))
        {
            PlayOwnerPlayerHitConfirmation();
            Debug.Log($"[NetworkPlayerAvatarRelay] {attackLabel} hit Penguin attacker={OwnerClientId} slot={penguinSlotId} point={penguinHitPoint}");
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

    private bool TryFindProjectilePenguinTarget(ProjectilePacket packet, out int penguinSlotId, out Vector3 hitPoint, out float hitDistance)
    {
        // Ask the gameplay manager for the nearest living event Penguin along this attack segment.
        penguinSlotId = -1;
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

        return pickupManager.TryFindDamageablePenguin(packet.Origin, direction.normalized, distance, packet.Radius, out penguinSlotId, out hitDistance, out hitPoint);
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

    private static bool TrySanitizeHitscan(Vector3 origin, Vector3 targetPoint, EquipmentAttackSettings attackSettings, out HitscanPacket packet)
    {
        // Clamp hitscan data so clients cannot request unbounded instant attack lines.
        packet = default;
        if (!IsFinite(origin) || !IsFinite(targetPoint))
        {
            return false;
        }

        Vector3 toTarget = targetPoint - origin;
        if (toTarget.sqrMagnitude < 0.0001f)
        {
            return false;
        }

        float attackRange = attackSettings != null && attackSettings.Range > 0f
            ? attackSettings.Range
            : MaxProjectileDistance;
        float distance = Mathf.Min(toTarget.magnitude, MaxProjectileDistance, attackRange);
        float radius = attackSettings != null && attackSettings.ProjectileRadius > 0f
            ? attackSettings.ProjectileRadius
            : MinProjectileRadius;

        packet = new HitscanPacket
        {
            Origin = origin,
            TargetPoint = origin + toTarget.normalized * distance,
            Radius = Mathf.Clamp(radius, MinProjectileRadius, MaxProjectileRadius)
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

    private struct HitscanPacket
    {
        public Vector3 Origin;
        public Vector3 TargetPoint;
        public float Radius;
    }

    private enum AnimationActionKind
    {
        Shoot,
        Hook
    }
}
