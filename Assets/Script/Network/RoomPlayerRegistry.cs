using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public struct RoomPlayerEntry : INetworkSerializable, IEquatable<RoomPlayerEntry>
{
    public ulong ClientId;
    public FixedString64Bytes DisplayName;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        // Serialize the minimal room-list data that every client needs for UI rendering.
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref DisplayName);
    }

    public bool Equals(RoomPlayerEntry other)
    {
        // Compare replicated fields so NetworkList can detect changed entries correctly.
        return ClientId == other.ClientId && DisplayName.Equals(other.DisplayName);
    }
}

public class RoomPlayerRegistry : NetworkBehaviour
{
    public static RoomPlayerRegistry Instance { get; private set; }

    public NetworkList<RoomPlayerEntry> Players { get; private set; }

    private void Awake()
    {
        // Create the replicated room player list before Netcode spawns this scene object.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        Players = new NetworkList<RoomPlayerEntry>();
    }

    public override void OnNetworkSpawn()
    {
        // The server owns list contents and rebuilds them from the current NetworkManager state.
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        RebuildFromConnectedClients();
    }

    public override void OnNetworkDespawn()
    {
        // Unhook NetworkManager callbacks when this replicated scene object despawns.
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    public override void OnDestroy()
    {
        // Dispose the NetworkList container and clear the singleton when this object is destroyed.
        if (Instance == this)
        {
            Instance = null;
        }

        Players?.Dispose();
        base.OnDestroy();
    }

    public bool ContainsClient(ulong clientId)
    {
        // Return whether the replicated room list currently includes the requested client.
        return FindIndex(clientId) >= 0;
    }

    public bool TryGetPlayer(ulong clientId, out RoomPlayerEntry entry)
    {
        // Find a replicated room entry by client id for UI or notices.
        int index = FindIndex(clientId);
        if (index >= 0)
        {
            entry = Players[index];
            return true;
        }

        entry = default;
        return false;
    }

    public string GetPlayerLabel(ulong clientId)
    {
        // Prefer a replicated display name and fall back to a stable temporary client id label.
        return TryGetPlayer(clientId, out RoomPlayerEntry entry) && !entry.DisplayName.IsEmpty
            ? entry.DisplayName.ToString()
            : FormatFallbackPlayerLabel(clientId).ToString();
    }

    private void OnClientConnected(ulong clientId)
    {
        // Add newly connected clients to the replicated room list.
        AddOrUpdateClient(clientId);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // Remove disconnected clients from the replicated room list.
        RemoveClient(clientId);
    }

    private void RebuildFromConnectedClients()
    {
        // Recreate the list from NetworkManager so host migration between tests starts cleanly.
        Players.Clear();
        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            AddOrUpdateClient(clientId);
        }

        Debug.Log($"[RoomPlayerRegistry] Rebuilt room player list count={Players.Count}");
    }

    private void AddOrUpdateClient(ulong clientId)
    {
        // Insert a client or refresh its display name entry when it already exists.
        int index = FindIndex(clientId);
        RoomPlayerEntry entry = new()
        {
            ClientId = clientId,
            DisplayName = FormatFallbackPlayerLabel(clientId)
        };

        if (index >= 0)
        {
            Players[index] = entry;
        }
        else
        {
            Players.Add(entry);
        }

        Debug.Log($"[RoomPlayerRegistry] Client listed clientId={clientId} count={Players.Count}");
    }

    private void RemoveClient(ulong clientId)
    {
        // Remove one client entry when the server receives a disconnect callback.
        int index = FindIndex(clientId);
        if (index < 0)
        {
            return;
        }

        Players.RemoveAt(index);
        Debug.Log($"[RoomPlayerRegistry] Client unlisted clientId={clientId} count={Players.Count}");
    }

    private int FindIndex(ulong clientId)
    {
        // Find the current NetworkList index for a client id.
        if (Players == null)
        {
            return -1;
        }

        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].ClientId == clientId)
            {
                return i;
            }
        }

        return -1;
    }

    private static FixedString64Bytes FormatFallbackPlayerLabel(ulong clientId)
    {
        // Build a temporary user-facing label until player names are introduced.
        return new FixedString64Bytes($"\uD50C\uB808\uC774\uC5B4 {clientId}");
    }
}
