using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class NetworkDebugHud : MonoBehaviour
{
    [Header("Display")]
    [SerializeField] private bool showHud = true;

    [Header("Layout")]
    [SerializeField] private Rect area = new Rect(12f, 12f, 360f, 180f);

    private GUIStyle labelStyle;

    private void Update()
    {
        // 테스트 중 화면 표시가 방해될 때 F1로 토글.
        if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
        {
            showHud = !showHud;
        }
    }

    private void OnGUI()
    {
        // OnGUI 기반 임시 HUD: 별도 Canvas 없이 테스트 정보를 바로 표시.
        if (!showHud)
        {
            return;
        }

        EnsureStyle();

        GUILayout.BeginArea(area, GUI.skin.box);
        GUILayout.Label("Network Debug HUD", labelStyle);
        DrawNetworkManagerInfo();
        DrawMatchInfo();
        GUILayout.EndArea();
    }

    private void DrawNetworkManagerInfo()
    {
        // 현재 네트워크 실행 모드와 접속 정보를 출력.
        NetworkManager networkManager = NetworkManager.Singleton;
        if (networkManager == null)
        {
            GUILayout.Label("NetworkManager: None", labelStyle);
            return;
        }

        string mode = "Offline";
        if (networkManager.IsHost)
        {
            mode = "Host";
        }
        else if (networkManager.IsServer)
        {
            mode = "Server";
        }
        else if (networkManager.IsClient)
        {
            mode = "Client";
        }

        int connectedCount = networkManager.IsListening ? networkManager.ConnectedClientsIds.Count : 0;

        GUILayout.Label($"Mode: {mode}", labelStyle);
        GUILayout.Label($"Listening: {networkManager.IsListening}", labelStyle);
        GUILayout.Label($"LocalClientId: {networkManager.LocalClientId}", labelStyle);
        GUILayout.Label($"Connected Players: {connectedCount}", labelStyle);

        RoomHostAuthority roomHostAuthority = RoomHostAuthority.Instance;
        if (roomHostAuthority != null && roomHostAuthority.IsSpawned)
        {
            GUILayout.Label($"RoomHostClientId: {roomHostAuthority.HostClientId.Value}", labelStyle);
        }
    }

    private void DrawMatchInfo()
    {
        // MatchStateController가 스폰된 뒤에는 경기 상태/타이머를 출력.
        MatchStateController matchStateController = MatchStateController.Instance;
        if (matchStateController == null || !matchStateController.IsSpawned)
        {
            GUILayout.Label("MatchState: Not Spawned", labelStyle);
            return;
        }

        GUILayout.Label($"Match State: {matchStateController.State.Value}", labelStyle);
        GUILayout.Label($"Remaining: {Mathf.CeilToInt(matchStateController.RemainingTime.Value)}s", labelStyle);
    }

    private void EnsureStyle()
    {
        // HUD 텍스트 스타일은 최초 1회만 생성.
        if (labelStyle != null)
        {
            return;
        }

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            normal = { textColor = Color.white }
        };
    }
}
