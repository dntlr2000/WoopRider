using System;
using System.Runtime.InteropServices;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

public class DedicatedServerBootstrap : MonoBehaviour
{
    [Header("Defaults")]
    [SerializeField] private ushort defaultPort = 7777;
    [SerializeField] private int defaultMaxPlayers = 6;
    [SerializeField] private string defaultRoomId = "local-room-1";

    [Header("Runtime")]
    [SerializeField] private bool autoStartInBatchMode = true;
    [SerializeField] private string serverWindowTitleFormat = "WoopRider Server - {roomId}:{port}";

    private void Start()
    {
        // Dedicated Server 빌드/배치 모드 또는 -server 인자가 있을 때만 자동 서버 시작.
        if (!ShouldStartDedicatedServer())
        {
            return;
        }

        StartDedicatedServerFromCommandLine();
    }

    private bool ShouldStartDedicatedServer()
    {
        // 실행 인자나 배치 모드 여부로 Dedicated Server 자동 시작 조건을 판단.
        string[] args = Environment.GetCommandLineArgs();
        bool hasServerArg = HasArg(args, "-server");
        bool isBatchServer = autoStartInBatchMode && Application.isBatchMode;

        return hasServerArg || isBatchServer;
    }

    private void StartDedicatedServerFromCommandLine()
    {
        // 커맨드라인 인자를 읽어 Transport/룸 정책을 설정한 뒤 서버를 시작.
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[DedicatedServerBootstrap] NetworkManager not found.");
            return;
        }

        string[] args = Environment.GetCommandLineArgs();
        ushort port = GetUShortArg(args, "-port", defaultPort);
        int maxPlayers = GetIntArg(args, "-maxPlayers", defaultMaxPlayers);
        string roomId = GetStringArg(args, "-roomId", defaultRoomId);

        ConfigureTransport(port);
        ConfigureRoomPolicy(maxPlayers);
        SetServerWindowTitle(roomId, port);

        bool started = NetworkManager.Singleton.StartServer();
        Debug.Log($"[DedicatedServerBootstrap] StartServer roomId={roomId} port={port} maxPlayers={maxPlayers} success={started}");
    }

    private void ConfigureTransport(ushort port)
    {
        // 서버는 모든 네트워크 인터페이스에서 접속을 받도록 0.0.0.0에 바인딩.
        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[DedicatedServerBootstrap] UnityTransport not found on NetworkManager.");
            return;
        }

        transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
        Debug.Log($"[DedicatedServerBootstrap] Transport configured listenAddress=0.0.0.0 port={port}");
    }

    private void ConfigureRoomPolicy(int maxPlayers)
    {
        // 실행 인자로 받은 정원 제한을 RoomPolicy에 적용.
        RoomPolicy roomPolicy = FindFirstObjectByType<RoomPolicy>();
        if (roomPolicy == null)
        {
            Debug.LogWarning("[DedicatedServerBootstrap] RoomPolicy not found. Max player limit will use inspector value.");
            return;
        }

        roomPolicy.SetMaxPlayers(maxPlayers);
    }

    private static bool HasArg(string[] args, string name)
    {
        // 대소문자 구분 없이 특정 실행 인자가 포함되어 있는지 확인.
        foreach (string arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetStringArg(string[] args, string name, string fallback)
    {
        // 이름 뒤에 따라오는 문자열 인자값을 읽고 없으면 기본값을 반환.
        int index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return fallback;
        }

        return args[index + 1];
    }

    private static int GetIntArg(string[] args, string name, int fallback)
    {
        // 문자열 실행 인자를 int로 파싱하고 실패하면 기본값을 반환.
        string value = GetStringArg(args, name, string.Empty);
        return int.TryParse(value, out int result) ? result : fallback;
    }

    private static ushort GetUShortArg(string[] args, string name, ushort fallback)
    {
        // 문자열 실행 인자를 ushort 포트 값으로 파싱하고 실패하면 기본값을 반환.
        string value = GetStringArg(args, name, string.Empty);
        return ushort.TryParse(value, out ushort result) ? result : fallback;
    }

    private void SetServerWindowTitle(string roomId, ushort port)
    {
        // Windows 로컬 테스트에서 Dedicated Server 창을 쉽게 구분하기 위한 제목 변경.
        string title = serverWindowTitleFormat
            .Replace("{roomId}", roomId)
            .Replace("{port}", port.ToString());

#if UNITY_STANDALONE_WIN && !UNITY_SERVER
        IntPtr windowHandle = GetActiveWindow();
        if (windowHandle == IntPtr.Zero)
        {
            Debug.LogWarning($"[DedicatedServerBootstrap] Could not find active window. title={title}");
            return;
        }

        SetWindowText(windowHandle, title);
        Debug.Log($"[DedicatedServerBootstrap] Window title set to '{title}'");
#else
        Debug.Log($"[DedicatedServerBootstrap] Server title='{title}'");
#endif
    }

#if UNITY_STANDALONE_WIN && !UNITY_SERVER
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string lpString);
#endif
}
