using Unity.Netcode;
using UnityEngine;

public class RoomHostAuthority : NetworkBehaviour
{
    public const ulong NoHostClientId = ulong.MaxValue;

    public static RoomHostAuthority Instance { get; private set; }

    public NetworkVariable<ulong> HostClientId = new(
        NoHostClientId,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private void Awake()
    {
        // 룸 권한 확인을 다른 UI/컨트롤러에서 쉽게 참조하기 위한 싱글턴.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        // 서버에서 접속/이탈 이벤트를 구독하고 현재 접속자 기준으로 방장을 지정.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        // 접속/이탈 때마다 방장을 지정하거나 재지정.
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        AssignFirstAvailableHost();
    }

    public override void OnNetworkDespawn()
    {
        // 네트워크 오브젝트가 사라질 때 서버 콜백 구독을 해제.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    public bool IsLocalRoomHost()
    {
        // 현재 로컬 클라이언트가 룸 방장인지 확인.
        return NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsClient &&
            HostClientId.Value == NetworkManager.Singleton.LocalClientId;
    }

    public bool CanClientControl(ulong clientId)
    {
        // 서버 RPC 요청자가 방장인지 검증할 때 사용.
        return HostClientId.Value == clientId;
    }

    public void ClearHost()
    {
        // 룸 해산/초기화 시 기존 방장 권한을 명시적으로 비운다.
        if (!IsServer)
        {
            return;
        }

        HostClientId.Value = NoHostClientId;
        Debug.Log("[RoomHostAuthority] Room host cleared.");
    }

    private void OnClientConnected(ulong clientId)
    {
        // 방장이 비어 있을 때 새로 들어온 클라이언트를 방장으로 지정.
        if (HostClientId.Value == NoHostClientId)
        {
            SetHost(clientId);
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // 현재 방장이 나가면 방장 슬롯을 비우고 남은 인원 중 재지정.
        if (HostClientId.Value != clientId)
        {
            return;
        }

        HostClientId.Value = NoHostClientId;
        AssignFirstAvailableHost();
    }

    private void AssignFirstAvailableHost()
    {
        // 현재 접속자 목록에서 가장 먼저 발견된 클라이언트를 방장으로 지정.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            SetHost(clientId);
            return;
        }
    }

    private void SetHost(ulong clientId)
    {
        // NetworkVariable에 방장 ClientId를 기록하고 모든 클라이언트에 동기화.
        HostClientId.Value = clientId;
        Debug.Log($"[RoomHostAuthority] Room host assigned clientId={clientId}");
    }
}
