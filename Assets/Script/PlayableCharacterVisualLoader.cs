using UnityEngine;

public class PlayableCharacterVisualLoader : MonoBehaviour
{
    [Header("Resources")]
    [SerializeField] private string visualPrefabResourcePath = "fbx/Bangae_Playable";
    [SerializeField] private string animatorControllerResourcePath = "PlayableCharacters/Bangae_Playable";
    [SerializeField] private bool addAnimatorWhenMissing = true;

    [Header("Transform")]
    [SerializeField] private string visualRootName = "BangaePlayableVisual";
    [SerializeField] private Vector3 localPosition = new(0f, -0.5f, 0f);
    [SerializeField] private Vector3 localEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 localScale = new(1.5f, 1.5f, 1.5f);

    [Header("Existing Visual")]
    [SerializeField] private bool hideRootRenderers = true;
    [SerializeField] private bool destroyExistingVisual = true;
    [SerializeField] private bool skipInBatchMode = true;

    private GameObject visualInstance;
    private Animator visualAnimator;

    public GameObject VisualInstance => visualInstance;
    public Animator Animator => ResolveAnimator();
    public bool IsVisualReady => ResolveAnimator() != null && ResolveAnimator().runtimeAnimatorController != null;

    private void Awake()
    {
        // Build the configured playable character visual as soon as the player object awakens.
        if (skipInBatchMode && Application.isBatchMode)
        {
            return;
        }

        LoadVisual();
    }

    public void LoadVisual()
    {
        // Replace the temporary primitive visual with the configured Resources character model.
        if (hideRootRenderers)
        {
            HideRootRenderers();
        }

        if (destroyExistingVisual)
        {
            DestroyExistingVisualRoot();
        }

        GameObject visualPrefab = Resources.Load<GameObject>(visualPrefabResourcePath);
        if (visualPrefab == null)
        {
            Debug.LogWarning($"[PlayableCharacterVisualLoader] Visual prefab not found at Resources/{visualPrefabResourcePath}.");
            return;
        }

        visualInstance = Instantiate(visualPrefab, transform);
        visualInstance.name = visualRootName;
        visualInstance.transform.SetLocalPositionAndRotation(localPosition, Quaternion.Euler(localEulerAngles));
        visualInstance.transform.localScale = localScale;

        ApplyAnimatorController();
    }

    private Animator ResolveAnimator()
    {
        // Return the cached visual Animator, resolving or creating it from the spawned model if needed.
        if (visualAnimator != null)
        {
            return visualAnimator;
        }

        if (visualInstance != null)
        {
            visualAnimator = visualInstance.GetComponentInChildren<Animator>(true);
        }

        if (visualAnimator == null)
        {
            visualAnimator = GetComponentInChildren<Animator>(true);
        }

        if (visualAnimator == null && visualInstance != null)
        {
            if (!addAnimatorWhenMissing)
            {
                Debug.LogWarning("[PlayableCharacterVisualLoader] Visual has no Animator, so a runtime Animator is being added for playable character animation.");
            }

            visualAnimator = visualInstance.AddComponent<Animator>();
        }

        return visualAnimator;
    }

    private void ApplyAnimatorController()
    {
        // Assign the generated Bangae AnimatorController to the loaded model Animator.
        RuntimeAnimatorController controller = Resources.Load<RuntimeAnimatorController>(animatorControllerResourcePath);
        if (controller == null)
        {
            Debug.LogWarning($"[PlayableCharacterVisualLoader] Animator controller not found at Resources/{animatorControllerResourcePath}.");
            return;
        }

        Animator animator = ResolveAnimator();
        if (animator == null)
        {
            Debug.LogWarning("[PlayableCharacterVisualLoader] Loaded visual has no Animator component.");
            return;
        }

        animator.runtimeAnimatorController = controller;
        AssignAvatarIfNeeded(animator);
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Rebind();
        animator.Update(0f);
    }

    private void AssignAvatarIfNeeded(Animator animator)
    {
        // Assign the FBX avatar sub-asset when Unity did not put one on the runtime Animator.
        if (animator == null || animator.avatar != null)
        {
            return;
        }

        Avatar[] avatars = Resources.LoadAll<Avatar>(visualPrefabResourcePath);
        for (int i = 0; i < avatars.Length; i++)
        {
            if (avatars[i] != null)
            {
                animator.avatar = avatars[i];
                return;
            }
        }
    }

    private void HideRootRenderers()
    {
        // Disable primitive renderers that live on the gameplay root object.
        Renderer[] rootRenderers = GetComponents<Renderer>();
        for (int i = 0; i < rootRenderers.Length; i++)
        {
            if (rootRenderers[i] != null)
            {
                rootRenderers[i].enabled = false;
            }
        }
    }

    private void DestroyExistingVisualRoot()
    {
        // Remove a previous generated visual child before creating a fresh instance.
        Transform existingVisual = transform.Find(visualRootName);
        if (existingVisual == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(existingVisual.gameObject);
        }
        else
        {
            DestroyImmediate(existingVisual.gameObject);
        }
    }
}
