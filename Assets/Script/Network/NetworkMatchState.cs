// 경기 전체 흐름에서 공통으로 사용하는 네트워크 상태값.
public enum NetworkMatchState : byte
{
    // 대기실 상태.
    Lobby = 0,
    // 본 경기(5분) 상태.
    MatchMain = 1,
    // 최종전 진입 전 전환 상태(카운트다운/이동 준비).
    FinalTransition = 2,
    // 최종전 상태.
    FinalMatch = 3,
    // 결과 표시 상태.
    Result = 4
}
