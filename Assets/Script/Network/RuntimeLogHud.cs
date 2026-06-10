using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class RuntimeLogHud : MonoBehaviour
{
    [Header("Display")]
    [SerializeField] private bool showHud = true;
    [SerializeField] private int maxLines = 10;

    [Header("Layout")]
    [SerializeField] private Rect area = new Rect(12f, 200f, 720f, 260f);

    private readonly Queue<string> lines = new();
    private GUIStyle labelStyle;

    private void OnEnable()
    {
        // Unity Debug.Log 계열 메시지를 화면 HUD에 같이 표시.
        Application.logMessageReceived += OnLogMessageReceived;
    }

    private void OnDisable()
    {
        // 오브젝트 비활성화 시 Unity 로그 이벤트 구독을 해제.
        Application.logMessageReceived -= OnLogMessageReceived;
    }

    private void Update()
    {
        // 테스트 중 로그 창이 방해되면 F2로 표시/숨김 전환.
        if (Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
        {
            showHud = !showHud;
        }
    }

    private void OnGUI()
    {
        // 최근 Debug.Log 메시지를 OnGUI 기반 임시 창에 출력.
        if (!showHud)
        {
            return;
        }

        EnsureStyle();

        GUILayout.BeginArea(area, GUI.skin.box);
        GUILayout.Label("Runtime Log HUD (F2)", labelStyle);

        foreach (string line in lines)
        {
            GUILayout.Label(line, labelStyle);
        }

        GUILayout.EndArea();
    }

    private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        // 너무 긴 로그는 HUD 가독성을 위해 앞부분만 표시.
        string line = $"[{type}] {condition}";
        if (line.Length > 140)
        {
            line = line[..140] + "...";
        }

        lines.Enqueue(line);

        while (lines.Count > maxLines)
        {
            lines.Dequeue();
        }
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
            fontSize = 13,
            normal = { textColor = Color.white }
        };
    }
}
