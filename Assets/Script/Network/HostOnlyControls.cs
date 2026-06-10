using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public class HostOnlyControls : MonoBehaviour
{
    [Header("Host Only Controls")]
    [SerializeField] private Button[] hostOnlyButtons;
    [SerializeField] private GameObject[] hostOnlyObjects;

    [Header("Options")]
    [SerializeField] private bool hideObjectsForClients = true;
    [SerializeField] private bool allowDedicatedServerControls = false;

    private void OnEnable()
    {
        // 오브젝트 활성화 시 네트워크 이벤트를 구독하고 현재 상태를 즉시 반영.
        RegisterNetworkCallbacks();
        RefreshControls();
    }

    private void OnDisable()
    {
        // 씬 전환/오브젝트 비활성화 시 이벤트 중복 구독 방지.
        UnregisterNetworkCallbacks();
    }

    private void Update()
    {
        // 에디터 테스트 중 Host/Client 전환 직후 UI 상태를 빠르게 반영.
        RefreshControls();
    }

    private void RegisterNetworkCallbacks()
    {
        // Host/Client 연결 상태 변화에 맞춰 버튼 권한을 갱신.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.OnServerStarted += RefreshControls;
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnectionChanged;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientConnectionChanged;
    }

    private void UnregisterNetworkCallbacks()
    {
        // 등록한 네트워크 콜백을 안전하게 해제.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.OnServerStarted -= RefreshControls;
        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnectionChanged;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientConnectionChanged;
    }

    private void OnClientConnectionChanged(ulong clientId)
    {
        // 접속/이탈이 발생하면 Host 여부를 다시 확인해 UI 상태 갱신.
        RefreshControls();
    }

    public void RefreshControls()
    {
        // 최종 구조에서는 RoomHostAuthority가 지정한 방장 클라이언트만 조작 가능.
        bool canControl = IsLocalRoomHost();

        if (!canControl && allowDedicatedServerControls)
        {
            canControl = NetworkManager.Singleton != null &&
                NetworkManager.Singleton.IsServer &&
                !NetworkManager.Singleton.IsClient;
        }

        // 방장 전용 버튼은 방장 클라이언트에서만 클릭 가능.
        foreach (Button button in hostOnlyButtons)
        {
            if (button != null)
            {
                button.interactable = canControl;
            }
        }

        // 버튼 묶음 오브젝트 자체를 숨길지 여부는 옵션으로 제어.
        foreach (GameObject target in hostOnlyObjects)
        {
            if (target != null)
            {
                target.SetActive(!hideObjectsForClients || canControl);
            }
        }
    }

    private static bool IsLocalRoomHost()
    {
        // RoomHostAuthority가 아직 스폰 전이면 Host 모드의 로컬 테스트만 임시 허용.
        RoomHostAuthority roomHostAuthority = RoomHostAuthority.Instance;
        if (roomHostAuthority != null && roomHostAuthority.IsSpawned)
        {
            return roomHostAuthority.IsLocalRoomHost();
        }

        return NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
    }
}
