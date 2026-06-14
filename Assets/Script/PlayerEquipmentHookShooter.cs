using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

[RequireComponent(typeof(ThirdPersonController))]
public class PlayerEquipmentHookShooter : MonoBehaviour
{
    [Header("Aim")]
    [SerializeField] private Camera aimCamera;
    [SerializeField] private LayerMask aimMask = ~0;
    [SerializeField] private float maxHookDistance = 45f;

    [Header("Muzzle")]
    [SerializeField] private Transform muzzleTransform;
    [SerializeField] private float muzzleHeight = 1.725f;
    [SerializeField] private float muzzleCameraRightOffset = 0.525f;
    [SerializeField] private float muzzleCameraForwardOffset = 0.825f;

    [Header("Input")]
    [SerializeField] private float hookCooldown = 0.35f;
    [SerializeField] private bool ignoreMouseWhenPointerOverUi = true;

    private ThirdPersonController controller;
    private PlayableCharacterAnimationDriver animationDriver;
    private float nextHookTime;

    private void Awake()
    {
        // Cache local references used by the temporary hook input path.
        controller = GetComponent<ThirdPersonController>();
        animationDriver = GetComponent<PlayableCharacterAnimationDriver>();
        ResolveAimCamera();
    }

    private void Update()
    {
        // Fire a hook once per right-click press while respecting a small local cooldown.
        if (!ShouldHookThisFrame() || Time.time < nextHookTime)
        {
            return;
        }

        FireHook();
    }

    private void FireHook()
    {
        // Aim from the screen center and request server-authoritative equipment hook collection.
        Camera cameraToUse = ResolveAimCamera();
        if (cameraToUse == null)
        {
            Debug.LogWarning("[PlayerEquipmentHookShooter] Cannot hook because no aim camera is available.");
            return;
        }

        nextHookTime = Time.time + Mathf.Max(0.05f, hookCooldown);

        Ray aimRay = cameraToUse.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 aimPoint = ResolveAimPoint(aimRay);
        Vector3 muzzlePosition = ResolveMuzzlePosition(cameraToUse);

        GameplayPickupManager pickupManager = GameplayPickupManager.Instance;
        if (pickupManager == null || !pickupManager.RequestEquipmentHook(muzzlePosition, aimPoint))
        {
            Debug.Log("[PlayerEquipmentHookShooter] Hook request ignored because no network pickup manager is ready.");
            return;
        }

        TriggerHookAnimation();
    }

    private void TriggerHookAnimation()
    {
        // Notify the local playable character animator that an equipment hook was fired.
        if (animationDriver == null)
        {
            animationDriver = GetComponent<PlayableCharacterAnimationDriver>();
        }

        animationDriver?.TriggerHook();
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

    private Vector3 ResolveAimPoint(Ray aimRay)
    {
        // Use the nearest non-self hit as the hook direction, otherwise use max hook range.
        RaycastHit[] hits = Physics.RaycastAll(aimRay, maxHookDistance, aimMask, QueryTriggerInteraction.Ignore);
        float nearestDistance = float.MaxValue;
        Vector3 aimPoint = aimRay.GetPoint(maxHookDistance);

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
        // Ignore the local player body so shoulder camera hooks do not target the player itself.
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
        // Use an explicit hook muzzle if assigned, otherwise place it near the camera-side shoulder.
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

    private bool ShouldHookThisFrame()
    {
        // Accept right mouse button hook input while avoiding UI clicks.
        if (Mouse.current == null || !Mouse.current.rightButton.wasPressedThisFrame)
        {
            return false;
        }

        return !ShouldBlockMouseHookForUi();
    }

    private bool ShouldBlockMouseHookForUi()
    {
        // Prevent UI right-clicks from also firing the equipment hook.
        return ignoreMouseWhenPointerOverUi &&
            EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject();
    }
}
