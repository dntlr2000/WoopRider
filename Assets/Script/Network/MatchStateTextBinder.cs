using TMPro;
using Unity.Netcode;
using UnityEngine;

public class MatchStateTextBinder : MonoBehaviour
{
    [Header("Text Bindings")]
    [SerializeField] private TMP_Text stateText;
    [SerializeField] private TMP_Text remainingTimeText;
    [SerializeField] private TMP_Text scoreText;

    [Header("Labels")]
    [SerializeField] private string offlineStateText = "State: Offline";
    [SerializeField] private string notReadyStateText = "State: Not Ready";
    [SerializeField] private string stateFormat = "State: {0}";
    [SerializeField] private string remainingTimeFormat = "{0:00}:{1:00}";
    [SerializeField] private string idleRemainingTimeText = "Remaining: --:--";
    [SerializeField] private string finalScoreFormat = "Score: {0}";

    private string lastStateText;
    private string lastRemainingTimeText;
    private string lastScoreText;

    private void OnEnable()
    {
        // UI가 켜질 때 현재 네트워크 상태를 즉시 텍스트에 반영.
        Refresh(force: true);
    }

    private void Update()
    {
        // NetworkVariable 값이 바뀌면 Canvas의 TMP 텍스트를 갱신.
        Refresh(force: false);
    }

    public void RefreshNow()
    {
        // 버튼이나 외부 UI 이벤트에서 수동으로 텍스트를 즉시 갱신할 때 사용.
        Refresh(force: true);
    }

    private void Refresh(bool force)
    {
        // MatchStateController 준비 상태에 따라 상태/타이머 표시 문자열을 만든다.
        MatchStateController controller = MatchStateController.Instance;
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            SetTexts(offlineStateText, idleRemainingTimeText, force);
            RefreshFinalScore(NetworkMatchState.Lobby, force);
            return;
        }

        if (controller == null || !controller.IsSpawned)
        {
            SetTexts(notReadyStateText, idleRemainingTimeText, force);
            RefreshFinalScore(NetworkMatchState.Lobby, force);
            return;
        }

        NetworkMatchState state = controller.State.Value;
        string nextStateText = string.Format(stateFormat, GetDisplayName(state));
        string nextRemainingTimeText = BuildRemainingTimeText(state, controller.RemainingTime.Value);
        SetTexts(nextStateText, nextRemainingTimeText, force);
        RefreshFinalScore(state, force);
    }

    private string BuildRemainingTimeText(NetworkMatchState state, float remainingTime)
    {
        // 로비/결과 상태는 타이머 대신 대기 표시를 사용하고 경기 상태만 mm:ss로 표시.
        if (state == NetworkMatchState.Lobby || state == NetworkMatchState.Result)
        {
            return idleRemainingTimeText;
        }

        int totalSeconds = Mathf.Max(0, Mathf.CeilToInt(remainingTime));
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return string.Format(remainingTimeFormat, minutes, seconds);
    }

    private void SetTexts(string nextStateText, string nextRemainingTimeText, bool force)
    {
        // 이전 값과 달라진 TMP 텍스트만 갱신해 불필요한 UI 변경을 줄인다.
        if ((force || lastStateText != nextStateText) && stateText != null)
        {
            stateText.text = nextStateText;
        }

        if ((force || lastRemainingTimeText != nextRemainingTimeText) && remainingTimeText != null)
        {
            remainingTimeText.text = nextRemainingTimeText;
        }

        lastStateText = nextStateText;
        lastRemainingTimeText = nextRemainingTimeText;
    }

    private void RefreshFinalScore(NetworkMatchState state, bool force)
    {
        // Show the local player's replicated objective score only during the active final match.
        if (scoreText == null)
        {
            return;
        }

        bool shouldShow = state == NetworkMatchState.FinalMatch;
        if (scoreText.gameObject.activeSelf != shouldShow)
        {
            scoreText.gameObject.SetActive(shouldShow);
        }

        if (!shouldShow)
        {
            lastScoreText = null;
            return;
        }

        int score = 0;
        GameplayPickupManager scoreManager = GameplayPickupManager.Instance;
        if (scoreManager != null)
        {
            scoreManager.TryGetLocalFinalScore(out score);
        }

        string nextScoreText = string.Format(finalScoreFormat, score);
        if (force || lastScoreText != nextScoreText)
        {
            scoreText.text = nextScoreText;
        }

        lastScoreText = nextScoreText;
    }

    private static string GetDisplayName(NetworkMatchState state)
    {
        // 내부 enum 이름을 UI에서 읽기 쉬운 경기 상태명으로 변환.
        return state switch
        {
            NetworkMatchState.Lobby => "Lobby",
            NetworkMatchState.MatchMain => "Main Match",
            NetworkMatchState.FinalTransition => "Final Transition",
            NetworkMatchState.FinalMatch => "Final Match",
            NetworkMatchState.Result => "Result",
            _ => state.ToString()
        };
    }
}
