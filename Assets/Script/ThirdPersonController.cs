using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class ThirdPersonController : MonoBehaviour
{
    [System.Serializable]
    public class PlayerStats
    {
        public float moveSpeed = 6f;
        public float jumpForce = 8f;
        public float weight = 70f;
        public float health = 100f;
        public float defense = 10f;
        public float attackPower = 20f;
        public float fireRate = 5f;
    }

    [Header("References")]
    [SerializeField] private Transform cameraTransform;

    [Header("Stats")]
    [SerializeField] private PlayerStats stats = new PlayerStats();

    [Header("Movement")]
    [SerializeField] private float rotationSpeed = 12f;
    [SerializeField] private float gravity = -20f;
    [SerializeField] private bool rotateCharacterToMoveDirection = true;
    [SerializeField] private float attackFacingLockDuration = 0.2f;

    [Header("Debug")]
    [SerializeField] private bool lockCursorOnStart = false;

    private CharacterController controller;
    private PlayerEquipment equipment;
    private PlayableCharacterAnimationDriver animationDriver;
    private NetworkObject networkObject;
    private float verticalVelocity;
    private float movementRotationLockedUntil;
    private float cameraFacingLockedUntil;
    private Transform cameraFacingLockTransform;

    public float MoveSpeed => GetModifiedStat(PlayerStatType.MoveSpeed, stats.moveSpeed);
    public float JumpForce => GetModifiedStat(PlayerStatType.JumpForce, stats.jumpForce);
    public float Weight => GetModifiedStat(PlayerStatType.Weight, stats.weight);
    public float Health => GetModifiedStat(PlayerStatType.Health, stats.health);
    public float Defense => GetModifiedStat(PlayerStatType.Defense, stats.defense);
    public float AttackPower => GetModifiedStat(PlayerStatType.AttackPower, stats.attackPower);
    public float FireRate => GetModifiedStat(PlayerStatType.FireRate, stats.fireRate);
    public bool HasLocalControl => ResolveHasLocalControl();
    public bool IsCameraFacingLocked => Time.time < cameraFacingLockedUntil;

    public bool FaceCameraForwardImmediate(Transform facingCamera = null)
    {
        // Snap the character to the active camera yaw for attack-facing actions.
        Transform cameraToUse = facingCamera != null ? facingCamera : ResolveCameraTransform();
        if (cameraToUse == null)
        {
            return false;
        }

        Vector3 forward = cameraToUse.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude <= 0.0001f)
        {
            return false;
        }

        transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        movementRotationLockedUntil = Mathf.Max(movementRotationLockedUntil, Time.time + Mathf.Max(0f, attackFacingLockDuration));
        return true;
    }

    public void LockCameraFacing(float duration, Transform facingCamera = null)
    {
        // Keep the character facing the camera direction for longer actions such as hook travel.
        if (duration <= 0f)
        {
            return;
        }

        cameraFacingLockTransform = facingCamera != null ? facingCamera : ResolveCameraTransform();
        cameraFacingLockedUntil = Mathf.Max(cameraFacingLockedUntil, Time.time + duration);
        ApplyCameraFacingLock();
    }

    private void Awake()
    {
        // Cache movement dependencies before the first input tick.
        controller = GetComponent<CharacterController>();
        equipment = GetComponent<PlayerEquipment>();
        animationDriver = GetComponent<PlayableCharacterAnimationDriver>();
        networkObject = GetComponent<NetworkObject>();
        ResolveCameraTransform();
    }

    private void Start()
    {
        // Apply the optional test cursor lock setting.
        if (!HasLocalControl)
        {
            return;
        }

        if (lockCursorOnStart)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    private void Update()
    {
        // Poll input each frame and update local character movement.
        if (!HasLocalControl)
        {
            return;
        }

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        ApplyCameraFacingLock();
        HandleMovement();
    }

    private void HandleMovement()
    {
        // Apply camera-yaw-relative movement, gravity, and jumping.
        if (equipment != null && !equipment.CanAct)
        {
            HandleActionDisabledMovement();
            return;
        }

        Vector2 moveInput = GetMoveInput();
        float horizontal = moveInput.x;
        float vertical = moveInput.y;

        Quaternion yawRotation = GetCameraYawRotation();
        Vector3 camForward = yawRotation * Vector3.forward;
        Vector3 camRight = yawRotation * Vector3.right;

        camForward.y = 0f;
        camRight.y = 0f;
        camForward.Normalize();
        camRight.Normalize();

        Vector3 move = (camForward * vertical + camRight * horizontal).normalized;
        Vector3 velocity = move * MoveSpeed;

        if (move.sqrMagnitude > 0.001f && rotateCharacterToMoveDirection && !IsMovementRotationLocked())
        {
            RotateCharacterToDirection(move);
        }

        if (controller.isGrounded)
        {
            if (verticalVelocity < 0f)
            {
                verticalVelocity = -2f;
            }

            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                verticalVelocity = JumpForce;
                animationDriver?.TriggerJump();
            }
        }

        verticalVelocity += gravity * Time.deltaTime;
        velocity.y = verticalVelocity;

        controller.Move(velocity * Time.deltaTime);
    }

    private void HandleActionDisabledMovement()
    {
        // Keep gravity active while preventing movement and jump input during action lock.
        if (controller.isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
        }

        verticalVelocity += gravity * Time.deltaTime;
        controller.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }

    private Quaternion GetCameraYawRotation()
    {
        // Use the active camera yaw as the movement basis, with character yaw as fallback.
        Transform movementCamera = ResolveCameraTransform();
        if (movementCamera == null)
        {
            return Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        }

        return Quaternion.Euler(0f, movementCamera.eulerAngles.y, 0f);
    }

    private Transform ResolveCameraTransform()
    {
        // Resolve the inspector reference or cache the active MainCamera transform.
        if (cameraTransform != null)
        {
            return cameraTransform;
        }

        if (Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        return cameraTransform;
    }

    private static Vector2 GetMoveInput()
    {
        // Collect keyboard and left-stick input into one movement vector.
        Vector2 move = Vector2.zero;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed)
            {
                move.y += 1f;
            }

            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed)
            {
                move.y -= 1f;
            }

            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed)
            {
                move.x -= 1f;
            }

            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed)
            {
                move.x += 1f;
            }
        }

        if (Gamepad.current != null)
        {
            move += Gamepad.current.leftStick.ReadValue();
        }

        return Vector2.ClampMagnitude(move, 1f);
    }

    private void RotateCharacterToDirection(Vector3 direction)
    {
        // Rotate the character toward the actual movement direction.
        Quaternion targetRotation = Quaternion.LookRotation(direction, Vector3.up);
        float t = 1f - Mathf.Exp(-rotationSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, t);
    }

    private bool IsMovementRotationLocked()
    {
        // Keep attack-facing rotation from being immediately overwritten by movement input.
        return Time.time < movementRotationLockedUntil;
    }

    private void ApplyCameraFacingLock()
    {
        // Refresh camera-facing rotation while a longer camera-facing action is active.
        if (!IsCameraFacingLocked)
        {
            return;
        }

        Transform cameraToUse = cameraFacingLockTransform != null ? cameraFacingLockTransform : ResolveCameraTransform();
        FaceCameraForwardImmediate(cameraToUse);
    }

    private bool ResolveHasLocalControl()
    {
        // Allow offline tests, but restrict network-spawned player input to the owning client.
        if (networkObject == null)
        {
            networkObject = GetComponent<NetworkObject>();
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            return true;
        }

        if (networkObject == null || !networkObject.IsSpawned)
        {
            return false;
        }

        return networkObject.IsOwner;
    }

    private float GetModifiedStat(PlayerStatType statType, float baseValue)
    {
        // Apply equipment modifiers first, then the replicated collected-stat bonus.
        if (equipment == null)
        {
            equipment = GetComponent<PlayerEquipment>();
        }

        float equipmentModifiedValue = equipment != null ? equipment.ModifyStat(statType, baseValue) : baseValue;
        float collectedModifiedValue = PlayerStatsState.ApplyLocalClientStatBonus(statType, equipmentModifiedValue);
        return statType == PlayerStatType.MoveSpeed
            ? NetworkPlayerCombatState.ApplyLocalMoveSpeedMultiplier(collectedModifiedValue)
            : collectedModifiedValue;
    }
}
