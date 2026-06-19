using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

public class RoomPolicy : MonoBehaviour
{
    public static RoomPolicy Instance { get; private set; }

    private const string ConnectionPayloadPrefix = "WoopRiderRoomClient:";
    private const string UnknownIdentityPrefix = "unknown-client:";

    [Header("Room")]
    [SerializeField] private int maxPlayers = 6;

    [Header("Kick Ban")]
    [SerializeField] private bool banKickedPlayersFromRejoining = true;
    [SerializeField] private bool kickedPlayerBanIsPermanent;
    [SerializeField] private float kickedPlayerBanSeconds = 300f;

    private readonly Dictionary<ulong, string> clientIdentities = new();
    private readonly Dictionary<string, float> kickedPlayerBanExpiryByIdentity = new();

    public int MaxPlayers => maxPlayers;

    private void Awake()
    {
        // Expose the active room policy so room-control code can register kicked clients.
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[RoomPolicy] Duplicate RoomPolicy detected. Keeping the first instance.");
            enabled = false;
            return;
        }

        Instance = this;
    }

    public void SetMaxPlayers(int value)
    {
        // Apply the max-player value requested by the dedicated server bootstrap.
        maxPlayers = Mathf.Max(1, value);
        Debug.Log($"[RoomPolicy] MaxPlayers set to {maxPlayers}");
    }

    public void RegisterKickedClient(ulong clientId)
    {
        // Ban the kicked client's stable connection identity before the server disconnects them.
        if (!banKickedPlayersFromRejoining)
        {
            return;
        }

        if (!clientIdentities.TryGetValue(clientId, out string clientIdentity) ||
            string.IsNullOrWhiteSpace(clientIdentity))
        {
            Debug.LogWarning($"[RoomPolicy] Kick ban skipped. Missing client identity for clientId={clientId}");
            return;
        }

        float expiresAt = kickedPlayerBanIsPermanent || kickedPlayerBanSeconds <= 0f
            ? float.PositiveInfinity
            : Time.realtimeSinceStartup + kickedPlayerBanSeconds;
        kickedPlayerBanExpiryByIdentity[clientIdentity] = expiresAt;
        Debug.Log($"[RoomPolicy] Kick ban registered clientId={clientId} identity={clientIdentity} permanent={float.IsPositiveInfinity(expiresAt)}");
    }

    private void OnEnable()
    {
        // Register connection approval and disconnect hooks with Netcode.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.ConnectionApprovalCallback += OnConnectionApproval;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        Debug.Log($"[RoomPolicy] Enabled. MaxPlayers={maxPlayers}");
    }

    private void OnDisable()
    {
        // Remove Netcode hooks when the policy is disabled.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        NetworkManager.Singleton.ConnectionApprovalCallback -= OnConnectionApproval;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        Debug.Log("[RoomPolicy] Disabled.");
    }

    private void OnDestroy()
    {
        // Clear the singleton when the scene object is destroyed.
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void OnConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // Decide whether a connection may enter this room based on bans, capacity, and match state.
        int currentPlayers = NetworkManager.Singleton.ConnectedClientsIds.Count;
        string clientIdentity = DecodeClientIdentity(request.Payload, request.ClientNetworkId);
        bool approved = CanAcceptConnection(currentPlayers, clientIdentity, out string rejectReason);

        response.Approved = approved;
        response.CreatePlayerObject = approved;
        response.Pending = false;

        if (approved)
        {
            clientIdentities[request.ClientNetworkId] = clientIdentity;
        }
        else
        {
            response.Reason = rejectReason;
        }

        Debug.Log($"[RoomPolicy] Approval clientId={request.ClientNetworkId} identity={clientIdentity} connected={currentPlayers} max={maxPlayers} approved={approved} reason='{rejectReason}'");
    }

    private bool CanAcceptConnection(int currentPlayers, string clientIdentity, out string rejectReason)
    {
        // Reject kicked identities first, then enforce capacity and in-progress match rules.
        PruneExpiredKickBans();
        if (IsKickedIdentityBanned(clientIdentity, out rejectReason))
        {
            return false;
        }

        if (currentPlayers >= maxPlayers)
        {
            rejectReason = "Room is full.";
            return false;
        }

        MatchStateController controller = MatchStateController.Instance;
        if (controller != null && controller.IsSpawned && IsMatchInProgress(controller.State.Value))
        {
            rejectReason = "Match is in progress.";
            return false;
        }

        rejectReason = string.Empty;
        return true;
    }

    private bool IsKickedIdentityBanned(string clientIdentity, out string rejectReason)
    {
        // Return whether a stable client identity is still blocked from this room.
        rejectReason = string.Empty;
        if (string.IsNullOrWhiteSpace(clientIdentity) ||
            !kickedPlayerBanExpiryByIdentity.TryGetValue(clientIdentity, out float expiresAt))
        {
            return false;
        }

        if (float.IsPositiveInfinity(expiresAt))
        {
            rejectReason = "You were kicked from this room.";
            return true;
        }

        float remainingSeconds = expiresAt - Time.realtimeSinceStartup;
        if (remainingSeconds <= 0f)
        {
            kickedPlayerBanExpiryByIdentity.Remove(clientIdentity);
            return false;
        }

        rejectReason = $"You were kicked from this room. Try again in {Mathf.CeilToInt(remainingSeconds)} seconds.";
        return true;
    }

    private void PruneExpiredKickBans()
    {
        // Remove expired temporary kick bans before each connection approval check.
        if (kickedPlayerBanExpiryByIdentity.Count == 0)
        {
            return;
        }

        List<string> expiredIdentities = null;
        float now = Time.realtimeSinceStartup;
        foreach (KeyValuePair<string, float> ban in kickedPlayerBanExpiryByIdentity)
        {
            if (!float.IsPositiveInfinity(ban.Value) && ban.Value <= now)
            {
                expiredIdentities ??= new List<string>();
                expiredIdentities.Add(ban.Key);
            }
        }

        if (expiredIdentities == null)
        {
            return;
        }

        for (int i = 0; i < expiredIdentities.Count; i++)
        {
            kickedPlayerBanExpiryByIdentity.Remove(expiredIdentities[i]);
        }
    }

    private static bool IsMatchInProgress(NetworkMatchState state)
    {
        // Treat every active gameplay phase as closed to new joins.
        return state == NetworkMatchState.MatchMain ||
            state == NetworkMatchState.FinalTransition ||
            state == NetworkMatchState.FinalMatch;
    }

    private static string DecodeClientIdentity(byte[] payload, ulong clientId)
    {
        // Decode the stable client identity sent by NetworkBootstrap, falling back for old clients.
        if (payload != null && payload.Length > 0)
        {
            string payloadText = Encoding.UTF8.GetString(payload);
            if (payloadText.StartsWith(ConnectionPayloadPrefix))
            {
                string identity = payloadText[ConnectionPayloadPrefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    return identity;
                }
            }
        }

        return UnknownIdentityPrefix + clientId;
    }

    private void OnClientDisconnected(ulong clientId)
    {
        // Remove transient identity mapping and forward active-match disconnects to match defeat logic.
        clientIdentities.Remove(clientId);
        Debug.Log($"[RoomPolicy] Client disconnected clientId={clientId}. Marking as defeated.");

        MatchStateController controller = MatchStateController.Instance;
        if (controller == null)
        {
            return;
        }

        controller.MarkAsDefeated(clientId);
    }
}
