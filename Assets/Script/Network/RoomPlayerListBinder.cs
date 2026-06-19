using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public class RoomPlayerListBinder : MonoBehaviour
{
    private const string DefaultNoticeTextObjectName = "NoticeText";

    [Header("Panel")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private Transform contentRoot;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private GameObject rowPrefab;

    [Header("Font")]
    [SerializeField] private TMP_FontAsset generatedTextFontAsset;
    [SerializeField] private TMP_Text fontReferenceText;

    [Header("Text")]
    [SerializeField] private string titleFormat = "Players ({0})";
    [SerializeField] private string hostSuffix = " (Host)";
    [SerializeField] private string localSuffix = " (You)";
    [SerializeField] private string kickButtonText = "Kick";
    [SerializeField] private string fallbackPlayerFormat = "Player {0}";

    [Header("Options")]
    [SerializeField] private bool hideWhenOffline = true;
    [SerializeField] private bool showOnlyInLobby = true;

    [Header("Generated Layout")]
    [SerializeField] private bool ensureVerticalLayoutOnContent = true;
    [SerializeField] private float generatedRowHeight = 32f;
    [SerializeField] private float generatedRowSpacing = 4f;

    private readonly List<RowView> rows = new();
    private RoomPlayerRegistry observedRegistry;
    private RoomHostAuthority observedHostAuthority;
    private MatchStateController observedMatchStateController;
    private bool registrySubscribed;
    private bool hostAuthoritySubscribed;
    private bool matchStateSubscribed;
    private bool needsRefresh = true;
    private CanvasGroup panelCanvasGroup;
    private TMP_FontAsset cachedSceneNoticeFontAsset;

    private sealed class RowView
    {
        public GameObject Root;
        public TMP_Text LabelText;
        public Button KickButton;
        public TMP_Text KickButtonLabel;
    }

    private void OnEnable()
    {
        // Bind to room list replication when this UI becomes active.
        BindSources();
        MarkDirty();
    }

    private void OnDisable()
    {
        // Avoid duplicate event subscriptions while the UI is hidden or destroyed.
        UnbindSources();
    }

    private void Update()
    {
        // Network scene objects can spawn after this Canvas, so keep bindings fresh.
        BindSources();
        if (needsRefresh)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        // Rebuild the visible player rows from the replicated room list.
        needsRefresh = false;
        bool canShow = CanShowPanel();
        SetPanelVisible(canShow);
        if (!canShow)
        {
            EnsureRowCount(0);
            return;
        }

        int playerCount = observedRegistry.Players.Count;
        if (titleText != null)
        {
            ApplyFontToText(titleText);
            titleText.text = string.Format(titleFormat, playerCount);
        }

        EnsureRowCount(playerCount);
        for (int i = 0; i < playerCount; i++)
        {
            BindRow(rows[i], observedRegistry.Players[i]);
        }
    }

    private void BindSources()
    {
        // Subscribe to the current registry and host authority, replacing stale references after reconnects.
        RoomPlayerRegistry nextRegistry = RoomPlayerRegistry.Instance;
        if (observedRegistry != nextRegistry)
        {
            UnbindRegistry();
            observedRegistry = nextRegistry;
            MarkDirty();
        }

        if (!registrySubscribed &&
            observedRegistry != null &&
            observedRegistry.IsSpawned &&
            observedRegistry.Players != null)
        {
            observedRegistry.Players.OnListChanged += OnPlayerListChanged;
            registrySubscribed = true;
            MarkDirty();
        }

        RoomHostAuthority nextHostAuthority = RoomHostAuthority.Instance;
        if (observedHostAuthority != nextHostAuthority)
        {
            UnbindHostAuthority();
            observedHostAuthority = nextHostAuthority;
            MarkDirty();
        }

        if (!hostAuthoritySubscribed &&
            observedHostAuthority != null &&
            observedHostAuthority.IsSpawned)
        {
            observedHostAuthority.HostClientId.OnValueChanged += OnHostChanged;
            hostAuthoritySubscribed = true;
            MarkDirty();
        }

        MatchStateController nextMatchStateController = MatchStateController.Instance;
        if (observedMatchStateController != nextMatchStateController)
        {
            UnbindMatchStateController();
            observedMatchStateController = nextMatchStateController;
            MarkDirty();
        }

        if (!matchStateSubscribed &&
            observedMatchStateController != null &&
            observedMatchStateController.IsSpawned)
        {
            observedMatchStateController.State.OnValueChanged += OnMatchStateChanged;
            matchStateSubscribed = true;
            MarkDirty();
        }
    }

    private void UnbindSources()
    {
        // Remove all network event listeners owned by this UI binder.
        UnbindRegistry();
        UnbindHostAuthority();
        UnbindMatchStateController();
    }

    private void UnbindRegistry()
    {
        // Stop listening to the previous room list.
        if (observedRegistry != null && observedRegistry.Players != null)
        {
            observedRegistry.Players.OnListChanged -= OnPlayerListChanged;
        }

        registrySubscribed = false;
        observedRegistry = null;
    }

    private void UnbindHostAuthority()
    {
        // Stop listening to the previous host id variable.
        if (observedHostAuthority != null && observedHostAuthority.IsSpawned)
        {
            observedHostAuthority.HostClientId.OnValueChanged -= OnHostChanged;
        }

        hostAuthoritySubscribed = false;
        observedHostAuthority = null;
    }

    private void UnbindMatchStateController()
    {
        // Stop listening to the previous match state controller.
        if (observedMatchStateController != null && observedMatchStateController.IsSpawned)
        {
            observedMatchStateController.State.OnValueChanged -= OnMatchStateChanged;
        }

        matchStateSubscribed = false;
        observedMatchStateController = null;
    }

    private void OnPlayerListChanged(NetworkListEvent<RoomPlayerEntry> changeEvent)
    {
        // Defer UI changes to Update so multiple network list changes collapse into one refresh.
        MarkDirty();
    }

    private void OnHostChanged(ulong previousHostClientId, ulong currentHostClientId)
    {
        // Refresh Kick button visibility whenever room host ownership changes.
        MarkDirty();
    }

    private void OnMatchStateChanged(NetworkMatchState previousState, NetworkMatchState currentState)
    {
        // Refresh panel visibility whenever the room leaves or returns to the lobby.
        MarkDirty();
    }

    private void EnsureRowCount(int count)
    {
        // Grow or shrink the local row objects to match the replicated player count.
        Transform parent = ResolveContentRoot();
        EnsureContentLayout(parent);
        while (rows.Count < count)
        {
            rows.Add(CreateRow(parent));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Root != null)
            {
                rows[i].Root.SetActive(i < count);
            }
        }
    }

    private RowView CreateRow(Transform parent)
    {
        // Instantiate a custom row prefab or build a simple text/button row for quick testing.
        GameObject rowObject = rowPrefab != null
            ? Instantiate(rowPrefab, parent)
            : CreateDefaultRow(parent);
        rowObject.name = "RoomPlayerRow";
        EnsureRowLayout(rowObject);
        ApplyFontToRowTexts(rowObject);

        TMP_Text[] texts = rowObject.GetComponentsInChildren<TMP_Text>(true);
        Button kickButton = rowObject.GetComponentInChildren<Button>(true);
        TMP_Text kickButtonLabel = kickButton != null
            ? kickButton.GetComponentInChildren<TMP_Text>(true)
            : null;

        return new RowView
        {
            Root = rowObject,
            LabelText = ResolveLabelText(texts, kickButtonLabel),
            KickButton = kickButton,
            KickButtonLabel = kickButtonLabel
        };
    }

    private GameObject CreateDefaultRow(Transform parent)
    {
        // Create a compact generated row so the binder works before final UI art exists.
        GameObject rowObject = new("RoomPlayerRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        rowObject.transform.SetParent(parent, false);

        HorizontalLayoutGroup layout = rowObject.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = false;
        layout.childAlignment = TextAnchor.MiddleLeft;

        GameObject labelObject = new("PlayerLabel", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        labelObject.transform.SetParent(rowObject.transform, false);
        TMP_Text label = labelObject.GetComponent<TMP_Text>();
        label.text = "Player";
        label.fontSize = 18f;
        ApplyFontToText(label);
        LayoutElement labelLayout = labelObject.GetComponent<LayoutElement>();
        labelLayout.minWidth = 160f;
        labelLayout.flexibleWidth = 1f;

        GameObject buttonObject = new("KickButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(rowObject.transform, false);
        Image image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.75f, 0.18f, 0.18f, 0.9f);
        LayoutElement buttonLayout = buttonObject.GetComponent<LayoutElement>();
        buttonLayout.minWidth = 72f;
        buttonLayout.minHeight = 28f;

        GameObject buttonTextObject = new("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        buttonTextObject.transform.SetParent(buttonObject.transform, false);
        RectTransform buttonTextTransform = buttonTextObject.GetComponent<RectTransform>();
        buttonTextTransform.anchorMin = Vector2.zero;
        buttonTextTransform.anchorMax = Vector2.one;
        buttonTextTransform.offsetMin = Vector2.zero;
        buttonTextTransform.offsetMax = Vector2.zero;
        TMP_Text buttonText = buttonTextObject.GetComponent<TMP_Text>();
        buttonText.text = kickButtonText;
        buttonText.fontSize = 16f;
        buttonText.alignment = TextAlignmentOptions.Center;
        ApplyFontToText(buttonText);

        return rowObject;
    }

    private void EnsureContentLayout(Transform parent)
    {
        // Add a vertical layout to plain RawImage/panel content roots so generated rows do not overlap.
        if (!ensureVerticalLayoutOnContent || parent == null || parent.GetComponent<LayoutGroup>() != null)
        {
            return;
        }

        VerticalLayoutGroup layout = parent.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = Mathf.Max(0f, generatedRowSpacing);
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
    }

    private void EnsureRowLayout(GameObject rowObject)
    {
        // Give generated or prefab rows a stable layout height when the parent uses a LayoutGroup.
        if (rowObject == null)
        {
            return;
        }

        LayoutElement layoutElement = rowObject.GetComponent<LayoutElement>();
        if (layoutElement == null)
        {
            layoutElement = rowObject.AddComponent<LayoutElement>();
        }

        layoutElement.minHeight = Mathf.Max(1f, generatedRowHeight);
        layoutElement.preferredHeight = Mathf.Max(1f, generatedRowHeight);
    }

    private void BindRow(RowView row, RoomPlayerEntry entry)
    {
        // Populate one row and wire its Kick button to the current target client id.
        if (row == null)
        {
            return;
        }

        bool isHost = IsRoomHost(entry.ClientId);
        bool isLocal = IsLocalClient(entry.ClientId);
        bool canKick = CanLocalKick(entry.ClientId);

        if (row.LabelText != null)
        {
            ApplyFontToText(row.LabelText);
            row.LabelText.text = FormatPlayerLabel(entry, isHost, isLocal);
        }

        if (row.KickButtonLabel != null)
        {
            ApplyFontToText(row.KickButtonLabel);
            row.KickButtonLabel.text = kickButtonText;
        }

        if (row.KickButton != null)
        {
            row.KickButton.onClick.RemoveAllListeners();
            ulong targetClientId = entry.ClientId;
            row.KickButton.onClick.AddListener(() => RequestKick(targetClientId));
            row.KickButton.gameObject.SetActive(IsLocalRoomHost());
            row.KickButton.interactable = canKick;
        }
    }

    private string FormatPlayerLabel(RoomPlayerEntry entry, bool isHost, bool isLocal)
    {
        // Build the row label with host/local suffixes while keeping display-name support centralized.
        string label = entry.DisplayName.IsEmpty
            ? string.Format(fallbackPlayerFormat, entry.ClientId)
            : entry.DisplayName.ToString();
        if (isHost)
        {
            label += hostSuffix;
        }

        if (isLocal)
        {
            label += localSuffix;
        }

        return label;
    }

    private void RequestKick(ulong targetClientId)
    {
        // Forward a row button click to the authoritative room controller.
        MatchStateController controller = MatchStateController.Instance;
        if (controller != null && controller.IsSpawned)
        {
            controller.RequestKickClient(targetClientId);
        }
    }

    private bool CanShowPanel()
    {
        // Hide optional room-list UI before the client is connected unless configured otherwise.
        if (!CanShowForCurrentMatchState())
        {
            return false;
        }

        if (!hideWhenOffline)
        {
            return true;
        }

        return NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsListening &&
            observedRegistry != null &&
            observedRegistry.IsSpawned;
    }

    private void SetPanelVisible(bool visible)
    {
        // Toggle a separate panel root, or fade this object when the binder lives on the panel itself.
        if (panelRoot != null && panelRoot != gameObject)
        {
            panelRoot.SetActive(visible);
            return;
        }

        CanvasGroup canvasGroup = ResolvePanelCanvasGroup();
        canvasGroup.alpha = visible ? 1f : 0f;
        canvasGroup.interactable = visible;
        canvasGroup.blocksRaycasts = visible;
    }

    private bool CanShowForCurrentMatchState()
    {
        // Keep the room management panel visible only while the room is in lobby state.
        if (!showOnlyInLobby)
        {
            return true;
        }

        return observedMatchStateController != null &&
            observedMatchStateController.IsSpawned &&
            observedMatchStateController.State.Value == NetworkMatchState.Lobby;
    }

    private CanvasGroup ResolvePanelCanvasGroup()
    {
        // Use a CanvasGroup so hiding this panel does not disable the binder that must re-show it later.
        if (panelCanvasGroup != null)
        {
            return panelCanvasGroup;
        }

        GameObject target = panelRoot != null ? panelRoot : gameObject;
        panelCanvasGroup = target.GetComponent<CanvasGroup>();
        if (panelCanvasGroup == null)
        {
            panelCanvasGroup = target.AddComponent<CanvasGroup>();
        }

        return panelCanvasGroup;
    }

    private Transform ResolveContentRoot()
    {
        // Use an assigned content root, otherwise create rows under this object.
        return contentRoot != null ? contentRoot : transform;
    }

    private bool IsLocalRoomHost()
    {
        // Check the replicated room host id against the local client id.
        return observedHostAuthority != null &&
            observedHostAuthority.IsSpawned &&
            observedHostAuthority.IsLocalRoomHost();
    }

    private bool IsRoomHost(ulong clientId)
    {
        // Check whether a listed client is the current room host.
        return observedHostAuthority != null &&
            observedHostAuthority.IsSpawned &&
            observedHostAuthority.HostClientId.Value == clientId;
    }

    private static bool IsLocalClient(ulong clientId)
    {
        // Check whether a listed client id belongs to this local client instance.
        return NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsClient &&
            NetworkManager.Singleton.LocalClientId == clientId;
    }

    private bool CanLocalKick(ulong targetClientId)
    {
        // Only the local room host can kick other connected clients.
        return IsLocalRoomHost() && !IsLocalClient(targetClientId);
    }

    private static TMP_Text ResolveLabelText(TMP_Text[] texts, TMP_Text kickButtonLabel)
    {
        // Pick the first TMP text that is not the button label as the player label.
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] != null && texts[i] != kickButtonLabel)
            {
                return texts[i];
            }
        }

        return texts.Length > 0 ? texts[0] : null;
    }

    private void ApplyFontToRowTexts(GameObject rowObject)
    {
        // Apply the resolved Korean-capable font to every TMP text generated or owned by the row.
        if (rowObject == null)
        {
            return;
        }

        TMP_Text[] texts = rowObject.GetComponentsInChildren<TMP_Text>(true);
        for (int i = 0; i < texts.Length; i++)
        {
            ApplyFontToText(texts[i]);
        }
    }

    private void ApplyFontToText(TMP_Text text)
    {
        // Use the explicit font, reference text font, or NoticeText font for room-list text.
        if (text == null)
        {
            return;
        }

        TMP_FontAsset fontAsset = ResolveTextFontAsset();
        if (fontAsset != null && text.font != fontAsset)
        {
            text.font = fontAsset;
        }
    }

    private TMP_FontAsset ResolveTextFontAsset()
    {
        // Resolve the font used by generated room-list text, preferring the notice panel font.
        if (generatedTextFontAsset != null)
        {
            return generatedTextFontAsset;
        }

        if (fontReferenceText != null && fontReferenceText.font != null)
        {
            return fontReferenceText.font;
        }

        if (cachedSceneNoticeFontAsset != null)
        {
            return cachedSceneNoticeFontAsset;
        }

        cachedSceneNoticeFontAsset = FindSceneNoticeTextFontAsset();
        return cachedSceneNoticeFontAsset;
    }

    private static TMP_FontAsset FindSceneNoticeTextFontAsset()
    {
        // Find the NoticeText font in loaded scenes, including inactive notice panels.
        TMP_Text[] textComponents = Resources.FindObjectsOfTypeAll<TMP_Text>();
        for (int i = 0; i < textComponents.Length; i++)
        {
            TMP_Text textComponent = textComponents[i];
            if (textComponent != null &&
                textComponent.name == DefaultNoticeTextObjectName &&
                textComponent.font != null &&
                textComponent.gameObject.scene.IsValid() &&
                textComponent.gameObject.scene.isLoaded)
            {
                return textComponent.font;
            }
        }

        return null;
    }

    private void MarkDirty()
    {
        // Schedule a refresh during Update.
        needsRefresh = true;
    }
}
