using System.Collections.Generic;
using UnityEngine;

public class SimpleHookVisual : MonoBehaviour
{
    private static readonly Color HookColor = new(0.55f, 0.9f, 1f, 1f);
    private static readonly Dictionary<int, SimpleHookVisual> ActiveVisualsById = new();

    private int hookId;
    private LineRenderer lineRenderer;
    private Transform hookTip;
    private Vector3 origin;
    private Vector3 equipmentPosition;
    private Vector3 returnPosition;
    private float outboundDuration;
    private float returnDuration;
    private float startTime;

    public static void Spawn(int hookId, Vector3 origin, Vector3 equipmentPosition, Vector3 returnPosition, float speed)
    {
        // Create a temporary hook tether visual until a real hook prefab or effect is ready.
        GameObject hookObject = new("HookVisual");
        SimpleHookVisual hookVisual = hookObject.AddComponent<SimpleHookVisual>();
        hookVisual.Initialize(hookId, origin, equipmentPosition, returnPosition, speed);
    }

    public static void Latch(int hookId, Vector3 latchPosition, Vector3 returnPosition, float speed)
    {
        // Retarget an existing hook visual when the server confirms it touched equipment.
        if (ActiveVisualsById.TryGetValue(hookId, out SimpleHookVisual hookVisual) && hookVisual != null)
        {
            hookVisual.StartReturnFromLatch(latchPosition, returnPosition, speed);
        }
    }

    private void Initialize(int newHookId, Vector3 startOrigin, Vector3 targetEquipmentPosition, Vector3 targetReturnPosition, float speed)
    {
        // Build a simple line and moving tip that travel out to the equipment and back to the player.
        hookId = newHookId;
        origin = startOrigin;
        equipmentPosition = targetEquipmentPosition;
        returnPosition = targetReturnPosition;
        float resolvedSpeed = Mathf.Max(0.1f, speed);
        outboundDuration = Vector3.Distance(origin, equipmentPosition) / resolvedSpeed;
        returnDuration = Vector3.Distance(equipmentPosition, returnPosition) / resolvedSpeed;
        startTime = Time.time;
        ActiveVisualsById[hookId] = this;

        lineRenderer = gameObject.AddComponent<LineRenderer>();
        lineRenderer.positionCount = 2;
        lineRenderer.startWidth = 0.045f;
        lineRenderer.endWidth = 0.02f;
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.startColor = HookColor;
        lineRenderer.endColor = HookColor;

        GameObject tipObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        tipObject.name = "HookTipVisual";
        tipObject.transform.localScale = Vector3.one * 0.16f;
        tipObject.transform.SetParent(transform, worldPositionStays: true);
        if (tipObject.TryGetComponent(out Collider tipCollider))
        {
            tipCollider.enabled = false;
        }

        if (tipObject.TryGetComponent(out Renderer tipRenderer))
        {
            tipRenderer.material.color = HookColor;
        }

        hookTip = tipObject.transform;
        UpdateVisual(origin);
    }

    private void OnDestroy()
    {
        // Remove this temporary visual from the latch lookup when it expires.
        if (ActiveVisualsById.TryGetValue(hookId, out SimpleHookVisual hookVisual) && hookVisual == this)
        {
            ActiveVisualsById.Remove(hookId);
        }
    }

    private void Update()
    {
        // Animate the temporary hook tip along a two-phase out-and-return path.
        float elapsed = Time.time - startTime;
        if (elapsed <= outboundDuration)
        {
            float t = outboundDuration <= 0.001f ? 1f : elapsed / outboundDuration;
            UpdateVisual(Vector3.Lerp(origin, equipmentPosition, t));
            return;
        }

        float returnElapsed = elapsed - outboundDuration;
        if (returnElapsed <= returnDuration)
        {
            float t = returnDuration <= 0.001f ? 1f : returnElapsed / returnDuration;
            UpdateVisual(Vector3.Lerp(equipmentPosition, returnPosition, t));
            return;
        }

        Destroy(gameObject);
    }

    private void StartReturnFromLatch(Vector3 latchPosition, Vector3 targetReturnPosition, float speed)
    {
        // Stop the outbound animation at the latched equipment point and begin the return phase immediately.
        equipmentPosition = latchPosition;
        returnPosition = targetReturnPosition;
        outboundDuration = 0f;
        returnDuration = Vector3.Distance(equipmentPosition, returnPosition) / Mathf.Max(0.1f, speed);
        startTime = Time.time;
        UpdateVisual(equipmentPosition);
    }

    private void UpdateVisual(Vector3 tipPosition)
    {
        // Keep the line connected to the firing origin and the animated hook tip.
        if (lineRenderer != null)
        {
            lineRenderer.SetPosition(0, origin);
            lineRenderer.SetPosition(1, tipPosition);
        }

        if (hookTip != null)
        {
            hookTip.position = tipPosition;
        }
    }
}
