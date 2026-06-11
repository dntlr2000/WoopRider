using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkPlayerEquipmentState))]
public class NetworkPlayerCombatState : NetworkBehaviour
{
    private static readonly Dictionary<ulong, NetworkPlayerCombatState> StatesByClientId = new();

    [Header("Health")]
    [SerializeField] private float defaultMaxHealth = 100f;

    [Header("Break State")]
    [SerializeField] private float equipmentBreakActionLockDuration = 3f;

    private readonly NetworkVariable<float> currentHealth = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> isInvincible = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> isActionDisabled = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private NetworkPlayerEquipmentState equipmentState;
    private Coroutine actionLockRoutine;
    private bool hadEquipmentLastFrame;

    public float CurrentHealth => currentHealth.Value;
    public bool HasHealth => equipmentState != null && equipmentState.HasEquipment;
    public bool IsInvincible => isInvincible.Value;
    public bool IsActionDisabled => isActionDisabled.Value;

    public override void OnNetworkSpawn()
    {
        // Register this combat state and initialize health when equipment exists.
        equipmentState = GetComponent<NetworkPlayerEquipmentState>();
        StatesByClientId[OwnerClientId] = this;
        hadEquipmentLastFrame = equipmentState != null && equipmentState.HasEquipment;

        if (IsServer)
        {
            RefreshHealthForEquipmentState();
        }

        TryBindLocalPlayerEquipment();
    }

    public override void OnNetworkDespawn()
    {
        // Remove static lookups and stop server routines when the player despawns.
        if (StatesByClientId.TryGetValue(OwnerClientId, out NetworkPlayerCombatState state) && state == this)
        {
            StatesByClientId.Remove(OwnerClientId);
        }

        if (actionLockRoutine != null)
        {
            StopCoroutine(actionLockRoutine);
            actionLockRoutine = null;
        }
    }

    private void Update()
    {
        // Server watches equipment transitions so health appears only while equipment is equipped.
        if (!IsServer)
        {
            return;
        }

        bool hasEquipmentNow = equipmentState != null && equipmentState.HasEquipment;
        if (hasEquipmentNow == hadEquipmentLastFrame)
        {
            return;
        }

        hadEquipmentLastFrame = hasEquipmentNow;
        RefreshHealthForEquipmentState();
    }

    public bool ApplyDamage(float amount, ulong attackerClientId)
    {
        // Apply server-authoritative damage only when this player has equipment-backed health.
        if (!IsServer || amount <= 0f || !CanReceiveDamage())
        {
            return false;
        }

        currentHealth.Value = Mathf.Max(0f, currentHealth.Value - amount);
        Debug.Log($"[NetworkPlayerCombatState] Damage target={OwnerClientId} attacker={attackerClientId} amount={amount:0.0} health={currentHealth.Value:0.0}");

        if (currentHealth.Value <= 0f)
        {
            BreakEquipmentAndDisableActions(attackerClientId);
        }

        return true;
    }

    public void SetInvincible(bool value)
    {
        // Server-side state setter for temporary or rule-driven invincibility.
        if (!IsServer)
        {
            return;
        }

        isInvincible.Value = value;
    }

    public void SetActionDisabled(bool value)
    {
        // Server-side state setter for blocking player actions.
        if (!IsServer)
        {
            return;
        }

        isActionDisabled.Value = value;
        TryBindLocalPlayerEquipment();
    }

    public void ResetForMatchStart()
    {
        // Restore combat flags and health from the current equipment at the start of a match.
        if (!IsServer)
        {
            return;
        }

        if (actionLockRoutine != null)
        {
            StopCoroutine(actionLockRoutine);
            actionLockRoutine = null;
        }

        isInvincible.Value = false;
        isActionDisabled.Value = false;
        hadEquipmentLastFrame = equipmentState != null && equipmentState.HasEquipment;
        RefreshHealthForEquipmentState();
        TryBindLocalPlayerEquipment();
    }

    public static bool ClientCanAct(ulong clientId)
    {
        // Missing combat state defaults to true so older/offline objects keep working.
        return !StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) ||
            !state.IsActionDisabled;
    }

    public static bool TryApplyDamage(ulong targetClientId, float amount, ulong attackerClientId)
    {
        // Static lookup helper used by server-side attack resolution.
        return StatesByClientId.TryGetValue(targetClientId, out NetworkPlayerCombatState state) &&
            state.ApplyDamage(amount, attackerClientId);
    }

    public static void ResetForMatchStartForAll()
    {
        // Reset combat flags and health for every connected player on the server.
        foreach (NetworkPlayerCombatState state in StatesByClientId.Values)
        {
            if (state != null && state.IsServer)
            {
                state.ResetForMatchStart();
            }
        }
    }

    public static void ResetForClients(IEnumerable<ulong> clientIds)
    {
        // Reset combat flags and health only for the listed clients after a rule-driven equipment restore.
        foreach (ulong clientId in clientIds)
        {
            if (StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) &&
                state != null &&
                state.IsServer)
            {
                state.ResetForMatchStart();
            }
        }
    }

    private bool CanReceiveDamage()
    {
        // Unequipped players have no health, and invincible players ignore incoming damage.
        return equipmentState != null &&
            equipmentState.HasEquipment &&
            !isInvincible.Value &&
            currentHealth.Value > 0f;
    }

    private void RefreshHealthForEquipmentState()
    {
        // Give health when equipment exists, otherwise clear health because unequipped players are not damageable.
        if (equipmentState != null && equipmentState.HasEquipment)
        {
            currentHealth.Value = Mathf.Max(1f, defaultMaxHealth);
            return;
        }

        currentHealth.Value = 0f;
    }

    private void BreakEquipmentAndDisableActions(ulong attackerClientId)
    {
        // On health depletion, remove equipment and lock actions for a short recovery window.
        Debug.Log($"[NetworkPlayerCombatState] Equipment broken target={OwnerClientId} attacker={attackerClientId}");
        currentHealth.Value = 0f;
        isInvincible.Value = true;
        isActionDisabled.Value = true;
        equipmentState?.Unequip();
        hadEquipmentLastFrame = false;

        if (actionLockRoutine != null)
        {
            StopCoroutine(actionLockRoutine);
        }

        actionLockRoutine = StartCoroutine(ClearActionLockAfterDelay());
    }

    private IEnumerator ClearActionLockAfterDelay()
    {
        // Keep the player unable to act briefly after equipment breaks, then return to unequipped control.
        yield return new WaitForSeconds(equipmentBreakActionLockDuration);

        isActionDisabled.Value = false;
        isInvincible.Value = false;
        actionLockRoutine = null;
        TryBindLocalPlayerEquipment();
    }

    private void TryBindLocalPlayerEquipment()
    {
        // Owners mirror combat state locally so movement, collection, and attack can respect action lock.
        if (!IsOwner)
        {
            return;
        }

        PlayerEquipment playerEquipment = FindFirstObjectByType<PlayerEquipment>();
        if (playerEquipment != null)
        {
            playerEquipment.BindCombatState(this);
        }
    }
}
