using Unity.Netcode;
using UnityEngine;

public class RoomPolicy : MonoBehaviour
{
    [Header("Room")]
    [SerializeField] private int maxPlayers = 6;

    public int MaxPlayers => maxPlayers;

    public void SetMaxPlayers(int value)
    {
        // DedicatedServerBootstrap 등 외부 런타임 설정에서 룸 정원을 변경할 때 사용.
        maxPlayers = Mathf.Max(1, value);
        Debug.Log($"[RoomPolicy] MaxPlayers set to {maxPlayers}");
    }

    private void OnEnable()
    {
        // 네트워크 매니저 이벤트 등록: 접속 승인/이탈 처리.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.ConnectionApprovalCallback += OnConnectionApproval;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        Debug.Log($"[RoomPolicy] Enabled. MaxPlayers={maxPlayers}");
    }

    private void OnDisable()
    {
        // 오브젝트 비활성화 시 이벤트 해제.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.ConnectionApprovalCallback -= OnConnectionApproval;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        Debug.Log("[RoomPolicy] Disabled.");
    }

    private void OnConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // 현재 접속 인원과 경기 진행 상태를 기준으로 접속 승인 여부를 결정.
        int currentPlayers = NetworkManager.Singleton.ConnectedClientsIds.Count;
        string rejectReason = string.Empty;
        bool approved = CanAcceptConnection(currentPlayers, out rejectReason);

        response.Approved = approved;
        response.CreatePlayerObject = approved;
        response.Pending = false;

        if (!approved)
        {
            // 거절 사유를 클라이언트에 전달.
            response.Reason = rejectReason;
        }

        Debug.Log($"[RoomPolicy] Approval clientId={request.ClientNetworkId} connected={currentPlayers} max={maxPlayers} approved={approved} reason='{rejectReason}'");
    }

    private bool CanAcceptConnection(int currentPlayers, out string rejectReason)
    {
        // 룸 정원과 경기 진행 중 입장 금지 정책을 차례대로 검사.
        if (currentPlayers >= maxPlayers)
        {
            rejectReason = "Room is full.";
            return false;
        }

        MatchStateController controller = MatchStateController.Instance;
        if (controller != null && controller.IsSpawned && IsMatchInProgress(controller.State.Value))
        {
            rejectReason = "Match is in progress.";
            return false;
        }

        rejectReason = string.Empty;
        return true;
    }

    private static bool IsMatchInProgress(NetworkMatchState state)
    {
        // 메인전/전환/최종전 중에는 새 클라이언트 입장을 막는다.
        return state == NetworkMatchState.MatchMain ||
            state == NetworkMatchState.FinalTransition ||
            state == NetworkMatchState.FinalMatch;
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // 클라이언트 이탈을 경기 정책에 전달해 필요 시 패배 처리.
        Debug.Log($"[RoomPolicy] Client disconnected clientId={clientId}. Marking as defeated.");

        // 중도 이탈자는 패배 처리 정책에 따라 탈락 표시.
        MatchStateController controller = MatchStateController.Instance;
        if (controller == null)
        {
            return;
        }

        controller.MarkAsDefeated(clientId);
    }
}
