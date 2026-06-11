using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayerEquipmentState : NetworkBehaviour
{
    private static readonly Dictionary<ulong, NetworkPlayerEquipmentState> StatesByClientId = new();

    [Header("Defaults")]
    [SerializeField] private EquipmentDefinition defaultEquipment;

    private readonly NetworkVariable<FixedString64Bytes> equippedEquipmentId = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public EquipmentDefinition CurrentEquipment => EquipmentCatalog.Get(equippedEquipmentId.Value.ToString());
    public bool HasEquipment => CurrentEquipment != null;
    public bool CanAttack => NetworkPlayerCombatState.ClientCanAct(OwnerClientId) && CurrentEquipment != null && CurrentEquipment.CanAttack;
    public bool CanCollectItems => NetworkPlayerCombatState.ClientCanAct(OwnerClientId) && CurrentEquipment != null && CurrentEquipment.CanCollectItems;

    public override void OnNetworkSpawn()
    {
        // Register this spawned player equipment state and assign the default equipment on the server.
        StatesByClientId[OwnerClientId] = this;
        equippedEquipmentId.OnValueChanged += OnEquipmentIdChanged;

        if (IsServer && string.IsNullOrWhiteSpace(equippedEquipmentId.Value.ToString()))
        {
            Equip(defaultEquipment);
        }

        TryBindLocalPlayerEquipment();
    }

    public override void OnNetworkDespawn()
    {
        // Remove static lookup entries and event subscriptions when this player despawns.
        equippedEquipmentId.OnValueChanged -= OnEquipmentIdChanged;
        if (StatesByClientId.TryGetValue(OwnerClientId, out NetworkPlayerEquipmentState state) && state == this)
        {
            StatesByClientId.Remove(OwnerClientId);
        }
    }

    public void Equip(EquipmentDefinition equipment)
    {
        // Server-side equip entry point for assigning a stable equipment id.
        if (!IsServer || equipment == null)
        {
            return;
        }

        equippedEquipmentId.Value = equipment.EquipmentId;
    }

    public void EquipDefault()
    {
        // Server-side helper for restoring the player's default starting equipment.
        Equip(defaultEquipment);
    }

    public void Unequip()
    {
        // Server-side unequip entry point that leaves the player without health-bearing equipment.
        if (!IsServer)
        {
            return;
        }

        equippedEquipmentId.Value = default;
    }

    public static bool ClientCanCollectItems(ulong clientId)
    {
        // Check whether the requested client currently has equipment that can collect items.
        return StatesByClientId.TryGetValue(clientId, out NetworkPlayerEquipmentState state) &&
            state.CanCollectItems;
    }

    public static bool ClientCanAttack(ulong clientId)
    {
        // Check whether the requested client currently has equipment that can attack.
        return StatesByClientId.TryGetValue(clientId, out NetworkPlayerEquipmentState state) &&
            state.CanAttack;
    }

    public static void EquipDefaultForAll()
    {
        // Restore default equipment for every connected player on the server.
        foreach (NetworkPlayerEquipmentState state in StatesByClientId.Values)
        {
            if (state != null && state.IsServer)
            {
                state.EquipDefault();
            }
        }
    }

    public static List<ulong> EquipDefaultForUnequippedAll()
    {
        // Restore default equipment only for players whose equipment was broken or removed.
        List<ulong> restoredClientIds = new();
        foreach (NetworkPlayerEquipmentState state in StatesByClientId.Values)
        {
            if (state != null && state.IsServer && !state.HasEquipment)
            {
                state.EquipDefault();
                restoredClientIds.Add(state.OwnerClientId);
                Debug.Log($"[NetworkPlayerEquipmentState] Default equipment restored for final match clientId={state.OwnerClientId}");
            }
        }

        return restoredClientIds;
    }

    private void OnEquipmentIdChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        // Refresh the local PlayerEquipment component when the network equipment id changes.
        TryBindLocalPlayerEquipment();
    }

    private void TryBindLocalPlayerEquipment()
    {
        // Owners mirror their network equipment state onto the local test character.
        if (!IsOwner)
        {
            return;
        }

        PlayerEquipment playerEquipment = FindFirstObjectByType<PlayerEquipment>();
        if (playerEquipment != null)
        {
            playerEquipment.BindNetworkState(this);
        }
    }
}
