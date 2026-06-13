using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerStatsPanelToggle : MonoBehaviour
{
    [Header("Panel")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private TMP_Text statsText;
    [SerializeField] private bool startVisible = false;

    [Header("Linked Submenu")]
    [SerializeField] private GameObject[] submenuObjects = System.Array.Empty<GameObject>();
    [SerializeField] private bool showSubmenuInLobby = true;
    [SerializeField] private bool showSubmenuInResult = true;

    [Header("Text")]
    [SerializeField] private string titleText = "Collected Stats";
    [SerializeField] private string noEquipmentText = "None";
    [SerializeField] private string offlineText = "Not connected.";
    [SerializeField] private string notReadyText = "Stats are not ready.";

    private PlayerEquipment localEquipment;
    private bool isVisible;
    private bool hasLastSubmenuVisible;
    private bool lastSubmenuVisible;
    private string lastRenderedText;

    private void Awake()
    {
        // 시작 시 Inspector 설정에 맞춰 스탯 패널 표시 상태를 초기화.
        isVisible = startVisible;
        SetPanelVisible(isVisible);
        RefreshSubmenuVisibility(force: true);
    }

    private void Update()
    {
        // Tab 키로 스탯 패널을 켜고 끄며, 켜져 있을 때만 내용을 갱신.
        if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
        {
            TogglePanel();
        }

        RefreshSubmenuVisibility(force: false);

        if (isVisible)
        {
            RefreshStatsText();
        }
    }

    public void TogglePanel()
    {
        // 외부 버튼이나 단축키에서 현재 스탯 패널 표시 상태를 반전.
        SetPanelVisible(!isVisible);
    }

    public void RefreshStatsText()
    {
        // 로컬 클라이언트의 현재 스탯 NetworkList 값을 TMP 텍스트에 반영.
        string nextText = BuildStatsText();
        if (statsText == null || lastRenderedText == nextText)
        {
            return;
        }

        statsText.text = nextText;
        lastRenderedText = nextText;
    }

    private void SetPanelVisible(bool visible)
    {
        // 패널 루트가 별도 오브젝트면 활성화하고, 아니면 TMP 텍스트 표시만 토글.
        isVisible = visible;

        if (panelRoot != null && panelRoot != gameObject)
        {
            panelRoot.SetActive(visible);
        }
        else if (statsText != null)
        {
            statsText.enabled = visible;
        }

        if (visible)
        {
            RefreshStatsText();
        }

        RefreshSubmenuVisibility(force: true);
    }

    private void RefreshSubmenuVisibility(bool force)
    {
        // Keep test-menu buttons visible in lobby/result, and otherwise tie them to the Tab submenu state.
        bool shouldShow = ShouldShowSubmenu();
        if (!force && hasLastSubmenuVisible && lastSubmenuVisible == shouldShow)
        {
            return;
        }

        for (int i = 0; i < submenuObjects.Length; i++)
        {
            if (submenuObjects[i] != null)
            {
                submenuObjects[i].SetActive(shouldShow);
            }
        }

        hasLastSubmenuVisible = true;
        lastSubmenuVisible = shouldShow;
    }

    private bool ShouldShowSubmenu()
    {
        // Tab always opens the linked test submenu; otherwise only non-combat states keep it visible.
        return isVisible || ShouldShowSubmenuForCurrentMatchState();
    }

    private bool ShouldShowSubmenuForCurrentMatchState()
    {
        // Before connecting, treat the UI like lobby so connection buttons remain available.
        if (Application.isBatchMode)
        {
            return false;
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            return showSubmenuInLobby;
        }

        MatchStateController controller = MatchStateController.Instance;
        if (controller == null || !controller.IsSpawned)
        {
            return showSubmenuInLobby;
        }

        return controller.State.Value switch
        {
            NetworkMatchState.Lobby => showSubmenuInLobby,
            NetworkMatchState.Result => showSubmenuInResult,
            _ => false
        };
    }

    private string BuildStatsText()
    {
        // 네트워크/스탯 준비 상태에 따라 표시할 스탯 문자열을 생성.
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || !NetworkManager.Singleton.IsClient)
        {
            return offlineText;
        }

        PlayerStatsState statsState = PlayerStatsState.Instance;
        if (statsState == null || !statsState.IsSpawned)
        {
            return notReadyText;
        }

        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        if (!statsState.TryGetStats(localClientId, out PlayerStatEntry entry))
        {
            return notReadyText;
        }

        return FormatStats(entry);
    }

    private string FormatStats(PlayerStatEntry entry)
    {
        // 플레이어가 수집한 스탯 아이템 개수를 줄 단위 텍스트로 변환.
        int total = entry.MoveSpeed +
            entry.JumpForce +
            entry.Weight +
            entry.Health +
            entry.Defense +
            entry.AttackPower +
            entry.FireRate;

        return $"{titleText}\n" +
            $"{GetCurrentEquipmentText()}\n" +
            $"Total: {total}\n" +
            $"Move Speed: {entry.MoveSpeed}\n" +
            $"Jump Force: {entry.JumpForce}\n" +
            $"Weight: {entry.Weight}\n" +
            $"Health: {entry.Health}\n" +
            $"Defense: {entry.Defense}\n" +
            $"Attack Power: {entry.AttackPower}\n" +
            $"Fire Rate: {entry.FireRate}";
    }

    private string GetCurrentEquipmentText()
    {
        // Resolve the local player's current equipment display name for the temporary stats panel.
        if (localEquipment == null)
        {
            localEquipment = FindFirstObjectByType<PlayerEquipment>();
        }

        EquipmentDefinition equipment = localEquipment != null ? localEquipment.CurrentEquipment : null;
        if (equipment == null)
        {
            return noEquipmentText;
        }

        return string.IsNullOrWhiteSpace(equipment.DisplayName) ? equipment.EquipmentId : equipment.DisplayName;
    }
}
