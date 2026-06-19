using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class MatchStateController : NetworkBehaviour
{
    public const ulong NoWinnerClientId = ulong.MaxValue;

    // 다른 컴포넌트에서 상태 제어를 쉽게 참조하기 위한 싱글턴.
    public static MatchStateController Instance { get; private set; }

    [Header("Durations (seconds)")]
    [SerializeField] private float mainMatchDuration = 300f;
    [SerializeField] private float finalTransitionDuration = 8f;
    [SerializeField] private float finalMatchDuration = 120f;

    [Header("Room Control")]
    [SerializeField] private float kickDisconnectNoticeDelay = 0.35f;
    [SerializeField] private bool allowKickOnlyInLobby = true;

    public NetworkVariable<NetworkMatchState> State = new(
        NetworkMatchState.Lobby,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<float> RemainingTime = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public NetworkVariable<ulong> FinalWinnerClientId = new(
        NoWinnerClientId,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // 중도 이탈로 패배 처리된 플레이어 목록.
    private readonly Dictionary<ulong, bool> defeatedByDisconnect = new();
    private readonly HashSet<ulong> pendingKickDisconnects = new();
    private int lastLoggedRemainingSecond = -1;
    private bool isDisbandingRoom;

    private void Awake()
    {
        // 경기 상태 컨트롤러를 전역에서 참조할 수 있도록 싱글턴을 설정.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        Debug.Log("[MatchStateController] Awake completed.");
    }

    public override void OnNetworkSpawn()
    {
        // 서버/클라이언트 양쪽에서 상태 변경을 확인할 수 있도록 동기화 이벤트를 구독.
        State.OnValueChanged += OnStateChanged;
        RemainingTime.OnValueChanged += OnRemainingTimeChanged;

        Debug.Log($"[MatchStateController] NetworkSpawn role={GetNetworkRole()} state={State.Value} remaining={RemainingTime.Value:0.0}s");
    }

    public override void OnNetworkDespawn()
    {
        // 네트워크 오브젝트가 사라질 때 상태/타이머 이벤트 구독을 해제.
        State.OnValueChanged -= OnStateChanged;
        RemainingTime.OnValueChanged -= OnRemainingTimeChanged;
    }

    private void Update()
    {
        // 상태/타이머 계산은 서버만 수행(서버 권위).
        if (!IsServer)
        {
            return;
        }

        if (State.Value == NetworkMatchState.Lobby || State.Value == NetworkMatchState.Result)
        {
            return;
        }

        if (RemainingTime.Value > 0f)
        {
            RemainingTime.Value = Mathf.Max(0f, RemainingTime.Value - Time.deltaTime);
            int remainingSecond = Mathf.CeilToInt(RemainingTime.Value);
            if (remainingSecond != lastLoggedRemainingSecond &&
                (remainingSecond <= 10 || remainingSecond % 30 == 0))
            {
                lastLoggedRemainingSecond = remainingSecond;
                Debug.Log($"[MatchStateController] State={State.Value} Remaining={remainingSecond}s");
            }
        }

        if (RemainingTime.Value > 0f)
        {
            return;
        }

        // 타이머가 끝나면 다음 상태로 진행.
        AdvanceState();
    }

    public void StartMatchByHost()
    {
        // 방장 시작 버튼에서 호출: 본 경기 시작.
        if (!IsServer)
        {
            return;
        }

        if (!CanStartMatch("StartMatchByHost"))
        {
            return;
        }

        InitializeGameLoopForNewMatch();
        SetState(NetworkMatchState.MatchMain, mainMatchDuration);
        Debug.Log("[MatchStateController] Host started match.");
    }

    public void RequestStartMatch()
    {
        // 클라이언트 UI 버튼에서 호출: 서버에 경기 시작을 요청.
        if (!CanRequestRoomControl("StartMatch"))
        {
            return;
        }

        if (!CanStartMatch("RequestStartMatch"))
        {
            return;
        }

        if (IsServer)
        {
            StartMatchByHost();
            return;
        }

        RequestStartMatchServerRpc();
    }

    public void ReturnToLobbyByHost()
    {
        // 방장 복귀 버튼에서 호출: 탈락 정보 초기화 후 로비 복귀.
        if (!IsServer)
        {
            return;
        }

        ResetGameLoopForRoomIdle();
        SetState(NetworkMatchState.Lobby, 0f);
        Debug.Log("[MatchStateController] Host returned room to lobby.");
    }

    public void RequestReturnToLobby()
    {
        // 클라이언트 UI 버튼에서 호출: 서버에 로비 복귀를 요청.
        if (!CanRequestRoomControl("ReturnToLobby"))
        {
            return;
        }

        if (IsServer)
        {
            ReturnToLobbyByHost();
            return;
        }

        RequestReturnToLobbyServerRpc();
    }

    public void DisbandRoomByHost()
    {
        // 방장 해산 버튼에서 호출: Dedicated Server는 유지하고 룸만 초기화.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        Debug.Log("[MatchStateController] Host disbanded room.");

        if (NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsClient)
        {
            DisbandDedicatedServerRoom();
            return;
        }

        NetworkManager.Singleton.Shutdown();
    }

    public void RequestDisbandRoom()
    {
        // 클라이언트 UI 버튼에서 호출: 서버에 룸 해산을 요청.
        if (!CanRequestRoomControl("DisbandRoom"))
        {
            return;
        }

        if (IsServer)
        {
            DisbandRoomByHost();
            return;
        }

        RequestDisbandRoomServerRpc();
    }

    public void RequestKickClient(ulong targetClientId)
    {
        // UI room list entry point: ask the server to disconnect one room client after authority checks.
        if (!CanRequestRoomControl("KickClient"))
        {
            return;
        }

        if (IsServer)
        {
            if (NetworkManager.Singleton.IsClient && !CanLocalRoomControl("KickClient"))
            {
                return;
            }

            ulong requesterClientId = NetworkManager.Singleton.IsClient
                ? NetworkManager.Singleton.LocalClientId
                : RoomHostAuthority.NoHostClientId;
            KickClientByHost(targetClientId, requesterClientId);
            return;
        }

        RequestKickClientServerRpc(targetClientId);
    }

    public void MarkAsDefeated(ulong clientId)
    {
        // 중도 이탈자를 패배 처리 대상으로 기록.
        if (!IsServer)
        {
            return;
        }

        if (isDisbandingRoom || State.Value == NetworkMatchState.Lobby || State.Value == NetworkMatchState.Result)
        {
            Debug.Log($"[MatchStateController] Disconnect ignored for defeat clientId={clientId} state={State.Value} disbanding={isDisbandingRoom}");
            return;
        }

        defeatedByDisconnect[clientId] = true;
        Debug.Log($"[MatchStateController] Client marked defeated by disconnect clientId={clientId}");
    }

    public List<ulong> ResolveWinners(IReadOnlyDictionary<ulong, int> scores)
    {
        // 동점 허용 정책: 최고 점수자 전원을 우승자로 반환.
        List<ulong> winners = new();

        int topScore = int.MinValue;
        foreach (KeyValuePair<ulong, int> pair in scores)
        {
            if (defeatedByDisconnect.ContainsKey(pair.Key))
            {
                continue;
            }

            if (pair.Value > topScore)
            {
                topScore = pair.Value;
            }
        }

        foreach (KeyValuePair<ulong, int> pair in scores)
        {
            if (defeatedByDisconnect.ContainsKey(pair.Key))
            {
                continue;
            }

            if (pair.Value == topScore)
            {
                winners.Add(pair.Key);
            }
        }

        Debug.Log($"[MatchStateController] Winner resolution complete winners={winners.Count}");
        return winners;
    }

    public void CompleteFinalObjectiveByClient(ulong clientId)
    {
        // 최종전 목표 아이템을 먼저 먹은 클라이언트를 우승자로 확정한다.
        if (!IsServer || State.Value != NetworkMatchState.FinalMatch)
        {
            return;
        }

        if (FinalWinnerClientId.Value != NoWinnerClientId)
        {
            return;
        }

        FinalWinnerClientId.Value = clientId;
        SetState(NetworkMatchState.Result, 0f);
    }

    public void ShowNoticeToClient(ulong clientId, string message, float duration = 4f)
    {
        // Send a server-authored notice to one specific connected client.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        if (!NetworkManager.Singleton.ConnectedClients.ContainsKey(clientId))
        {
            Debug.Log($"[MatchStateController] Notice skipped for disconnected clientId={clientId} message={message}");
            return;
        }

        ClientRpcParams rpcParams = new()
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        };

        ShowNoticeClientRpc(ToFixedNoticeText(message), Mathf.Max(0f, duration), rpcParams);
    }

    public void ShowNoticeToAll(string message, float duration = 4f)
    {
        // Send a server-authored notice to every connected client.
        if (!IsServer)
        {
            return;
        }

        ShowNoticeClientRpc(ToFixedNoticeText(message), Mathf.Max(0f, duration));
    }

    private void AdvanceState()
    {
        // 경기 루프 상태 전이.
        switch (State.Value)
        {
            case NetworkMatchState.MatchMain:
                LogMainMatchSummary();
                SetState(NetworkMatchState.FinalTransition, finalTransitionDuration);
                break;
            case NetworkMatchState.FinalTransition:
                SetState(NetworkMatchState.FinalMatch, finalMatchDuration);
                break;
            case NetworkMatchState.FinalMatch:
                SetState(NetworkMatchState.Result, 0f);
                break;
            default:
                break;
        }
    }

    private void SetState(NetworkMatchState nextState, float duration)
    {
        // 서버가 경기 상태와 남은 시간을 갱신해 모든 클라이언트에 동기화.
        NetworkMatchState previous = State.Value;
        // 상태 전환과 남은 시간 초기화를 한 번에 처리.
        State.Value = nextState;
        RemainingTime.Value = duration;
        lastLoggedRemainingSecond = -1;

        Debug.Log($"[MatchStateController] State changed {previous} -> {nextState}, duration={duration:0.0}s");

        if (nextState == NetworkMatchState.Result)
        {
            LogFinalResult();
        }
    }

    private void DisbandDedicatedServerRoom()
    {
        // EC2 전용 서버는 프로세스를 끄지 않고 모든 클라이언트만 내보낸 뒤 다음 룸을 기다린다.
        isDisbandingRoom = true;
        ResetGameLoopForRoomIdle();
        SetState(NetworkMatchState.Lobby, 0f);

        RoomHostAuthority.Instance?.ClearHost();

        List<ulong> clientsToDisconnect = new(NetworkManager.Singleton.ConnectedClientsIds);
        foreach (ulong clientId in clientsToDisconnect)
        {
            NetworkManager.Singleton.DisconnectClient(clientId);
            Debug.Log($"[MatchStateController] Disconnected client for room disband clientId={clientId}");
        }

        StartCoroutine(CompleteDedicatedDisbandAfterCallbacks());
    }

    private void InitializeGameLoopForNewMatch()
    {
        // 새 경기 시작 전에 이전 판의 승자/이탈자/타이머 로그 상태를 초기화.
        defeatedByDisconnect.Clear();
        pendingKickDisconnects.Clear();
        FinalWinnerClientId.Value = NoWinnerClientId;
        lastLoggedRemainingSecond = -1;
    }

    private void ResetGameLoopForRoomIdle()
    {
        // 로비 복귀/룸 해산처럼 대기 상태로 돌아갈 때 경기 진행 정보를 초기화.
        defeatedByDisconnect.Clear();
        pendingKickDisconnects.Clear();
        FinalWinnerClientId.Value = NoWinnerClientId;
        RemainingTime.Value = 0f;
        lastLoggedRemainingSecond = -1;
    }

    private void LogMainMatchSummary()
    {
        // 메인 경기 종료 직후 최종전으로 넘어가기 전에 플레이어별 스탯 요약을 출력.
        PlayerStatsState statsState = PlayerStatsState.Instance;
        if (statsState == null || !statsState.IsSpawned)
        {
            Debug.Log("[MatchStateController] Main match stats summary skipped. PlayerStatsState is not ready.");
            return;
        }

        statsState.LogStatsSummary("MainMatchCompleted");
    }

    private void LogFinalResult()
    {
        // 결과 상태 진입 시 최종 우승자만 서버 로그에 출력.
        if (FinalWinnerClientId.Value == NoWinnerClientId)
        {
            Debug.Log("[MatchStateController] Result finalWinnerClientId=None");
            return;
        }

        Debug.Log($"[MatchStateController] Result finalWinnerClientId={FinalWinnerClientId.Value}");
    }

    private IEnumerator CompleteDedicatedDisbandAfterCallbacks()
    {
        // Disconnect 콜백이 처리될 시간을 준 뒤 최종적으로 방장 슬롯을 비운다.
        yield return null;
        yield return null;

        isDisbandingRoom = false;
        RoomHostAuthority.Instance?.ClearHost();
        Debug.Log("[MatchStateController] Dedicated server room reset complete. Waiting for next first client.");
    }

    private void OnStateChanged(NetworkMatchState previous, NetworkMatchState current)
    {
        // NetworkVariable 동기화 결과를 모든 인스턴스에서 로그로 확인.
        Debug.Log($"[MatchStateController] Synced state role={GetNetworkRole()} {previous} -> {current}");
    }

    private void OnRemainingTimeChanged(float previous, float current)
    {
        // 상태 전환 직후/종료 직전 위주로 타이머 동기화를 확인.
        int currentSecond = Mathf.CeilToInt(current);
        int previousSecond = Mathf.CeilToInt(previous);
        if (currentSecond == previousSecond)
        {
            return;
        }

        if (currentSecond <= 10 || currentSecond % 30 == 0)
        {
            Debug.Log($"[MatchStateController] Synced timer role={GetNetworkRole()} state={State.Value} remaining={currentSecond}s");
        }
    }

    [ClientRpc]
    private void ShowNoticeClientRpc(FixedString512Bytes message, float duration, ClientRpcParams rpcParams = default)
    {
        // Receive a server notice and forward it to the local Canvas notice binder.
        NoticePanelBinder.ShowNotice(message.ToString(), duration);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestStartMatchServerRpc(ServerRpcParams rpcParams = default)
    {
        // 서버가 요청자를 검증한 뒤 실제 상태를 변경.
        if (!CanSenderControlRoom(rpcParams.Receive.SenderClientId, "StartMatch"))
        {
            return;
        }

        StartMatchByHost();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestReturnToLobbyServerRpc(ServerRpcParams rpcParams = default)
    {
        // 서버가 요청자를 검증한 뒤 로비 복귀 처리.
        if (!CanSenderControlRoom(rpcParams.Receive.SenderClientId, "ReturnToLobby"))
        {
            return;
        }

        ReturnToLobbyByHost();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestDisbandRoomServerRpc(ServerRpcParams rpcParams = default)
    {
        // 서버가 요청자를 검증한 뒤 룸 해산 처리.
        if (!CanSenderControlRoom(rpcParams.Receive.SenderClientId, "DisbandRoom"))
        {
            return;
        }

        DisbandRoomByHost();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestKickClientServerRpc(ulong targetClientId, ServerRpcParams rpcParams = default)
    {
        // Verify the requesting client is the current room host before disconnecting another client.
        if (!CanSenderControlRoom(rpcParams.Receive.SenderClientId, "KickClient"))
        {
            return;
        }

        KickClientByHost(targetClientId, rpcParams.Receive.SenderClientId);
    }

    private void KickClientByHost(ulong targetClientId, ulong requesterClientId)
    {
        // Server-authoritatively notify and disconnect a requested room member.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        if (!CanKickClient(targetClientId, requesterClientId, out string rejectReason))
        {
            Debug.Log($"[MatchStateController] Kick rejected requester={requesterClientId} target={targetClientId} reason='{rejectReason}'");
            ShowKickRejectNotice(requesterClientId, rejectReason);
            return;
        }

        pendingKickDisconnects.Add(targetClientId);
        RoomPolicy.Instance?.RegisterKickedClient(targetClientId);
        string targetLabel = FormatRoomClientLabel(targetClientId);
        ShowNoticeToClient(targetClientId, "\uBC29\uC7A5\uC5D0 \uC758\uD574 \uBC29\uC5D0\uC11C \uD1F4\uC7A5\uB418\uC5C8\uC2B5\uB2C8\uB2E4.", 2f);
        ShowKickRequesterNotice(requesterClientId, $"{targetLabel}\uB97C \uBC29\uC5D0\uC11C \uB0B4\uBCF4\uB0C8\uC2B5\uB2C8\uB2E4.");
        /*
        ShowNoticeToClient(targetClientId, "방장에 의해 방에서 퇴장되었습니다.", 2f);
        ShowKickRequesterNotice(requesterClientId, $"{targetLabel}를 방에서 내보냈습니다.");
        */
        StartCoroutine(DisconnectKickedClientAfterDelay(targetClientId));
        Debug.Log($"[MatchStateController] Kick approved requester={requesterClientId} target={targetClientId}");
    }

    private bool CanKickClient(ulong targetClientId, ulong requesterClientId, out string rejectReason)
    {
        // Validate the target exists and that the host is not trying to kick themselves.
        if (allowKickOnlyInLobby && State.Value != NetworkMatchState.Lobby)
        {
            rejectReason = "Players can only be kicked while the room is in lobby.";
            return false;
        }

        if (!NetworkManager.Singleton.ConnectedClients.ContainsKey(targetClientId))
        {
            rejectReason = "\uB300\uC0C1 \uD50C\uB808\uC774\uC5B4\uAC00 \uC774\uBBF8 \uBC29\uC5D0 \uC5C6\uC2B5\uB2C8\uB2E4.";
            /*
            rejectReason = "대상 플레이어가 이미 방에 없습니다.";
            */
            return false;
        }

        if (requesterClientId != RoomHostAuthority.NoHostClientId && targetClientId == requesterClientId)
        {
            rejectReason = "자기 자신은 강퇴할 수 없습니다.";
            rejectReason = "\uC790\uAE30 \uC790\uC2E0\uC740 \uAC15\uD1F4\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.";
            return false;
        }

        if (pendingKickDisconnects.Contains(targetClientId))
        {
            rejectReason = "이미 강퇴 처리 중인 플레이어입니다.";
            rejectReason = "\uC774\uBBF8 \uAC15\uD1F4 \uCC98\uB9AC \uC911\uC778 \uD50C\uB808\uC774\uC5B4\uC785\uB2C8\uB2E4.";
            return false;
        }

        rejectReason = string.Empty;
        return true;
    }

    private IEnumerator DisconnectKickedClientAfterDelay(ulong targetClientId)
    {
        // Give the target client a brief moment to receive the notice before closing the connection.
        yield return new WaitForSeconds(Mathf.Max(0f, kickDisconnectNoticeDelay));

        pendingKickDisconnects.Remove(targetClientId);
        if (!IsServer || NetworkManager.Singleton == null)
        {
            yield break;
        }

        if (!NetworkManager.Singleton.ConnectedClients.ContainsKey(targetClientId))
        {
            yield break;
        }

        NetworkManager.Singleton.DisconnectClient(targetClientId);
        Debug.Log($"[MatchStateController] Kicked client disconnected clientId={targetClientId}");
    }

    private void ShowKickRejectNotice(ulong requesterClientId, string message)
    {
        // Send kick failure feedback only when the requester is an actual connected client.
        if (requesterClientId == RoomHostAuthority.NoHostClientId)
        {
            return;
        }

        ShowNoticeToClient(requesterClientId, message, 3f);
    }

    private void ShowKickRequesterNotice(ulong requesterClientId, string message)
    {
        // Confirm a successful kick to the room host while supporting dedicated-server console calls.
        if (requesterClientId == RoomHostAuthority.NoHostClientId)
        {
            return;
        }

        ShowNoticeToClient(requesterClientId, message, 3f);
    }

    private bool CanSenderControlRoom(ulong senderClientId, string actionName)
    {
        // ServerRpc 요청자가 현재 룸 방장 권한을 가진 클라이언트인지 확인.
        RoomHostAuthority roomHostAuthority = RoomHostAuthority.Instance;
        bool allowed = roomHostAuthority != null && roomHostAuthority.CanClientControl(senderClientId);
        Debug.Log($"[MatchStateController] Control request action={actionName} sender={senderClientId} allowed={allowed}");
        return allowed;
    }

    private bool CanLocalRoomControl(string actionName)
    {
        // Validate direct local-server button calls with the same room host authority used by RPC requests.
        RoomHostAuthority roomHostAuthority = RoomHostAuthority.Instance;
        bool allowed = roomHostAuthority != null &&
            roomHostAuthority.IsSpawned &&
            roomHostAuthority.IsLocalRoomHost();
        Debug.Log($"[MatchStateController] Local control request action={actionName} allowed={allowed}");
        return allowed;
    }

    private bool CanRequestRoomControl(string actionName)
    {
        // 네트워크가 시작되기 전 UI 클릭은 ServerRpc를 호출하지 않고 무시한다.
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            Debug.Log($"[MatchStateController] Ignored {actionName}. NetworkManager is not running.");
            return false;
        }

        return true;
    }

    private bool CanStartMatch(string actionName)
    {
        // 경기 시작은 대기/결과 상태에서만 허용하여 진행 중 중복 시작을 막는다.
        bool canStart = State.Value == NetworkMatchState.Lobby || State.Value == NetworkMatchState.Result;
        if (!canStart)
        {
            Debug.Log($"[MatchStateController] Ignored {actionName}. Match can only start from Lobby or Result. currentState={State.Value}");
        }

        return canStart;
    }

    private string GetNetworkRole()
    {
        // 현재 NetworkManager 실행 역할을 로그용 문자열로 변환.
        if (NetworkManager.Singleton == null)
        {
            return "NoNetworkManager";
        }

        if (NetworkManager.Singleton.IsHost)
        {
            return "Host";
        }

        if (NetworkManager.Singleton.IsServer)
        {
            return "Server";
        }

        if (NetworkManager.Singleton.IsClient)
        {
            return "Client";
        }

        return "Offline";
    }

    private static FixedString512Bytes ToFixedNoticeText(string message)
    {
        // Convert a notice string to an RPC-safe fixed string payload.
        return new FixedString512Bytes(message ?? string.Empty);
    }

    private static string FormatRoomClientLabel(ulong clientId)
    {
        // Prefer the replicated room player label and fall back to a temporary client id label.
        RoomPlayerRegistry registry = RoomPlayerRegistry.Instance;
        return registry != null && registry.IsSpawned
            ? registry.GetPlayerLabel(clientId)
            : $"플레이어 {clientId}";
    }
}
