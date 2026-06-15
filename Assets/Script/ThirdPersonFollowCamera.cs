using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class ThirdPersonFollowCamera : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform target;
    [SerializeField] private bool autoFindTarget = true;

    [Header("Look")]
    [SerializeField] private bool allowManualCameraLook = true;
    [SerializeField] private float mouseSensitivity = 1f;
    [SerializeField] private float startPitch = 15f;
    [SerializeField] private float minPitch = -30f;
    [SerializeField] private float maxPitch = 65f;

    [Header("Follow")]
    [SerializeField] private float cameraDistance = 3.25f;
    [SerializeField] private float cameraHeight = 1.55f;
    [SerializeField] private float shoulderOffset = 0.75f;
    [SerializeField] private float positionSmoothing = 18f;
    [SerializeField] private float yawFollowSpeed = 8f;
    [SerializeField] private float fieldOfView = 65f;

    [Header("Collision")]
    [SerializeField] private float collisionRadius = 0.25f;
    [SerializeField] private LayerMask collisionMask = ~0;

    private Camera followCamera;
    private float yaw;
    private float pitch;
    private bool initializedAngles;
    private bool snappedToTarget;

    public float MouseSensitivity => mouseSensitivity;

    private void Awake()
    {
        // Cache the Camera component and clamp the initial pitch.
        followCamera = GetComponent<Camera>();
        pitch = Mathf.Clamp(startPitch, minPitch, maxPitch);
    }

    private void OnEnable()
    {
        // Apply saved config and listen for runtime sensitivity changes.
        GameConfigStore.MouseSensitivityChanged += SetMouseSensitivity;
        SetMouseSensitivity(GameConfigStore.MouseSensitivity);
    }

    private void OnDisable()
    {
        // Stop listening when this camera is disabled or destroyed.
        GameConfigStore.MouseSensitivityChanged -= SetMouseSensitivity;
    }

    private void Start()
    {
        // Find the local player target and align the starting camera angle.
        ResolveTarget();
        InitializeAnglesFromTarget();
    }

    private void LateUpdate()
    {
        // Follow after character movement so the camera sees the final frame position.
        if (ResolveTarget() == null)
        {
            return;
        }

        HandleManualLookInput();
        FollowTargetYawWhenNeeded();
        UpdateCameraTransform();
    }

    private Transform ResolveTarget()
    {
        // Auto-bind to the locally controlled player when no explicit target is assigned.
        if (!autoFindTarget)
        {
            return target;
        }

        if (target != null && IsLocalTarget(target))
        {
            return target;
        }

        target = null;
        initializedAngles = false;
        ThirdPersonController controller = FindLocalController();
        if (controller != null)
        {
            target = controller.transform;
            snappedToTarget = false;
            InitializeAnglesFromTarget();
        }

        return target;
    }

    private static ThirdPersonController FindLocalController()
    {
        // Prefer the owner-controlled network player, falling back to offline test controllers.
        ThirdPersonController[] controllers = FindObjectsByType<ThirdPersonController>(FindObjectsSortMode.None);
        for (int i = 0; i < controllers.Length; i++)
        {
            if (controllers[i] != null && controllers[i].HasLocalControl)
            {
                return controllers[i];
            }
        }

        return null;
    }

    private static bool IsLocalTarget(Transform candidate)
    {
        // Keep the current target only while it still belongs to the locally controlled player.
        ThirdPersonController controller = candidate != null ? candidate.GetComponent<ThirdPersonController>() : null;
        return controller != null && controller.HasLocalControl;
    }

    private void InitializeAnglesFromTarget()
    {
        // Use the target yaw once so the camera starts behind the player.
        if (initializedAngles || target == null)
        {
            return;
        }

        yaw = target.eulerAngles.y;
        pitch = Mathf.Clamp(startPitch, minPitch, maxPitch);
        initializedAngles = true;
    }

    private void HandleManualLookInput()
    {
        // Rotate the camera from mouse or right-stick input when manual look is enabled.
        if (!allowManualCameraLook)
        {
            return;
        }

        Vector2 lookInput = GetLookInput();
        yaw += lookInput.x * mouseSensitivity;
        pitch -= lookInput.y * mouseSensitivity;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    public void SetMouseSensitivity(float value)
    {
        // Update the camera look multiplier from saved or live config values.
        mouseSensitivity = Mathf.Clamp(value, GameConfigStore.MinMouseSensitivity, GameConfigStore.MaxMouseSensitivity);
    }

    private void FollowTargetYawWhenNeeded()
    {
        // Smoothly follow target yaw only in automatic look mode.
        if (allowManualCameraLook || target == null)
        {
            return;
        }

        float t = 1f - Mathf.Exp(-yawFollowSpeed * Time.deltaTime);
        yaw = Mathf.LerpAngle(yaw, target.eulerAngles.y, t);
        pitch = Mathf.Lerp(pitch, Mathf.Clamp(startPitch, minPitch, maxPitch), t);
    }

    private void UpdateCameraTransform()
    {
        // Calculate the shoulder view, apply collision correction, and move the camera.
        Vector3 pivot = target.position + Vector3.up * cameraHeight;
        Quaternion cameraRotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 shoulderPivot = pivot + cameraRotation * Vector3.right * shoulderOffset;
        Vector3 desiredPosition = shoulderPivot - cameraRotation * Vector3.forward * cameraDistance;
        Vector3 finalPosition = ResolveCameraCollision(shoulderPivot, desiredPosition);

        if (!snappedToTarget)
        {
            transform.position = finalPosition;
            snappedToTarget = true;
        }
        else
        {
            float t = 1f - Mathf.Exp(-positionSmoothing * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, finalPosition, t);
        }

        transform.rotation = cameraRotation;
        followCamera.fieldOfView = fieldOfView;
    }

    private Vector3 ResolveCameraCollision(Vector3 shoulderPivot, Vector3 desiredPosition)
    {
        // Pull the camera forward when level geometry blocks the desired view.
        Vector3 direction = desiredPosition - shoulderPivot;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return desiredPosition;
        }

        RaycastHit[] hits = Physics.SphereCastAll(
            shoulderPivot,
            collisionRadius,
            direction.normalized,
            distance,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        float nearestDistance = float.MaxValue;
        bool hasBlockingHit = false;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || hit.collider.transform.IsChildOf(target))
            {
                continue;
            }

            if (hit.distance < nearestDistance)
            {
                nearestDistance = hit.distance;
                hasBlockingHit = true;
            }
        }

        if (hasBlockingHit)
        {
            return shoulderPivot + direction.normalized * Mathf.Max(0f, nearestDistance - collisionRadius);
        }

        return desiredPosition;
    }

    private static Vector2 GetLookInput()
    {
        // Collect mouse delta and right-stick input as camera rotation input.
        Vector2 look = Vector2.zero;

        if (Mouse.current != null)
        {
            look += Mouse.current.delta.ReadValue();
        }

        if (Gamepad.current != null)
        {
            look += Gamepad.current.rightStick.ReadValue() * 120f * Time.deltaTime;
        }

        return look;
    }
}
