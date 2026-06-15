using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

[RequireComponent(typeof(ThirdPersonController))]
public class PlayerEquipmentHookShooter : MonoBehaviour
{
    [Header("Aim")]
    [SerializeField] private Camera aimCamera;
    [SerializeField] private LayerMask aimMask = ~0;
    [SerializeField] private float maxHookDistance = 30f;

    [Header("Muzzle")]
    [SerializeField] private Transform muzzleTransform;
    [SerializeField] private float muzzleHeight = 0.65f;
    [FormerlySerializedAs("muzzleCameraRightOffset")]
    [SerializeField] private float muzzleRightOffset = 0.15f;
    [FormerlySerializedAs("muzzleCameraForwardOffset")]
    [SerializeField] private float muzzleForwardOffset = 0.3f;

    [Header("Input")]
    [SerializeField] private float hookCooldown = 0.35f;
    [SerializeField] private bool ignoreMouseWhenPointerOverUi = true;

    [Header("Hook Action Lock")]
    [SerializeField] private float hookVisualSpeed = 70f;
    [SerializeField] private float hookFacingLockPadding = 0.15f;

    private ThirdPersonController controller;
    private CharacterController characterController;
    private PlayableCharacterAnimationDriver animationDriver;
    private float nextHookTime;
    private float hookActionLockedUntil;
    private Transform hookFacingCamera;

    public bool IsHookActionActive => Time.time < hookActionLockedUntil;

    private void Awake()
    {
        // Cache local references used by the temporary hook input path.
        controller = GetComponent<ThirdPersonController>();
        characterController = GetComponent<CharacterController>();
        animationDriver = GetComponent<PlayableCharacterAnimationDriver>();
        ResolveAimCamera();
    }

    private void Update()
    {
        // Fire a hook once per right-click press while respecting a small local cooldown.
        MaintainHookActionFacing();

        if (!HasLocalControl() || !ShouldHookThisFrame() || Time.time < nextHookTime)
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

        FaceCharacterToCamera(cameraToUse);

        nextHookTime = Time.time + Mathf.Max(0.05f, hookCooldown);

        Ray aimRay = cameraToUse.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 aimPoint = ResolveAimPoint(aimRay);
        Vector3 muzzlePosition = ResolveMuzzlePosition();

        GameplayPickupManager pickupManager = GameplayPickupManager.Instance;
        if (pickupManager == null || !pickupManager.RequestEquipmentHook(muzzlePosition, aimPoint))
        {
            Debug.Log("[PlayerEquipmentHookShooter] Hook request ignored because no network pickup manager is ready.");
            return;
        }

        StartHookActionLock(cameraToUse, muzzlePosition, aimPoint);
        TriggerHookAnimation();
    }

    private void FaceCharacterToCamera(Camera cameraToUse)
    {
        // Turn the local character toward camera-forward before playing the hook motion.
        if (controller == null)
        {
            controller = GetComponent<ThirdPersonController>();
        }

        controller?.FaceCameraForwardImmediate(cameraToUse != null ? cameraToUse.transform : null);
    }

    private void StartHookActionLock(Camera cameraToUse, Vector3 muzzlePosition, Vector3 aimPoint)
    {
        // Lock facing for the estimated outbound and return time of the temporary hook.
        hookFacingCamera = cameraToUse != null ? cameraToUse.transform : null;
        float duration = EstimateHookActionDuration(muzzlePosition, aimPoint);
        hookActionLockedUntil = Mathf.Max(hookActionLockedUntil, Time.time + duration);

        if (controller == null)
        {
            controller = GetComponent<ThirdPersonController>();
        }

        controller?.LockCameraFacing(duration, hookFacingCamera);
    }

    private void MaintainHookActionFacing()
    {
        // Keep reapplying camera-facing rotation while hook travel or pull-back is active.
        if (!IsHookActionActive)
        {
            return;
        }

        if (controller == null)
        {
            controller = GetComponent<ThirdPersonController>();
        }

        float remaining = Mathf.Max(0f, hookActionLockedUntil - Time.time);
        controller?.LockCameraFacing(remaining, hookFacingCamera);
    }

    private float EstimateHookActionDuration(Vector3 muzzlePosition, Vector3 aimPoint)
    {
        // Estimate the visual out-and-back duration so fire input stays blocked during hook action.
        float speed = Mathf.Max(0.1f, hookVisualSpeed);
        float outboundDistance = Vector3.Distance(muzzlePosition, aimPoint);
        float returnDistance = outboundDistance;
        return Mathf.Max(0.1f, (outboundDistance + returnDistance) / speed + Mathf.Max(0f, hookFacingLockPadding));
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

    private bool HasLocalControl()
    {
        // Only the owning network player should read local hook input.
        if (controller == null)
        {
            controller = GetComponent<ThirdPersonController>();
        }

        return controller == null || controller.HasLocalControl;
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

    private Vector3 ResolveMuzzlePosition()
    {
        // Use an explicit hook muzzle if assigned, otherwise place it on the player body facing direction.
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
        // Anchor fallback hook origin to the player body, not to the shoulder camera.
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
