using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public sealed class JumpPlatform : MonoBehaviour
{
    [Header("Launch")]
    [SerializeField, Tooltip("Launch direction. When Use Local Direction is enabled, this vector follows the platform rotation.")]
    private Vector3 launchDirection = Vector3.up;

    [SerializeField, Min(0f), Tooltip("Initial launch speed applied to the player.")]
    private float launchStrength = 12f;

    [SerializeField, Tooltip("Treat Launch Direction as this platform's local-space direction.")]
    private bool useLocalDirection = true;

    [SerializeField, Min(0f), Tooltip("Minimum time before the same player can trigger this platform again.")]
    private float retriggerCooldown = 0.25f;

    [Header("Audio")]
    [SerializeField, Tooltip("Positional one-shot played for the player who successfully triggers this platform.")]
    private AudioClip activationSfxClip;

    [SerializeField, Range(0f, 1f), Tooltip("Volume scale applied through SoundManager's SFX channel.")]
    private float activationSfxVolumeScale = 1f;

    private readonly Dictionary<int, float> nextLaunchTimes = new();
    private Collider launchTrigger;

    private void Awake()
    {
        // Cache the trigger used to detect players entering the launch area.
        launchTrigger = GetComponent<Collider>();
        if (!launchTrigger.isTrigger)
        {
            Debug.LogWarning($"[JumpPlatform] {name} requires its Collider to use Is Trigger.", this);
        }
    }

    private void Reset()
    {
        // Configure newly added jump platforms with a trigger collider by default.
        Collider platformCollider = GetComponent<Collider>();
        platformCollider.isTrigger = true;
    }

    private void OnValidate()
    {
        // Keep inspector-authored launch settings within usable ranges.
        launchStrength = Mathf.Max(0f, launchStrength);
        retriggerCooldown = Mathf.Max(0f, retriggerCooldown);
        activationSfxVolumeScale = Mathf.Clamp01(activationSfxVolumeScale);
        if (launchDirection.sqrMagnitude <= 0.0001f)
        {
            launchDirection = Vector3.up;
        }
    }

    private void OnDisable()
    {
        // Discard per-player cooldown state when the platform is disabled or unloaded.
        nextLaunchTimes.Clear();
    }

    private void OnTriggerEnter(Collider other)
    {
        // Launch only the locally controlled player represented by the entering collider.
        ThirdPersonController player = other.GetComponentInParent<ThirdPersonController>();
        if (player == null || !player.HasLocalControl)
        {
            return;
        }

        int playerId = player.GetInstanceID();
        if (nextLaunchTimes.TryGetValue(playerId, out float nextLaunchTime) && Time.time < nextLaunchTime)
        {
            return;
        }

        Vector3 worldDirection = ResolveWorldLaunchDirection();
        if (player.ApplyLaunch(worldDirection, launchStrength))
        {
            nextLaunchTimes[playerId] = Time.time + retriggerCooldown;
            PlayActivationSfx();
        }
    }

    private void PlayActivationSfx()
    {
        // Route successful local activation through the pooled 3D SFX channel at the platform position.
        Vector3 soundPosition = launchTrigger != null ? launchTrigger.bounds.center : transform.position;
        SoundManager.Instance?.PlayWorldSfx(activationSfxClip, soundPosition, activationSfxVolumeScale);
    }

    private Vector3 ResolveWorldLaunchDirection()
    {
        // Convert the inspector direction to world space so platform rotation can steer the launch.
        Vector3 direction = launchDirection.sqrMagnitude > 0.0001f ? launchDirection.normalized : Vector3.up;
        return useLocalDirection ? transform.TransformDirection(direction).normalized : direction;
    }

    private void OnDrawGizmosSelected()
    {
        // Draw a Scene-view arrow that previews the configured launch direction and relative strength.
        Collider platformCollider = launchTrigger != null ? launchTrigger : GetComponent<Collider>();
        Vector3 origin = platformCollider != null ? platformCollider.bounds.center : transform.position;
        Vector3 direction = ResolveWorldLaunchDirection();
        float arrowLength = Mathf.Clamp(launchStrength * 0.25f, 1f, 5f);
        Vector3 end = origin + direction * arrowLength;

        Gizmos.color = new Color(0.1f, 0.85f, 1f, 1f);
        Gizmos.DrawLine(origin, end);
        DrawArrowHead(end, direction, Mathf.Min(0.45f, arrowLength * 0.25f));
    }

    private static void DrawArrowHead(Vector3 tip, Vector3 direction, float size)
    {
        // Draw four simple arrowhead lines without requiring an editor-only custom inspector.
        Vector3 side = Vector3.Cross(direction, Vector3.up);
        if (side.sqrMagnitude <= 0.0001f)
        {
            side = Vector3.Cross(direction, Vector3.right);
        }

        side.Normalize();
        Vector3 verticalSide = Vector3.Cross(direction, side).normalized;
        Vector3 basePoint = tip - direction * size;
        float width = size * 0.55f;

        Gizmos.DrawLine(tip, basePoint + side * width);
        Gizmos.DrawLine(tip, basePoint - side * width);
        Gizmos.DrawLine(tip, basePoint + verticalSide * width);
        Gizmos.DrawLine(tip, basePoint - verticalSide * width);
    }
}
