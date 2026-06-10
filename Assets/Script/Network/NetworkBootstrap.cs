using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

public class NetworkBootstrap : MonoBehaviour
{
    [Header("Transport")]
    [SerializeField] private string address = "127.0.0.1";
    [SerializeField] private ushort port = 7777;

    [Header("Optional UI Bindings")]
    [SerializeField] private InputField addressInputField;
    [SerializeField] private InputField portInputField;
    [SerializeField] private TMP_InputField addressTmpInputField;
    [SerializeField] private TMP_InputField portTmpInputField;

    public void StartHost()
    {
        // Host 시작 전에 전송 주소/포트를 Transport에 적용.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        ConfigureTransport();
        bool started = NetworkManager.Singleton.StartHost();
        Debug.Log($"[NetworkBootstrap] StartHost address={address} port={port} success={started}");
    }

    public void StartClient()
    {
        // Client 시작 전에 전송 주소/포트를 Transport에 적용.
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[NetworkBootstrap] StartClient failed. NetworkManager.Singleton is null.");
            return;
        }

        if (NetworkManager.Singleton.IsListening)
        {
            Debug.LogWarning($"[NetworkBootstrap] StartClient ignored. NetworkManager is already running role={GetNetworkRole()}");
            return;
        }

        ApplyConnectionInput();
        if (!ConfigureTransport())
        {
            Debug.LogError("[NetworkBootstrap] StartClient failed. UnityTransport is missing.");
            return;
        }

        bool started = NetworkManager.Singleton.StartClient();
        Debug.Log($"[NetworkBootstrap] StartClient address={address} port={port} success={started}");
    }

    public void ApplyConnectionInput()
    {
        // Canvas 입력창이 연결되어 있으면 현재 IP/Port 값을 읽어 Transport 설정에 반영.
        string inputAddress = GetAddressInput();
        string inputPort = GetPortInput();

        if (!string.IsNullOrWhiteSpace(inputAddress))
        {
            SetAddress(inputAddress);
        }

        if (!string.IsNullOrWhiteSpace(inputPort))
        {
            SetPort(inputPort);
        }
    }

    public void StartClientFromInput()
    {
        // Canvas Client 버튼에서 호출: 입력값 반영 후 클라이언트 접속.
        ApplyConnectionInput();
        StartClient();
    }

    public void StopNetwork()
    {
        // Host/Client 구분 없이 네트워크 세션 종료.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.Shutdown();
        Debug.Log("[NetworkBootstrap] Shutdown requested.");
    }

    public void LeaveRoom()
    {
        // 일반 클라이언트의 룸 떠나기 버튼에서 호출해 로컬 네트워크 연결만 종료.
        if (NetworkManager.Singleton == null)
        {
            Debug.LogWarning("[NetworkBootstrap] LeaveRoom ignored. NetworkManager.Singleton is null.");
            return;
        }

        if (!NetworkManager.Singleton.IsListening)
        {
            Debug.Log("[NetworkBootstrap] LeaveRoom ignored. NetworkManager is not running.");
            return;
        }

        if (NetworkManager.Singleton.IsServer && !NetworkManager.Singleton.IsClient)
        {
            Debug.LogWarning("[NetworkBootstrap] LeaveRoom ignored. Dedicated server cannot leave a room as a client.");
            return;
        }

        string role = GetNetworkRole();
        NetworkManager.Singleton.Shutdown();
        Debug.Log($"[NetworkBootstrap] LeaveRoom requested role={role}");
    }

    public void SetAddress(string value)
    {
        // UI InputField에서 받은 주소 문자열 적용.
        if (!string.IsNullOrWhiteSpace(value))
        {
            address = value.Trim();
            Debug.Log($"[NetworkBootstrap] Address set to {address}");
        }
    }

    public void SetPort(string value)
    {
        // UI InputField에서 받은 포트를 ushort로 파싱해 적용.
        if (ushort.TryParse(value, out ushort parsedPort))
        {
            port = parsedPort;
            Debug.Log($"[NetworkBootstrap] Port set to {port}");
        }
    }

    private bool ConfigureTransport()
    {
        // UnityTransport에 최종 접속 정보 반영.
        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
        {
            return false;
        }

        transport.SetConnectionData(address, port);
        Debug.Log($"[NetworkBootstrap] Transport configured address={address} port={port}");
        return true;
    }

    private string GetAddressInput()
    {
        // TextMeshPro/UGUI 입력창 중 연결된 주소 입력값을 반환.
        if (addressTmpInputField != null)
        {
            return addressTmpInputField.text;
        }

        return addressInputField != null ? addressInputField.text : string.Empty;
    }

    private string GetPortInput()
    {
        // TextMeshPro/UGUI 입력창 중 연결된 포트 입력값을 반환.
        if (portTmpInputField != null)
        {
            return portTmpInputField.text;
        }

        return portInputField != null ? portInputField.text : string.Empty;
    }

    private static string GetNetworkRole()
    {
        // 현재 NetworkManager 상태를 로그에 쓰기 쉬운 문자열로 변환.
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
}
