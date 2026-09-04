using Unity.Netcode;
using UnityEngine;

public class PlayableCharacterAnimationDriver : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Animator animator;

    [Header("Parameters")]
    [SerializeField] private string moveSpeedParameter = "MoveSpeed";
    [SerializeField] private string isGroundedParameter = "IsGrounded";
    [SerializeField] private string jumpTrigger = "Jump";
    [SerializeField] private string damagedTrigger = "Damaged";
    [SerializeField] private string shootTrigger = "Shoot";
    [SerializeField] private string hookTrigger = "Hook";
    [SerializeField] private string landTrigger = "Land";

    [Header("Locomotion")]
    [SerializeField] private bool driveLocomotion = true;
    [SerializeField] private float moveSpeedDampTime = 0.08f;
    [SerializeField] private float stopSpeedSnapThreshold = 0.05f;
    [SerializeField] private bool assumeGroundedWithoutController = true;
    [SerializeField] private float runSpeedThreshold = 0.1f;

    [Header("Controller States")]
    [SerializeField] private bool driveControllerStatesDirectly = false;
    [SerializeField] private string idleStateName = "GroundIdle";
    [SerializeField] private string runStateName = "Run";
    [SerializeField] private string fallStateName = "Fall";
    [SerializeField] private string jumpStateName = "Jump";
    [SerializeField] private string landStateName = "Land";
    [SerializeField] private string damagedStateName = "Damaged";
    [SerializeField] private string shootStateName = "GroundShoot";
    [SerializeField] private string hookStateName = "Hook";
    [SerializeField] private float locomotionFadeDuration = 0.08f;
    [SerializeField] private float actionFadeDuration = 0.04f;
    [SerializeField] private float jumpActionDuration = 0.22f;
    [SerializeField] private float landActionDuration = 0.18f;
    [SerializeField] private float shootActionDuration = 0.28f;
    [SerializeField] private float hookActionDuration = 0.35f;
    [SerializeField] private float damagedActionDuration = 0.35f;

    private PlayableCharacterVisualLoader visualLoader;
    private CharacterController characterController;
    private NetworkObject networkObject;
    private Vector3 lastPosition;
    private bool wasGrounded;
    private bool initialized;
    private string lastRequestedStateName;
    private float actionStateEndTime;

    private int moveSpeedHash;
    private int isGroundedHash;
    private int jumpHash;
    private int damagedHash;
    private int shootHash;
    private int hookHash;
    private int landHash;

    private void Awake()
    {
        // Cache local dependencies and Animator parameter hashes.
        visualLoader = GetComponent<PlayableCharacterVisualLoader>();
        characterController = GetComponent<CharacterController>();
        networkObject = GetComponent<NetworkObject>();
        CacheParameterHashes();
        lastPosition = transform.position;
        wasGrounded = ResolveGrounded();
        initialized = false;
    }

    private void Update()
    {
        // Continuously drive movement and grounded parameters for the character controller.
        if (!driveLocomotion || ResolveAnimator() == null)
        {
            lastPosition = transform.position;
            return;
        }

        float horizontalSpeed = ResolveHorizontalSpeed();
        float animatorMoveSpeed = ResolveAnimatorMoveSpeed(horizontalSpeed);
        bool isGrounded = ResolveGrounded();

        if (!driveControllerStatesDirectly)
        {
            SetLocomotionParameters(animatorMoveSpeed, isGrounded);
        }

        if (initialized && !wasGrounded && isGrounded)
        {
            PlayLand();
        }
        else if (!initialized)
        {
            initialized = true;
        }

        DriveLocomotionState(animatorMoveSpeed, isGrounded);

        wasGrounded = isGrounded;
        lastPosition = transform.position;
    }

    public void TriggerJump()
    {
        // Play the jump one-shot animation.
        PlayTriggerOrActionState(jumpHash, jumpStateName, jumpActionDuration);
    }

    public void ResetMotionTracking()
    {
        // Reset transform-delta tracking after a teleport so it does not produce a false movement spike.
        lastPosition = transform.position;
        wasGrounded = ResolveGrounded();
    }

    public void TriggerDamaged()
    {
        // Play the damaged one-shot animation.
        PlayTriggerOrActionState(damagedHash, damagedStateName, damagedActionDuration, restartIfAlreadyPlaying: true);
    }

    public void TriggerShoot()
    {
        // Play the shooting one-shot animation.
        PlayTriggerOrActionState(shootHash, shootStateName, shootActionDuration, restartIfAlreadyPlaying: true);
    }

    public void TriggerHook()
    {
        // Play the equipment hook one-shot animation.
        PlayTriggerOrActionState(hookHash, hookStateName, hookActionDuration, restartIfAlreadyPlaying: true);
    }

    private Animator ResolveAnimator()
    {
        // Resolve the Animator from the assigned reference, visual loader, or child model.
        if (animator != null)
        {
            return animator;
        }

        if (visualLoader != null)
        {
            animator = visualLoader.Animator;
        }

        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>(true);
        }

        return animator;
    }

    private void CacheParameterHashes()
    {
        // Convert Animator parameter names into stable integer hashes.
        moveSpeedHash = Animator.StringToHash(moveSpeedParameter);
        isGroundedHash = Animator.StringToHash(isGroundedParameter);
        jumpHash = Animator.StringToHash(jumpTrigger);
        damagedHash = Animator.StringToHash(damagedTrigger);
        shootHash = Animator.StringToHash(shootTrigger);
        hookHash = Animator.StringToHash(hookTrigger);
        landHash = Animator.StringToHash(landTrigger);
    }

    private float ResolveAnimatorMoveSpeed(float horizontalSpeed)
    {
        // Snap near-zero movement to idle so stopping does not linger in run animation.
        return horizontalSpeed <= Mathf.Max(0f, stopSpeedSnapThreshold) ? 0f : horizontalSpeed;
    }

    private void SetLocomotionParameters(float animatorMoveSpeed, bool isGrounded)
    {
        // Damp movement while running, but clear speed immediately when the character stops.
        if (animatorMoveSpeed <= 0f)
        {
            animator.SetFloat(moveSpeedHash, 0f);
        }
        else
        {
            animator.SetFloat(moveSpeedHash, animatorMoveSpeed, moveSpeedDampTime, Time.deltaTime);
        }

        animator.SetBool(isGroundedHash, isGrounded);
    }

    private void DriveLocomotionState(float horizontalSpeed, bool isGrounded)
    {
        // Keep the AnimatorController in the state that matches current movement when no action is playing.
        if (!driveControllerStatesDirectly || IsActionStateActive() || ResolveAnimator() == null)
        {
            return;
        }

        string targetStateName = ResolveLocomotionStateName(horizontalSpeed, isGrounded);
        CrossFadeState(targetStateName, locomotionFadeDuration);
    }

    private string ResolveLocomotionStateName(float horizontalSpeed, bool isGrounded)
    {
        // Convert grounded and speed values into one of the shared playable character locomotion states.
        if (!isGrounded)
        {
            return fallStateName;
        }

        return horizontalSpeed > Mathf.Max(0f, runSpeedThreshold) ? runStateName : idleStateName;
    }

    private void PlayLand()
    {
        // Trigger the landing animation when the character returns to ground.
        PlayTriggerOrActionState(landHash, landStateName, landActionDuration);
    }

    private void PlayTriggerOrActionState(int triggerHash, string stateName, float duration, bool restartIfAlreadyPlaying = false)
    {
        // Route one-shot animations through exactly one control path to avoid double-starting the same clip.
        if (driveControllerStatesDirectly && PlayActionState(stateName, duration))
        {
            return;
        }

        if (restartIfAlreadyPlaying && RestartActionStateIfAlreadyPlaying(triggerHash, stateName))
        {
            return;
        }

        SetTrigger(triggerHash);
    }

    private bool RestartActionStateIfAlreadyPlaying(int triggerHash, string stateName)
    {
        // Restart the same one-shot action from frame zero when it is already current or being entered.
        if (!TryResolveStateHash(stateName, out int fullPathHash))
        {
            return false;
        }

        bool isCurrentState = animator.GetCurrentAnimatorStateInfo(0).fullPathHash == fullPathHash;
        bool isNextState = animator.IsInTransition(0) &&
            animator.GetNextAnimatorStateInfo(0).fullPathHash == fullPathHash;

        if (!isCurrentState && !isNextState)
        {
            return false;
        }

        animator.ResetTrigger(triggerHash);
        animator.Play(fullPathHash, 0, 0f);
        lastRequestedStateName = stateName;
        return true;
    }

    private bool PlayActionState(string stateName, float duration)
    {
        // Immediately switch to an action state, then briefly hold locomotion changes.
        if (!driveControllerStatesDirectly || ResolveAnimator() == null)
        {
            return false;
        }

        if (!CrossFadeState(stateName, actionFadeDuration, force: true))
        {
            return false;
        }

        actionStateEndTime = Time.time + Mathf.Max(0f, duration);
        return true;
    }

    private bool IsActionStateActive()
    {
        // Check whether a recently requested one-shot action should still be allowed to finish.
        return Time.time < actionStateEndTime;
    }

    private bool CrossFadeState(string stateName, float fadeDuration, bool force = false)
    {
        // Crossfade to a named AnimatorController state when it exists on the base layer.
        if (!TryResolveStateHash(stateName, out int fullPathHash))
        {
            return false;
        }

        if (!force && lastRequestedStateName == stateName)
        {
            return false;
        }

        animator.CrossFade(fullPathHash, Mathf.Max(0f, fadeDuration), 0);
        lastRequestedStateName = stateName;
        return true;
    }

    private bool TryResolveStateHash(string stateName, out int fullPathHash)
    {
        // Resolve a base-layer state hash only when the Animator exists and the state is present.
        fullPathHash = 0;
        if (string.IsNullOrWhiteSpace(stateName) || ResolveAnimator() == null)
        {
            return false;
        }

        fullPathHash = Animator.StringToHash($"Base Layer.{stateName}");
        return animator.HasState(0, fullPathHash);
    }

    private float ResolveHorizontalSpeed()
    {
        // Estimate planar speed from transform movement so both local and network avatars can animate.
        Vector3 delta = transform.position - lastPosition;
        delta.y = 0f;

        if (Time.deltaTime <= 0f)
        {
            return 0f;
        }

        return delta.magnitude / Time.deltaTime;
    }

    private bool ResolveGrounded()
    {
        // Prefer CharacterController grounded state, then fall back to the configured remote-avatar assumption.
        if (networkObject == null)
        {
            networkObject = GetComponent<NetworkObject>();
        }

        if (networkObject != null &&
            NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsListening &&
            networkObject.IsSpawned &&
            !networkObject.IsOwner)
        {
            return assumeGroundedWithoutController;
        }

        if (characterController != null)
        {
            return characterController.isGrounded;
        }

        return assumeGroundedWithoutController;
    }

    private void SetTrigger(int triggerHash)
    {
        // Set an Animator trigger only when the visual Animator is ready.
        if (ResolveAnimator() == null)
        {
            return;
        }

        animator.SetTrigger(triggerHash);
    }
}
