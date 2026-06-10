using UnityEngine;

public class TestWindowSettings : MonoBehaviour
{
    [Header("Window")]
    [SerializeField] private int width = 960;
    [SerializeField] private int height = 540;
    [SerializeField] private bool fullscreen = false;

    private void Start()
    {
        // 다중 클라이언트 테스트 시 빌드 창이 너무 커지지 않도록 해상도를 고정.
        Screen.SetResolution(width, height, fullscreen);

        // 빌드 실행 로그에서 테스트 해상도가 적용됐는지 확인하기 위한 출력.
        Debug.Log($"[TestWindowSettings] Resolution set to {width}x{height}, fullscreen={fullscreen}");
    }
}
