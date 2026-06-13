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
        // Serialize one client's accumulated stat stacks for NetworkList replication.
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
        // Compare every field so NetworkList can detect entry changes correctly.
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
    public const int MaxStacksPerStat = 15;
    public const float BonusPerStack = 0.1f;

    public static PlayerStatsState Instance { get; private set; }

    public NetworkList<PlayerStatEntry> Stats { get; private set; }

    private void Awake()
    {
        // Create the replicated stat list before Netcode spawns this object.
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
        // On the server, prepare entries for already-connected clients and future joins.
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
        // Remove server callbacks when the network object despawns.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    public override void OnDestroy()
    {
        // Release the NetworkList container owned by this behaviour.
        if (Instance == this)
        {
            Instance = null;
        }

        Stats?.Dispose();
        base.OnDestroy();
    }

    public void ResetStats()
    {
        // Clear all accumulated stat stacks and recreate entries for connected clients.
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
        // Server-authoritatively add stat stacks while enforcing the per-stat cap.
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
        int previousValue = GetStatValue(entry, statType);
        int nextValue = ClampStackValue(previousValue + amount);
        entry = SetStatValue(entry, statType, nextValue);
        Stats[index] = entry;

        int gainedAmount = nextValue - previousValue;
        if (amount > 0 && gainedAmount <= 0)
        {
            Debug.Log($"[PlayerStatsState] Client {clientId} {statType} already at cap {MaxStacksPerStat}.");
            return;
        }

        Debug.Log($"[PlayerStatsState] Client {clientId} gained {statType} +{gainedAmount} ({nextValue}/{MaxStacksPerStat})");
    }

    public bool TryGetStats(ulong clientId, out PlayerStatEntry entry)
    {
        // Find the current accumulated stat entry for a client.
        int index = FindIndex(clientId);
        if (index >= 0)
        {
            entry = Stats[index];
            return true;
        }

        entry = default;
        return false;
    }

    public int GetStackCount(ulong clientId, PlayerStatType statType)
    {
        // Return the clamped stack count for one stat on one client.
        return TryGetStats(clientId, out PlayerStatEntry entry)
            ? ClampStackValue(GetStatValue(entry, statType))
            : 0;
    }

    public float GetStatMultiplier(ulong clientId, PlayerStatType statType)
    {
        // Convert collected stacks into the final bonus multiplier for a stat.
        return 1f + GetStackCount(clientId, statType) * BonusPerStack;
    }

    public void LogStatsSummary(string context)
    {
        // Print the current stat stacks for every tracked client.
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

    public static float ApplyCollectedStatBonus(ulong clientId, PlayerStatType statType, float value)
    {
        // Apply the replicated collected-stat multiplier to an already equipment-modified value.
        if (Instance == null)
        {
            return value;
        }

        return value * Instance.GetStatMultiplier(clientId, statType);
    }

    public static float ApplyLocalClientStatBonus(PlayerStatType statType, float value)
    {
        // Apply collected stats for the local client when a network session is active.
        if (!TryGetLocalClientId(out ulong clientId))
        {
            return value;
        }

        return ApplyCollectedStatBonus(clientId, statType, value);
    }

    public static bool TryGetLocalClientId(out ulong clientId)
    {
        // Resolve the local Netcode client id used by local-only gameplay scripts.
        clientId = 0;
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
        {
            return false;
        }

        clientId = manager.LocalClientId;
        return true;
    }

    private void OnClientConnected(ulong clientId)
    {
        // Ensure a stat entry exists as soon as a client joins.
        EnsurePlayer(clientId);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // Keep stat entries for summaries and result handling after disconnects.
        Debug.Log($"[PlayerStatsState] Client disconnected clientId={clientId}");
    }

    private void EnsurePlayer(ulong clientId)
    {
        // Add a zeroed stat entry only when this client is not already tracked.
        if (FindIndex(clientId) >= 0)
        {
            return;
        }

        Stats.Add(new PlayerStatEntry { ClientId = clientId });
        Debug.Log($"[PlayerStatsState] Stats entry added clientId={clientId}");
    }

    private int FindIndex(ulong clientId)
    {
        // Search the replicated list for a matching client id.
        for (int i = 0; i < Stats.Count; i++)
        {
            if (Stats[i].ClientId == clientId)
            {
                return i;
            }
        }

        return -1;
    }

    private static int ClampStackValue(int value)
    {
        // Clamp stat stacks to the current temporary design cap.
        return Mathf.Clamp(value, 0, MaxStacksPerStat);
    }

    private static int GetStatValue(PlayerStatEntry entry, PlayerStatType statType)
    {
        // Read one stat field from the packed network entry.
        return statType switch
        {
            PlayerStatType.MoveSpeed => entry.MoveSpeed,
            PlayerStatType.JumpForce => entry.JumpForce,
            PlayerStatType.Weight => entry.Weight,
            PlayerStatType.Health => entry.Health,
            PlayerStatType.Defense => entry.Defense,
            PlayerStatType.AttackPower => entry.AttackPower,
            PlayerStatType.FireRate => entry.FireRate,
            _ => 0
        };
    }

    private static PlayerStatEntry SetStatValue(PlayerStatEntry entry, PlayerStatType statType, int value)
    {
        // Write one stat field back into the packed network entry.
        value = ClampStackValue(value);
        switch (statType)
        {
            case PlayerStatType.MoveSpeed:
                entry.MoveSpeed = value;
                break;
            case PlayerStatType.JumpForce:
                entry.JumpForce = value;
                break;
            case PlayerStatType.Weight:
                entry.Weight = value;
                break;
            case PlayerStatType.Health:
                entry.Health = value;
                break;
            case PlayerStatType.Defense:
                entry.Defense = value;
                break;
            case PlayerStatType.AttackPower:
                entry.AttackPower = value;
                break;
            case PlayerStatType.FireRate:
                entry.FireRate = value;
                break;
        }

        return entry;
    }
}
