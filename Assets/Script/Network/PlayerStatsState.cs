using System;
using Unity.Netcode;
using UnityEngine;

public struct PlayerStatEntry : INetworkSerializable, IEquatable<PlayerStatEntry>
{
    public ulong ClientId;
    public int MoveSpeed;
    public int JumpForce;
    public int Weight;
    public int Health;
    public int Defense;
    public int AttackPower;
    public int FireRate;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        // NetworkList에서 클라이언트별 스탯 묶음을 직렬화/역직렬화.
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref MoveSpeed);
        serializer.SerializeValue(ref JumpForce);
        serializer.SerializeValue(ref Weight);
        serializer.SerializeValue(ref Health);
        serializer.SerializeValue(ref Defense);
        serializer.SerializeValue(ref AttackPower);
        serializer.SerializeValue(ref FireRate);
    }

    public bool Equals(PlayerStatEntry other)
    {
        // NetworkList 변경 감지를 위해 모든 스탯 필드가 같은지 비교.
        return ClientId == other.ClientId &&
            MoveSpeed == other.MoveSpeed &&
            JumpForce == other.JumpForce &&
            Weight == other.Weight &&
            Health == other.Health &&
            Defense == other.Defense &&
            AttackPower == other.AttackPower &&
            FireRate == other.FireRate;
    }
}

public class PlayerStatsState : NetworkBehaviour
{
    public static PlayerStatsState Instance { get; private set; }

    public NetworkList<PlayerStatEntry> Stats { get; private set; }

    private void Awake()
    {
        // 서버가 관리하는 클라이언트별 스탯 목록.
        Stats = new NetworkList<PlayerStatEntry>();

        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        // 서버에서 접속자 이벤트를 구독하고 기존 접속자의 스탯 엔트리를 준비.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            EnsurePlayer(clientId);
        }
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

    private void OnDestroy()
    {
        // NetworkList가 남긴 네이티브 리소스를 정리.
        Stats?.Dispose();
    }

    public void ResetStats()
    {
        // 새 경기 시작/룸 초기화 시 모든 누적 스탯을 비운다.
        if (!IsServer)
        {
            return;
        }

        Stats.Clear();

        if (NetworkManager.Singleton == null)
        {
            return;
        }

        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            EnsurePlayer(clientId);
        }

        Debug.Log("[PlayerStatsState] Stats reset.");
    }

    public void AddStat(ulong clientId, PlayerStatType statType, int amount)
    {
        // 서버 판정으로 획득한 아이템만 스탯에 반영한다.
        if (!IsServer)
        {
            return;
        }

        EnsurePlayer(clientId);
        int index = FindIndex(clientId);
        if (index < 0)
        {
            return;
        }

        PlayerStatEntry entry = Stats[index];
        switch (statType)
        {
            case PlayerStatType.MoveSpeed:
                entry.MoveSpeed += amount;
                break;
            case PlayerStatType.JumpForce:
                entry.JumpForce += amount;
                break;
            case PlayerStatType.Weight:
                entry.Weight += amount;
                break;
            case PlayerStatType.Health:
                entry.Health += amount;
                break;
            case PlayerStatType.Defense:
                entry.Defense += amount;
                break;
            case PlayerStatType.AttackPower:
                entry.AttackPower += amount;
                break;
            case PlayerStatType.FireRate:
                entry.FireRate += amount;
                break;
        }

        Stats[index] = entry;
        Debug.Log($"[PlayerStatsState] Client {clientId} gained {statType} +{amount}");
    }

    public bool TryGetStats(ulong clientId, out PlayerStatEntry entry)
    {
        // 특정 클라이언트의 현재 스탯 스냅샷을 조회.
        int index = FindIndex(clientId);
        if (index >= 0)
        {
            entry = Stats[index];
            return true;
        }

        entry = default;
        return false;
    }

    public void LogStatsSummary(string context)
    {
        // 서버 로그에 현재 클라이언트별 스탯 누적치를 요약 출력.
        if (!IsServer)
        {
            return;
        }

        Debug.Log($"[PlayerStatsState] Stats summary start context='{context}' players={Stats.Count}");
        for (int i = 0; i < Stats.Count; i++)
        {
            PlayerStatEntry entry = Stats[i];
            Debug.Log($"[PlayerStatsState] Summary clientId={entry.ClientId} " +
                $"MoveSpeed={entry.MoveSpeed} JumpForce={entry.JumpForce} Weight={entry.Weight} " +
                $"Health={entry.Health} Defense={entry.Defense} AttackPower={entry.AttackPower} FireRate={entry.FireRate}");
        }

        Debug.Log($"[PlayerStatsState] Stats summary end context='{context}'");
    }

    private void OnClientConnected(ulong clientId)
    {
        // 새로 접속한 클라이언트의 스탯 엔트리를 보장.
        EnsurePlayer(clientId);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // 결과 처리와 재접속 확장을 고려해 현재는 스탯을 즉시 삭제하지 않는다.
        Debug.Log($"[PlayerStatsState] Client disconnected clientId={clientId}");
    }

    private void EnsurePlayer(ulong clientId)
    {
        // 아직 스탯 엔트리가 없는 클라이언트만 새 엔트리를 추가.
        if (FindIndex(clientId) >= 0)
        {
            return;
        }

        Stats.Add(new PlayerStatEntry { ClientId = clientId });
        Debug.Log($"[PlayerStatsState] Stats entry added clientId={clientId}");
    }

    private int FindIndex(ulong clientId)
    {
        // NetworkList에서 ClientId가 일치하는 엔트리 위치를 찾는다.
        for (int i = 0; i < Stats.Count; i++)
        {
            if (Stats[i].ClientId == clientId)
            {
                return i;
            }
        }

        return -1;
    }
}
