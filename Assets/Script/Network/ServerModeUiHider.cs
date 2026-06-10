using System;
using UnityEngine;

public class ServerModeUiHider : MonoBehaviour
{
    [Header("Server Mode UI")]
    [SerializeField] private GameObject[] hideInServerMode;
    [SerializeField] private GameObject[] showOnlyInServerMode;

    private void Start()
    {
        // 현재 실행 환경이 서버 모드인지 확인하고 지정된 UI 표시 상태를 전환.
        bool serverMode = IsServerMode();

        // Dedicated Server 창에서는 접속/방장 조작 UI를 숨겨 실수 클릭을 방지.
        foreach (GameObject target in hideInServerMode)
        {
            if (target != null)
            {
                target.SetActive(!serverMode);
            }
        }

        // 서버 전용 HUD나 로그 UI가 있다면 서버 모드에서만 표시.
        foreach (GameObject target in showOnlyInServerMode)
        {
            if (target != null)
            {
                target.SetActive(serverMode);
            }
        }

        Debug.Log($"[ServerModeUiHider] ServerMode={serverMode}");
    }

    private static bool IsServerMode()
    {
        // -server 인자 또는 배치 모드를 Dedicated Server 실행으로 판단.
        if (Application.isBatchMode)
        {
            return true;
        }

        string[] args = Environment.GetCommandLineArgs();
        foreach (string arg in args)
        {
            if (string.Equals(arg, "-server", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
