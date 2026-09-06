using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

[RequireComponent(typeof(ThirdPersonController))]
[RequireComponent(typeof(PlayerEquipment))]
public class PlayerProjectileShooter : MonoBehaviour
{
    [Header("Aim")]
    [SerializeField] private Camera aimCamera;
    [SerializeField] private LayerMask aimMask = ~0;
    [SerializeField] private float maxAimDistance = 100f;

    [Header("Muzzle")]
    [SerializeField] private Transform muzzleTransform;
    [SerializeField] private float muzzleHeight = 0.75f;
    [FormerlySerializedAs("muzzleCameraRightOffset")]
    [SerializeField] private float muzzleRightOffset = 0.25f;
    [FormerlySerializedAs("muzzleCameraForwardOffset")]
    [SerializeField] private float muzzleForwardOffset = 0.35f;

    [Header("Projectile")]
    [SerializeField] private float fallbackShotsPerSecond = 5f;
    [SerializeField] private float projectileSpeed = 32f;
    [SerializeField] private float projectileRadius = 0.12f;
    [SerializeField] private float projectileLifeTime = 4f;

    [Header("Input")]
    [SerializeField] private bool allowKeyboardFireKey = true;
    [SerializeField] private bool ignoreMouseWhenPointerOverUi = true;

    private ThirdPersonController controller;
    private CharacterController characterController;
    private PlayerEquipment equipment;
    private PlayerEquipmentHookShooter hookShooter;
    private PlayableCharacterAnimationDriver animationDriver;
    private NetworkPlayerAvatarRelay cachedRelay;
    private float nextFireTime;

    private void Awake()
    {
        // Cache the local movement controller and the active camera reference.
        controller = GetComponent<ThirdPersonController>();
        characterController = GetComponent<CharacterController>();
        equipment = GetComponent<PlayerEquipment>();
        hookShooter = GetComponent<PlayerEquipmentHookShooter>();
        animationDriver = GetComponent<PlayableCharacterAnimationDriver>();
        ResolveAimCamera();
    }

    private void Update()
    {
        // Fire from input or from the temporary auto-fire buff while respecting fire-rate limits.
        if (!HasLocalControl() || IsBlockedByHookAction() || Time.time < nextFireTime)
        {
            return;
        }

        if (!ShouldFireThisFrame() && !ShouldAutoFireThisFrame())
        {
            return;
        }

        FireProjectile();
    }

    private void FireProjectile()
    {
        // Aim from the screen center ray and route the attack through the equipped weapon mode.
        if (!TryGetAttackSettings(out EquipmentAttackSettings attackSettings))
        {
            return;
        }

        Camera cameraToUse = ResolveAimCamera();
        if (cameraToUse == null)
        {
            Debug.LogWarning("[PlayerProjectileShooter] Cannot fire because no aim camera is available.");
            return;
        }

        FaceCharacterToCamera(cameraToUse);

        float shotsPerSecond = GetShotsPerSecond(attackSettings);
        nextFireTime = Time.time + 1f / shotsPerSecond;

        float attackRange = GetAttackRange(attackSettings);
        Ray aimRay = cameraToUse.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 aimPoint = ResolveAimPoint(aimRay, attackRange);
        Vector3 muzzlePosition = ResolveMuzzlePosition();

        if (attackSettings.AttackMode == EquipmentAttackMode.Hitscan)
        {
            FireHitscan(muzzlePosition, aimPoint);
            return;
        }

        if (attackSettings.AttackMode == EquipmentAttackMode.Cannon)
        {
            FireCannon(muzzlePosition, aimPoint);
            return;
        }

        float resolvedProjectileSpeed = GetProjectileSpeed(attackSettings);
        float resolvedProjectileRadius = GetProjectileRadius(attackSettings);
        float resolvedProjectileLifeTime = GetProjectileLifeTime(attackSettings);

        if (TrySendNetworkProjectile(muzzlePosition, aimPoint, resolvedProjectileSpeed, resolvedProjectileRadius, resolvedProjectileLifeTime, out bool usedNetworkPath) ||
            usedNetworkPath)
        {
            TriggerShootAnimation();
            return;
        }

        SimpleProjectileVisual.Spawn(
            muzzlePosition,
            aimPoint,
            resolvedProjectileSpeed,
            resolvedProjectileRadius,
            resolvedProjectileLifeTime,
            GetProjectileVisualPrefab(attackSettings),
            GetProjectileVisualResourcePath(attackSettings));
        TriggerShootAnimation();
    }

    private void FireHitscan(Vector3 muzzlePosition, Vector3 aimPoint)
    {
        // Send a hitscan attack to the network relay, or draw a local tracer for offline tests.
        if (TrySendNetworkHitscan(muzzlePosition, aimPoint, out bool usedNetworkPath) || usedNetworkPath)
        {
            TriggerShootAnimation();
            return;
        }

        SimpleHitscanVisual.Spawn(muzzlePosition, aimPoint);
        TriggerShootAnimation();
    }

    private void FireCannon(Vector3 muzzlePosition, Vector3 aimPoint)
    {
        // Send a gravity-driven cannon shot to the network relay, or spawn a local ballistic visual for offline tests.
        if (!TryGetAttackSettings(out EquipmentAttackSettings attackSettings))
        {
            return;
        }

        float resolvedProjectileSpeed = GetProjectileSpeed(attackSettings);
        float resolvedProjectileRadius = GetProjectileRadius(attackSettings);
        float resolvedProjectileLifeTime = GetProjectileLifeTime(attackSettings);
        float resolvedProjectileGravity = GetProjectileGravity(attackSettings);

        if (TrySendNetworkCannon(muzzlePosition, aimPoint, resolvedProjectileSpeed, resolvedProjectileRadius, resolvedProjectileLifeTime, out bool usedNetworkPath) ||
            usedNetworkPath)
        {
            TriggerShootAnimation();
            return;
        }

        SimpleProjectileVisual.SpawnBallistic(
            muzzlePosition,
            aimPoint,
            resolvedProjectileSpeed,
            resolvedProjectileRadius,
            resolvedProjectileLifeTime,
            resolvedProjectileGravity,
            GetProjectileVisualPrefab(attackSettings),
            GetProjectileVisualResourcePath(attackSettings));
        TriggerShootAnimation();
    }

    private void FaceCharacterToCamera(Camera cameraToUse)
    {
        // Turn the local character toward camera-forward before playing the shoot motion.
        if (controller == null)
        {
            controller = GetComponent<ThirdPersonController>();
        }

        controller?.FaceCameraForwardImmediate(cameraToUse != null ? cameraToUse.transform : null);
    }

    private bool HasLocalControl()
    {
        // Only the owning network player should read local fire input.
        if (controller == null)
        {
            controller = GetComponent<ThirdPersonController>();
        }

        return controller == null || controller.HasLocalControl;
    }

    private bool IsBlockedByHookAction()
    {
        // Prevent basic projectile fire while the local hook is travelling or pulling back.
        if (hookShooter == null)
        {
            hookShooter = GetComponent<PlayerEquipmentHookShooter>();
        }

        return hookShooter != null && hookShooter.IsHookActionActive;
    }

    private void TriggerShootAnimation()
    {
        // Notify the local playable character animator that a projectile attack was fired.
        if (animationDriver == null)
        {
            animationDriver = GetComponent<PlayableCharacterAnimationDriver>();
        }

        animationDriver?.TriggerShoot();
    }

    private Camera ResolveAimCamera()
    {
        // Resolve the inspector camera or cache the active MainCamera.
        if (aimCamera != null)
        {
            return aimCamera;
        }

        aimCamera = Camera.main;
        return aimCamera;
    }

    private Vector3 ResolveAimPoint(Ray aimRay, float range)
    {
        // Delegate aim geometry while keeping the current input and cached controller in this component.
        return PlayerAttackAimQuery.ResolveAimPoint(aimRay, range, aimMask, transform, controller);
    }

    private bool ShouldIgnoreAimHit(RaycastHit hit)
    {
        // Preserve the component's self-hit filter entry point and delegate the existing exclusion rules.
        return PlayerAttackAimQuery.ShouldIgnoreAimHit(hit, transform, controller);
    }

    private Vector3 ResolveMuzzlePosition()
    {
        // Use an explicit muzzle if assigned, otherwise place it on the player body facing direction.
        if (muzzleTransform != null)
        {
            return muzzleTransform.position;
        }

        return PlayerAttackAimQuery.ResolveMuzzlePosition(
            ResolveBodyMuzzleBasePosition(), transform, muzzleRightOffset, muzzleForwardOffset);
    }

    private Vector3 ResolveBodyMuzzleBasePosition()
    {
        // Anchor fallback muzzle height to the player body, not to the shoulder camera.
        if (characterController == null)
        {
            characterController = GetComponent<CharacterController>();
        }

        return PlayerAttackAimQuery.ResolveBodyMuzzleBasePosition(transform, characterController, muzzleHeight);
    }

    private bool TrySendNetworkProjectile(Vector3 muzzlePosition, Vector3 aimPoint, float speed, float radius, float lifeTime, out bool usedNetworkPath)
    {
        // In multiplayer, relay the projectile request through the owned NetworkObject.
        usedNetworkPath = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        NetworkPlayerAvatarRelay relay = ResolveLocalRelay();
        if (relay == null)
        {
            return false;
        }

        usedNetworkPath = true;
        return relay.RequestProjectileVisual(muzzlePosition, aimPoint, speed, radius, lifeTime);
    }

    private bool TrySendNetworkHitscan(Vector3 muzzlePosition, Vector3 aimPoint, out bool usedNetworkPath)
    {
        // In multiplayer, relay hitscan attacks through the owned NetworkObject for server damage approval.
        usedNetworkPath = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        NetworkPlayerAvatarRelay relay = ResolveLocalRelay();
        if (relay == null)
        {
            return false;
        }

        usedNetworkPath = true;
        return relay.RequestHitscanAttack(muzzlePosition, aimPoint);
    }

    private bool TrySendNetworkCannon(Vector3 muzzlePosition, Vector3 aimPoint, float speed, float radius, float lifeTime, out bool usedNetworkPath)
    {
        // In multiplayer, relay cannon shots through the owned NetworkObject for server splash approval.
        usedNetworkPath = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        NetworkPlayerAvatarRelay relay = ResolveLocalRelay();
        if (relay == null)
        {
            return false;
        }

        usedNetworkPath = true;
        return relay.RequestCannonAttack(muzzlePosition, aimPoint, speed, radius, lifeTime);
    }

    private NetworkPlayerAvatarRelay ResolveLocalRelay()
    {
        // Find the local player's relay on the spawned NetworkObject_Test.
        if (cachedRelay != null && cachedRelay.IsSpawned && cachedRelay.IsOwner)
        {
            return cachedRelay;
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            return null;
        }

        NetworkObject localPlayerObject = NetworkManager.Singleton.SpawnManager?.GetLocalPlayerObject();
        if (localPlayerObject != null && localPlayerObject.TryGetComponent(out NetworkPlayerAvatarRelay relay))
        {
            cachedRelay = relay;
            return cachedRelay;
        }

        NetworkPlayerAvatarRelay[] relays = FindObjectsByType<NetworkPlayerAvatarRelay>(FindObjectsSortMode.None);
        for (int i = 0; i < relays.Length; i++)
        {
            if (relays[i] != null && relays[i].IsOwner)
            {
                cachedRelay = relays[i];
                return cachedRelay;
            }
        }

        return null;
    }

    private bool TryGetAttackSettings(out EquipmentAttackSettings attackSettings)
    {
        // Require an equipped attacking item and return its supported attack settings.
        attackSettings = null;
        if (equipment == null)
        {
            equipment = GetComponent<PlayerEquipment>();
        }

        if (equipment == null || !equipment.CanAttack || equipment.CurrentEquipment == null)
        {
            return false;
        }

        attackSettings = equipment.CurrentEquipment.Attack;
        if (attackSettings == null)
        {
            return false;
        }

        if (attackSettings.AttackMode != EquipmentAttackMode.Projectile &&
            attackSettings.AttackMode != EquipmentAttackMode.Hitscan &&
            attackSettings.AttackMode != EquipmentAttackMode.Cannon)
        {
            Debug.LogWarning($"[PlayerProjectileShooter] Equipment '{equipment.CurrentEquipment.DisplayName}' has unsupported attack mode {attackSettings.AttackMode}.");
            return false;
        }

        return true;
    }

    private float GetShotsPerSecond(EquipmentAttackSettings attackSettings)
    {
        // Prefer equipment attack override, then controller stat, and apply collected FireRate stacks.
        if (attackSettings != null && attackSettings.ShotsPerSecondOverride > 0f)
        {
            float equipmentFireRate = PlayerStatsState.ApplyLocalClientStatBonus(PlayerStatType.FireRate, attackSettings.ShotsPerSecondOverride);
            return Mathf.Max(0.1f, equipmentFireRate);
        }

        float statFireRate = controller != null
            ? controller.FireRate
            : PlayerStatsState.ApplyLocalClientStatBonus(PlayerStatType.FireRate, fallbackShotsPerSecond);
        return Mathf.Max(0.1f, statFireRate > 0f ? statFireRate : fallbackShotsPerSecond);
    }

    private float GetAttackRange(EquipmentAttackSettings attackSettings)
    {
        // Resolve the effective aim range from equipment data or the inspector fallback.
        return Mathf.Max(0.1f, attackSettings != null && attackSettings.Range > 0f ? attackSettings.Range : maxAimDistance);
    }

    private float GetProjectileSpeed(EquipmentAttackSettings attackSettings)
    {
        // Resolve the projectile speed from equipment data or the inspector fallback.
        return Mathf.Max(0.1f, attackSettings != null && attackSettings.ProjectileSpeed > 0f ? attackSettings.ProjectileSpeed : projectileSpeed);
    }

    private float GetProjectileRadius(EquipmentAttackSettings attackSettings)
    {
        // Resolve the projectile radius from equipment data or the inspector fallback.
        return Mathf.Max(0.01f, attackSettings != null && attackSettings.ProjectileRadius > 0f ? attackSettings.ProjectileRadius : projectileRadius);
    }

    private float GetProjectileLifeTime(EquipmentAttackSettings attackSettings)
    {
        // Resolve the projectile lifetime from equipment data or the inspector fallback.
        return Mathf.Max(0.1f, attackSettings != null && attackSettings.ProjectileLifeTime > 0f ? attackSettings.ProjectileLifeTime : projectileLifeTime);
    }

    private static float GetProjectileGravity(EquipmentAttackSettings attackSettings)
    {
        // Resolve gravity for cannon-style projectile visuals.
        return Mathf.Max(0f, attackSettings != null && attackSettings.ProjectileGravity > 0f ? attackSettings.ProjectileGravity : 18f);
    }

    private GameObject GetProjectileVisualPrefab(EquipmentAttackSettings attackSettings)
    {
        // Resolve an optional per-equipment projectile prefab for offline or local fallback shots.
        return attackSettings != null ? attackSettings.ProjectileVisualPrefab : null;
    }

    private string GetProjectileVisualResourcePath(EquipmentAttackSettings attackSettings)
    {
        // Resolve an optional per-equipment Resources path for projectile visuals.
        return attackSettings != null ? attackSettings.ProjectileVisualResourcePath : string.Empty;
    }

    private bool ShouldFireThisFrame()
    {
        // Accept mouse, optional keyboard, and gamepad trigger fire inputs.
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            return !ShouldBlockMouseFireForUi();
        }

        if (allowKeyboardFireKey && Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame)
        {
            return true;
        }

        return Gamepad.current != null && Gamepad.current.rightTrigger.wasPressedThisFrame;
    }

    private static bool ShouldAutoFireThisFrame()
    {
        // Treat an active replicated auto-fire buff as a held fire input.
        return NetworkPlayerCombatState.LocalClientHasAutoFireBuff();
    }

    private bool ShouldBlockMouseFireForUi()
    {
        // Prevent UI button clicks from also firing a gameplay projectile.
        return ignoreMouseWhenPointerOverUi &&
            EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject();
    }
}
