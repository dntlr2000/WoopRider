using UnityEngine;

public class PlayerEquipment : MonoBehaviour
{
    [Header("Defaults")]
    [SerializeField] private EquipmentDefinition defaultEquipment;
    [SerializeField] private bool equipDefaultOnStart = true;

    private EquipmentDefinition currentEquipment;
    private NetworkPlayerEquipmentState networkState;

    public EquipmentDefinition CurrentEquipment => currentEquipment;
    public bool HasEquipment => currentEquipment != null;
    public bool CanAttack => currentEquipment != null && currentEquipment.CanAttack;
    public bool CanCollectItems => currentEquipment != null && currentEquipment.CanCollectItems;

    private void Awake()
    {
        // Equip the local default early so offline tests and first-frame input have equipment state.
        if (equipDefaultOnStart)
        {
            Equip(defaultEquipment);
        }
    }

    public void BindNetworkState(NetworkPlayerEquipmentState state)
    {
        // Keep the local player equipment view aligned with the owned network equipment state.
        networkState = state;
        if (networkState != null && networkState.CurrentEquipment != null)
        {
            Equip(networkState.CurrentEquipment);
        }
    }

    public void Equip(EquipmentDefinition equipment)
    {
        // Set the current equipment definition used by local movement, collection, and attack checks.
        currentEquipment = equipment;
    }

    public float ModifyStat(PlayerStatType statType, float baseValue)
    {
        // Apply the currently equipped item's modifiers to a base stat value.
        if (currentEquipment == null)
        {
            return baseValue;
        }

        return currentEquipment.ModifyStat(statType, baseValue);
    }
}
