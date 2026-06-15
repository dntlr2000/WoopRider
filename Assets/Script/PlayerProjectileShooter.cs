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
        // Fire once per input press while respecting the controller fire-rate stat.
        if (!HasLocalControl() || IsBlockedByHookAction() || !ShouldFireThisFrame() || Time.time < nextFireTime)
        {
            return;
        }

        FireProjectile();
    }

    private void FireProjectile()
    {
        // Aim from the screen center ray and spawn or request a projectile visual.
        if (!TryGetProjectileAttack(out EquipmentAttackSettings attackSettings))
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

        Ray aimRay = cameraToUse.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 aimPoint = ResolveAimPoint(aimRay, GetAttackRange(attackSettings));
        Vector3 muzzlePosition = ResolveMuzzlePosition();
        float resolvedProjectileSpeed = GetProjectileSpeed(attackSettings);
        float resolvedProjectileRadius = GetProjectileRadius(attackSettings);
        float resolvedProjectileLifeTime = GetProjectileLifeTime(attackSettings);

        if (TrySendNetworkProjectile(muzzlePosition, aimPoint, resolvedProjectileSpeed, resolvedProjectileRadius, resolvedProjectileLifeTime, out bool usedNetworkPath) ||
            usedNetworkPath)
        {
            TriggerShootAnimation();
            return;
        }

        SimpleProjectileVisual.Spawn(muzzlePosition, aimPoint, resolvedProjectileSpeed, resolvedProjectileRadius, resolvedProjectileLifeTime);
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
        // Choose the nearest non-self hit from the center-screen ray.
        RaycastHit[] hits = Physics.RaycastAll(aimRay, range, aimMask, QueryTriggerInteraction.Ignore);
        float nearestDistance = float.MaxValue;
        Vector3 aimPoint = aimRay.GetPoint(range);

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (ShouldIgnoreAimHit(hit))
            {
                continue;
            }

            if (hit.distance < nearestDistance)
            {
                nearestDistance = hit.distance;
                aimPoint = hit.point;
            }
        }

        return aimPoint;
    }

    private bool ShouldIgnoreAimHit(RaycastHit hit)
    {
        // Ignore the local test character so the shoulder camera does not shoot into itself.
        if (hit.collider == null)
        {
            return true;
        }

        Transform hitTransform = hit.collider.transform;
        if (hitTransform == transform || hitTransform.IsChildOf(transform))
        {
            return true;
        }

        ThirdPersonController hitController = hit.collider.GetComponentInParent<ThirdPersonController>();
        return hitController != null && hitController == controller;
    }

    private Vector3 ResolveMuzzlePosition()
    {
        // Use an explicit muzzle if assigned, otherwise place it on the player body facing direction.
        if (muzzleTransform != null)
        {
            return muzzleTransform.position;
        }

        return ResolveBodyMuzzleBasePosition() +
            transform.right * muzzleRightOffset +
            transform.forward * muzzleForwardOffset;
    }

    private Vector3 ResolveBodyMuzzleBasePosition()
    {
        // Anchor fallback muzzle height to the player body, not to the shoulder camera.
        if (characterController == null)
        {
            characterController = GetComponent<CharacterController>();
        }

        if (characterController == null)
        {
            return transform.position + Vector3.up * muzzleHeight;
        }

        float clampedHeight = Mathf.Clamp(muzzleHeight, 0f, Mathf.Max(0.1f, characterController.height));
        return transform.position + Vector3.up * clampedHeight;
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

    private bool TryGetProjectileAttack(out EquipmentAttackSettings attackSettings)
    {
        // Require an equipped attacking item and only accept projectile-mode equipment for this shooter.
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
        if (attackSettings == null || attackSettings.AttackMode != EquipmentAttackMode.Projectile)
        {
            Debug.LogWarning($"[PlayerProjectileShooter] Equipment '{equipment.CurrentEquipment.DisplayName}' is not a projectile weapon.");
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

    private bool ShouldBlockMouseFireForUi()
    {
        // Prevent UI button clicks from also firing a gameplay projectile.
        return ignoreMouseWhenPointerOverUi &&
            EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject();
    }
}
