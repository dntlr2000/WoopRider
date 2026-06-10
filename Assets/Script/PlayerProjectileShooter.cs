using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

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
    [SerializeField] private float muzzleHeight = 1.15f;
    [SerializeField] private float muzzleCameraRightOffset = 0.45f;
    [SerializeField] private float muzzleCameraForwardOffset = 0.6f;

    [Header("Projectile")]
    [SerializeField] private float fallbackShotsPerSecond = 5f;
    [SerializeField] private float projectileSpeed = 32f;
    [SerializeField] private float projectileRadius = 0.12f;
    [SerializeField] private float projectileLifeTime = 4f;

    [Header("Input")]
    [SerializeField] private bool allowKeyboardFireKey = true;
    [SerializeField] private bool ignoreMouseWhenPointerOverUi = true;

    private ThirdPersonController controller;
    private PlayerEquipment equipment;
    private NetworkPlayerAvatarRelay cachedRelay;
    private float nextFireTime;

    private void Awake()
    {
        // Cache the local movement controller and the active camera reference.
        controller = GetComponent<ThirdPersonController>();
        equipment = GetComponent<PlayerEquipment>();
        ResolveAimCamera();
    }

    private void Update()
    {
        // Fire once per input press while respecting the controller fire-rate stat.
        if (!ShouldFireThisFrame() || Time.time < nextFireTime)
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

        float shotsPerSecond = GetShotsPerSecond(attackSettings);
        nextFireTime = Time.time + 1f / shotsPerSecond;

        Ray aimRay = cameraToUse.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 aimPoint = ResolveAimPoint(aimRay, GetAttackRange(attackSettings));
        Vector3 muzzlePosition = ResolveMuzzlePosition(cameraToUse);
        float resolvedProjectileSpeed = GetProjectileSpeed(attackSettings);
        float resolvedProjectileRadius = GetProjectileRadius(attackSettings);
        float resolvedProjectileLifeTime = GetProjectileLifeTime(attackSettings);

        if (TrySendNetworkProjectile(muzzlePosition, aimPoint, resolvedProjectileSpeed, resolvedProjectileRadius, resolvedProjectileLifeTime))
        {
            return;
        }

        SimpleProjectileVisual.Spawn(muzzlePosition, aimPoint, resolvedProjectileSpeed, resolvedProjectileRadius, resolvedProjectileLifeTime);
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

    private Vector3 ResolveMuzzlePosition(Camera cameraToUse)
    {
        // Use an explicit muzzle if assigned, otherwise place it near the camera-side shoulder.
        if (muzzleTransform != null)
        {
            return muzzleTransform.position;
        }

        Transform cameraTransform = cameraToUse.transform;
        return transform.position +
            Vector3.up * muzzleHeight +
            cameraTransform.right * muzzleCameraRightOffset +
            cameraTransform.forward * muzzleCameraForwardOffset;
    }

    private bool TrySendNetworkProjectile(Vector3 muzzlePosition, Vector3 aimPoint, float speed, float radius, float lifeTime)
    {
        // In multiplayer, relay the projectile request through the owned NetworkObject.
        NetworkPlayerAvatarRelay relay = ResolveLocalRelay();
        if (relay == null)
        {
            return false;
        }

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
        // Prefer equipment attack override, then controller stat, then inspector fallback.
        if (attackSettings != null && attackSettings.ShotsPerSecondOverride > 0f)
        {
            return Mathf.Max(0.1f, attackSettings.ShotsPerSecondOverride);
        }

        float statFireRate = controller != null ? controller.FireRate : fallbackShotsPerSecond;
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
