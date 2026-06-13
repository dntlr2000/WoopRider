using System.Collections;
using TMPro;
using UnityEngine;

public class NoticePanelBinder : MonoBehaviour
{
    private const string DefaultPanelObjectName = "NoticePanel";
    private const string DefaultTextObjectName = "NoticeText";

    private static NoticePanelBinder instance;

    [Header("Bindings")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private TMP_Text noticeText;

    [Header("Timing")]
    [SerializeField] private float defaultDuration = 4f;
    [SerializeField] private bool hideOnAwake = true;
    [SerializeField] private bool clearTextWhenHidden = true;

    [Header("Fade")]
    [SerializeField] private CanvasGroup noticeCanvasGroup;
    [SerializeField] private bool useFade = true;
    [SerializeField] private float fadeInDuration = 0.2f;
    [SerializeField] private float fadeOutDuration = 0.3f;
    [SerializeField] private bool useUnscaledTime = true;
    [SerializeField] private bool blocksRaycastsWhileVisible = false;

    private Coroutine hideRoutine;
    private bool isShowingNotice;

    private void Awake()
    {
        // Register the scene notice UI and optionally start hidden for normal gameplay.
        RegisterInstance();

        if (hideOnAwake && !isShowingNotice)
        {
            HideImmediate();
        }
    }

    private void OnEnable()
    {
        // Re-register when the binder lives on an inactive panel that becomes active later.
        RegisterInstance();
    }

    private void OnDestroy()
    {
        // Clear the static reference when this scene UI is destroyed.
        if (instance == this)
        {
            instance = null;
        }
    }

    public static void ShowNotice(string message, float duration)
    {
        // Static entry point used by network RPCs to display a local notice.
        NoticePanelBinder binder = ResolveInstance();
        if (binder == null)
        {
            Debug.LogWarning($"[NoticePanelBinder] Notice ignored because no binder exists. message={message}");
            return;
        }

        binder.Show(message, duration);
    }

    public void Show(string message, float duration)
    {
        // Activate the notice panel, set text, and restart the fade/display/hide sequence.
        isShowingNotice = true;

        EnsureCanvasGroup();
        SetNoticeAlpha(useFade ? 0f : 1f);

        if (noticeText != null)
        {
            noticeText.text = message ?? string.Empty;
        }

        SetPanelVisible(true);
        isShowingNotice = false;

        if (hideRoutine != null)
        {
            StopCoroutine(hideRoutine);
        }

        float resolvedDuration = duration > 0f ? duration : Mathf.Max(0f, defaultDuration);
        hideRoutine = StartCoroutine(ShowThenHideAfterDelay(resolvedDuration));
    }

    public void Hide()
    {
        // Hide the notice panel and clear the current timer.
        if (hideRoutine != null)
        {
            StopCoroutine(hideRoutine);
            hideRoutine = null;
        }

        HideImmediate();
    }

    private static NoticePanelBinder ResolveInstance()
    {
        // Find an active or inactive binder so disabled NoticePanel objects can still receive RPC notices.
        if (instance != null)
        {
            return instance;
        }

        instance = FindFirstObjectByType<NoticePanelBinder>(FindObjectsInactive.Include);
        if (instance == null)
        {
            instance = CreateFromNamedSceneObjects();
        }

        return instance;
    }

    private static NoticePanelBinder CreateFromNamedSceneObjects()
    {
        // Auto-bind a conventionally named NoticePanel/NoticeText pair when no binder was assigned.
        GameObject panelObject = FindSceneObjectByName(DefaultPanelObjectName);
        if (panelObject == null)
        {
            return null;
        }

        NoticePanelBinder binder = panelObject.GetComponent<NoticePanelBinder>();
        if (binder == null)
        {
            binder = panelObject.AddComponent<NoticePanelBinder>();
        }

        binder.panelRoot = panelObject;
        binder.noticeText = FindNoticeText(panelObject.transform);
        return binder;
    }

    private static GameObject FindSceneObjectByName(string objectName)
    {
        // Search loaded scene objects by name, including inactive UI objects.
        GameObject[] sceneObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        for (int i = 0; i < sceneObjects.Length; i++)
        {
            GameObject sceneObject = sceneObjects[i];
            if (sceneObject != null &&
                sceneObject.name == objectName &&
                sceneObject.scene.IsValid() &&
                sceneObject.scene.isLoaded)
            {
                return sceneObject;
            }
        }

        return null;
    }

    private static TMP_Text FindNoticeText(Transform panelTransform)
    {
        // Prefer the NoticeText child, then fall back to the first TMP text under the panel.
        if (panelTransform == null)
        {
            return null;
        }

        TMP_Text[] textComponents = panelTransform.GetComponentsInChildren<TMP_Text>(true);
        TMP_Text fallbackText = null;
        for (int i = 0; i < textComponents.Length; i++)
        {
            TMP_Text textComponent = textComponents[i];
            if (textComponent == null)
            {
                continue;
            }

            if (fallbackText == null)
            {
                fallbackText = textComponent;
            }

            if (textComponent.name == DefaultTextObjectName)
            {
                return textComponent;
            }
        }

        return fallbackText;
    }

    private void EnsureCanvasGroup()
    {
        // Resolve or create the CanvasGroup used to fade the notice panel.
        if (noticeCanvasGroup != null)
        {
            return;
        }

        GameObject targetPanel = panelRoot != null ? panelRoot : gameObject;
        if (targetPanel == null)
        {
            return;
        }

        noticeCanvasGroup = targetPanel.GetComponent<CanvasGroup>();
        if (noticeCanvasGroup == null)
        {
            noticeCanvasGroup = targetPanel.AddComponent<CanvasGroup>();
        }
    }

    private void RegisterInstance()
    {
        // Prefer the first scene binder and ignore duplicate UI binders.
        if (instance == null || instance == this)
        {
            instance = this;
        }
    }

    private IEnumerator ShowThenHideAfterDelay(float duration)
    {
        // Fade the notice in, keep it visible, then fade it out before hiding the panel.
        if (useFade && fadeInDuration > 0f)
        {
            yield return FadeNotice(0f, 1f, fadeInDuration);
        }
        else
        {
            SetNoticeAlpha(1f);
        }

        if (duration > 0f)
        {
            yield return WaitForNoticeSeconds(duration);
        }
        else
        {
            hideRoutine = null;
            yield break;
        }

        if (useFade && fadeOutDuration > 0f)
        {
            yield return FadeNotice(GetNoticeAlpha(), 0f, fadeOutDuration);
        }
        else
        {
            SetNoticeAlpha(0f);
        }

        hideRoutine = null;
        HideImmediate();
    }

    private IEnumerator FadeNotice(float fromAlpha, float toAlpha, float duration)
    {
        // Interpolate CanvasGroup alpha over time for notice fade transitions.
        float elapsed = 0f;
        float safeDuration = Mathf.Max(0.0001f, duration);
        while (elapsed < safeDuration)
        {
            elapsed += GetNoticeDeltaTime();
            float t = Mathf.Clamp01(elapsed / safeDuration);
            SetNoticeAlpha(Mathf.Lerp(fromAlpha, toAlpha, t));
            yield return null;
        }

        SetNoticeAlpha(toAlpha);
    }

    private IEnumerator WaitForNoticeSeconds(float duration)
    {
        // Wait using scaled or unscaled time depending on the UI timing setting.
        if (!useUnscaledTime)
        {
            yield return new WaitForSeconds(duration);
            yield break;
        }

        yield return new WaitForSecondsRealtime(duration);
    }

    private void HideImmediate()
    {
        // Apply the hidden panel state without touching the current timer.
        SetNoticeAlpha(0f);

        if (noticeText != null && clearTextWhenHidden)
        {
            noticeText.text = string.Empty;
        }

        SetPanelVisible(false);
    }

    private void SetPanelVisible(bool visible)
    {
        // Toggle the configured panel root, or this GameObject if no root was assigned.
        GameObject targetPanel = panelRoot != null ? panelRoot : gameObject;
        if (targetPanel != null && targetPanel.activeSelf != visible)
        {
            targetPanel.SetActive(visible);
        }
    }

    private float GetNoticeAlpha()
    {
        // Read the current notice alpha, defaulting to fully visible without a CanvasGroup.
        EnsureCanvasGroup();
        return noticeCanvasGroup != null ? noticeCanvasGroup.alpha : 1f;
    }

    private void SetNoticeAlpha(float alpha)
    {
        // Apply fade alpha and raycast behavior to the notice CanvasGroup.
        EnsureCanvasGroup();
        if (noticeCanvasGroup == null)
        {
            return;
        }

        noticeCanvasGroup.alpha = Mathf.Clamp01(alpha);
        noticeCanvasGroup.interactable = false;
        noticeCanvasGroup.blocksRaycasts = blocksRaycastsWhileVisible && noticeCanvasGroup.alpha > 0f;
    }

    private float GetNoticeDeltaTime()
    {
        // Return the timing source used by notice fade animations.
        return useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
    }
}
