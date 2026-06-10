using System;
using UnityEngine;

public enum EquipmentAttackMode : byte
{
    Projectile = 0,
    Hitscan = 1
}

[Serializable]
public class EquipmentAttackSettings
{
    [SerializeField] private EquipmentAttackMode attackMode = EquipmentAttackMode.Projectile;
    [SerializeField] private float shotsPerSecondOverride;
    [SerializeField] private float damageMultiplier = 1f;
    [SerializeField] private float range = 100f;
    [SerializeField] private float projectileSpeed = 32f;
    [SerializeField] private float projectileRadius = 0.12f;
    [SerializeField] private float projectileLifeTime = 4f;

    public EquipmentAttackMode AttackMode => attackMode;
    public float ShotsPerSecondOverride => shotsPerSecondOverride;
    public float DamageMultiplier => damageMultiplier;
    public float Range => range;
    public float ProjectileSpeed => projectileSpeed;
    public float ProjectileRadius => projectileRadius;
    public float ProjectileLifeTime => projectileLifeTime;
}

[Serializable]
public class EquipmentStatModifier
{
    [SerializeField] private PlayerStatType statType;
    [SerializeField] private float flatBonus;
    [SerializeField] private float multiplier = 1f;

    public PlayerStatType StatType => statType;
    public float FlatBonus => flatBonus;
    public float Multiplier => multiplier;
}

[CreateAssetMenu(menuName = "WoopRider/Equipment Definition")]
public class EquipmentDefinition : ScriptableObject
{
    [Header("Identity")]
    [SerializeField] private string equipmentId = "equipment";
    [SerializeField] private string displayName = "Equipment";

    [Header("Permissions")]
    [SerializeField] private bool canAttack = true;
    [SerializeField] private bool canCollectItems = true;

    [Header("Stats")]
    [SerializeField] private EquipmentStatModifier[] statModifiers = Array.Empty<EquipmentStatModifier>();

    [Header("Attack")]
    [SerializeField] private EquipmentAttackSettings attack = new();

    [Header("Visual")]
    [SerializeField] private GameObject visualPrefab;

    public string EquipmentId => equipmentId;
    public string DisplayName => displayName;
    public bool CanAttack => canAttack;
    public bool CanCollectItems => canCollectItems;
    public EquipmentAttackSettings Attack => attack;
    public GameObject VisualPrefab => visualPrefab;

    public float ModifyStat(PlayerStatType statType, float baseValue)
    {
        // Apply matching flat bonuses first, then stack multipliers for the final stat value.
        float flatBonus = 0f;
        float multiplier = 1f;

        for (int i = 0; i < statModifiers.Length; i++)
        {
            EquipmentStatModifier modifier = statModifiers[i];
            if (modifier == null || modifier.StatType != statType)
            {
                continue;
            }

            flatBonus += modifier.FlatBonus;
            multiplier *= Mathf.Approximately(modifier.Multiplier, 0f) ? 1f : modifier.Multiplier;
        }

        return Mathf.Max(0f, (baseValue + flatBonus) * multiplier);
    }
}
