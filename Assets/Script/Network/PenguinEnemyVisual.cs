using UnityEngine;

public class PenguinEnemyVisual : MonoBehaviour
{
    private static readonly int MoveSpeedHash = Animator.StringToHash("MoveSpeed");
    private static readonly int IsDeadHash = Animator.StringToHash("IsDead");
    private static readonly int DeathStateHash = Animator.StringToHash("Base Layer.death");

    private Animator animator;
    private Vector3 targetPosition;
    private Quaternion targetRotation = Quaternion.identity;
    private float positionSharpness = 14f;
    private float rotationSpeed = 540f;
    private Transform visualContentRoot;
    private bool hasMoveSpeedParameter;
    private bool hasIsDeadParameter;
    private bool dead;

    public void Configure(Vector3 visualScale, float newPositionSharpness, float newRotationSpeed)
    {
        // Cache animation support and configure smoothing for this pooled client-side visual.
        transform.localScale = visualScale;
        positionSharpness = Mathf.Max(0.01f, newPositionSharpness);
        rotationSpeed = Mathf.Max(0f, newRotationSpeed);
        CenterVisualContentOnRuntimeRoot();
        animator = GetComponentInChildren<Animator>(true);
        if (animator != null)
        {
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        CacheAnimatorParameters();
        DisableVisualPhysics();
    }

    public void ShowAlive(Vector3 position, Vector3 forward)
    {
        // Reactivate a pooled Penguin and reset its one-shot death animation state.
        gameObject.SetActive(true);
        dead = false;
        targetPosition = position;
        targetRotation = ResolveRotation(forward, transform.rotation);
        transform.SetPositionAndRotation(targetPosition, targetRotation);

        if (animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
            SetAnimatorBool(IsDeadHash, hasIsDeadParameter, false);
            SetAnimatorFloat(0f);
        }

        RecenterVisualContentFromCurrentPose();
    }

    public void SetNetworkState(Vector3 position, Vector3 forward, float moveSpeed, bool snap)
    {
        // Receive a server-authored movement sample and update interpolation and locomotion animation.
        if (!gameObject.activeSelf)
        {
            gameObject.SetActive(true);
        }

        targetPosition = position;
        targetRotation = ResolveRotation(forward, targetRotation);
        if (snap)
        {
            transform.SetPositionAndRotation(targetPosition, targetRotation);
        }

        if (!dead)
        {
            SetAnimatorFloat(Mathf.Max(0f, moveSpeed));
        }
    }

    public void PlayDeath(Vector3 position, Vector3 forward)
    {
        // Freeze locomotion at the final server pose and enter the persistent death state.
        bool wasActive = gameObject.activeSelf;
        gameObject.SetActive(true);
        dead = true;
        targetPosition = position;
        targetRotation = ResolveRotation(forward, targetRotation);
        transform.SetPositionAndRotation(targetPosition, targetRotation);
        if (!wasActive && animator != null)
        {
            animator.Rebind();
            animator.Update(0f);
        }

        SetAnimatorFloat(0f);
        SetAnimatorBool(IsDeadHash, hasIsDeadParameter, true);
        if (animator != null && animator.HasState(0, DeathStateHash))
        {
            animator.Play(DeathStateHash, 0, 0f);
            animator.Update(0f);
        }
    }

    public void Hide()
    {
        // Return this local visual to its inactive pooled state.
        dead = false;
        gameObject.SetActive(false);
    }

    private void Update()
    {
        // Smooth sparse server samples while leaving all gameplay authority on the server.
        if (dead)
        {
            return;
        }

        float positionBlend = 1f - Mathf.Exp(-positionSharpness * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, targetPosition, positionBlend);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
    }

    private void CacheAnimatorParameters()
    {
        // Detect optional controller parameters so regenerated or replacement prefabs remain compatible.
        hasMoveSpeedParameter = false;
        hasIsDeadParameter = false;
        if (animator == null)
        {
            return;
        }

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];
            if (parameter.nameHash == MoveSpeedHash && parameter.type == AnimatorControllerParameterType.Float)
            {
                hasMoveSpeedParameter = true;
            }
            else if (parameter.nameHash == IsDeadHash && parameter.type == AnimatorControllerParameterType.Bool)
            {
                hasIsDeadParameter = true;
            }
        }
    }

    private void CenterVisualContentOnRuntimeRoot()
    {
        // Recenter offset FBX content horizontally and place its rendered bottom on the event root.
        Transform[] directChildren = new Transform[transform.childCount];
        for (int i = 0; i < directChildren.Length; i++)
        {
            directChildren[i] = transform.GetChild(i);
        }

        if (directChildren.Length == 0)
        {
            return;
        }

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (!TryCalculateRootLocalBounds(renderers, out Bounds localBounds))
        {
            return;
        }

        GameObject contentRootObject = new("PenguinVisualContent");
        Transform contentRoot = contentRootObject.transform;
        contentRoot.SetParent(transform, false);
        for (int i = 0; i < directChildren.Length; i++)
        {
            directChildren[i].SetParent(contentRoot, false);
        }

        contentRoot.localPosition = new Vector3(-localBounds.center.x, -localBounds.min.y, -localBounds.center.z);
        visualContentRoot = contentRoot;
    }

    private void RecenterVisualContentFromCurrentPose()
    {
        // Remove residual pivot and ground offsets after Animator.Rebind establishes the actual visible idle pose.
        if (visualContentRoot == null)
        {
            return;
        }

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return;
        }

        Bounds worldBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            worldBounds.Encapsulate(renderers[i].bounds);
        }

        Vector3 localCenter = transform.InverseTransformPoint(worldBounds.center);
        Vector3 localBottom = transform.InverseTransformPoint(new Vector3(worldBounds.center.x, worldBounds.min.y, worldBounds.center.z));
        visualContentRoot.localPosition += new Vector3(-localCenter.x, -localBottom.y, -localCenter.z);
    }

    private bool TryCalculateRootLocalBounds(Renderer[] renderers, out Bounds rootLocalBounds)
    {
        // Transform renderer-local bound corners directly into root space without relying on stale world bounds.
        rootLocalBounds = default;
        bool hasPoint = false;
        if (renderers == null)
        {
            return false;
        }

        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null)
            {
                continue;
            }

            Bounds rendererBounds = renderer.localBounds;
            Matrix4x4 rootFromRenderer = transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Vector3 min = rendererBounds.min;
            Vector3 max = rendererBounds.max;
            for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
            {
                Vector3 rendererPoint = new(
                    (cornerIndex & 1) == 0 ? min.x : max.x,
                    (cornerIndex & 2) == 0 ? min.y : max.y,
                    (cornerIndex & 4) == 0 ? min.z : max.z);
                Vector3 rootPoint = rootFromRenderer.MultiplyPoint3x4(rendererPoint);
                if (!hasPoint)
                {
                    rootLocalBounds = new Bounds(rootPoint, Vector3.zero);
                    hasPoint = true;
                }
                else
                {
                    rootLocalBounds.Encapsulate(rootPoint);
                }
            }
        }

        return hasPoint;
    }

    private void DisableVisualPhysics()
    {
        // Keep local presentation objects from interfering with authoritative projectile and world collision tests.
        Collider[] colliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }

        Rigidbody[] rigidbodies = GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < rigidbodies.Length; i++)
        {
            rigidbodies[i].isKinematic = true;
            rigidbodies[i].detectCollisions = false;
        }
    }

    private void SetAnimatorFloat(float value)
    {
        // Drive idle and walk transitions only when the assigned controller exposes MoveSpeed.
        if (animator != null && hasMoveSpeedParameter)
        {
            animator.SetFloat(MoveSpeedHash, value);
        }
    }

    private void SetAnimatorBool(int parameterHash, bool parameterExists, bool value)
    {
        // Safely set a bool without producing Animator warnings on replacement controllers.
        if (animator != null && parameterExists)
        {
            animator.SetBool(parameterHash, value);
        }
    }

    private static Quaternion ResolveRotation(Vector3 forward, Quaternion fallback)
    {
        // Convert a horizontal travel direction into a stable upright world rotation.
        forward.y = 0f;
        return forward.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(forward.normalized, Vector3.up)
            : fallback;
    }
}
