using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkPlayerEquipmentState))]
public class NetworkPlayerCombatState : NetworkBehaviour
{
    private static readonly Dictionary<ulong, NetworkPlayerCombatState> StatesByClientId = new();
    private const string DefaultLowHealthSparkResourcePath = "Effects/CustomEffects/SmokeLeak_RedSparks";
    private const string DefaultAttackUpEffectResourcePath = "Effects/CustomEffects/AttackUp";
    private const string DefaultDefenceUpEffectResourcePath = "Effects/CustomEffects/DefenceUp";
    private const string DefaultSpeedUpEffectResourcePath = "Effects/CustomEffects/SpeedUp";
    private const string DefaultDamageHitEffectResourcePath = "Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Green hit";
    private const string DefaultBreakExplosionEffectResourcePath = "Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Explosion";
    private const string LowHealthSparkAnchorName = "EffectPoint_Spark";
    private const string HitEffectAnchorName = "HitEffectPoint";
    private const string BuffEffectAnchorName = "BuffEffectPoint";
    private const float SplashTargetHeight = 1.65f;
    private const float FallbackSplashBodyRadius = 0.65f;

    [Header("Health")]
    [SerializeField] private float defaultMaxHealth = 100f;

    [Header("Defense")]
    [SerializeField] private float defaultDefense = 10f;

    [Header("Break State")]
    [SerializeField] private float equipmentBreakActionLockDuration = 3f;

    [Header("Functional Buffs")]
    [SerializeField] private float defaultAttackBuffMultiplier = 2f;
    [SerializeField] private float defaultDamageTakenMultiplier = 0.5f;
    [SerializeField] private float defaultMoveSpeedBuffMultiplier = 2f;

    [Header("Low Health Effect")]
    [Range(0f, 1f)]
    [SerializeField] private float lowHealthSparkThreshold = 0.2f;
    [SerializeField] private ParticleSystem lowHealthSparkPrefab;
    [SerializeField] private Transform lowHealthSparkAnchor;
    [SerializeField] private Vector3 lowHealthSparkLocalOffset = new(0f, 1.5f, 0f);
    [SerializeField] private Vector3 lowHealthSparkLocalEulerAngles = new(-20f, 180f, 0f);
    [Min(0.01f)]
    [SerializeField] private float lowHealthSparkScale = 2f;
    [SerializeField] private float lowHealthSparkRate = 14f;

    [Header("Buff Effects")]
    [SerializeField] private GameObject attackUpEffectPrefab;
    [SerializeField] private GameObject defenceUpEffectPrefab;
    [SerializeField] private GameObject speedUpEffectPrefab;
    [SerializeField] private Transform buffEffectAnchor;
    [SerializeField] private Vector3 buffEffectLocalOffset = new(0f, 0.25f, 0f);
    [SerializeField] private Vector3 buffEffectLocalEulerAngles;
    [Min(0.01f)]
    [SerializeField] private float buffEffectScale = 1f;

    [Header("Hit Effects")]
    [SerializeField] private GameObject damageHitEffectPrefab;
    [SerializeField] private GameObject equipmentBreakExplosionPrefab;
    [SerializeField] private Vector3 damageHitEffectEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float damageHitEffectScale = 1f;
    [SerializeField] private float damageHitEffectLifetime = 2f;
    [SerializeField] private Vector3 breakExplosionEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float breakExplosionScale = 1f;
    [SerializeField] private float breakExplosionLifetime = 3f;

    [Header("Splash Damage")]
    [SerializeField] private float splashBodyRadius = FallbackSplashBodyRadius;

    private readonly NetworkVariable<float> currentHealth = new(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> attackBuffActive = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> damageReductionBuffActive = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> moveSpeedBuffActive = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<float> replicatedMoveSpeedBuffMultiplier = new(
        1f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> autoFireBuffActive = new(
        false,
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
    private ParticleSystem lowHealthSparkEffect;
    private GameObject attackUpEffectObject;
    private GameObject defenceUpEffectObject;
    private GameObject speedUpEffectObject;
    private ParticleSystem resolvedDefaultLowHealthSparkPrefab;
    private GameObject resolvedDefaultAttackUpEffectPrefab;
    private GameObject resolvedDefaultDefenceUpEffectPrefab;
    private GameObject resolvedDefaultSpeedUpEffectPrefab;
    private GameObject resolvedDefaultDamageHitEffectPrefab;
    private GameObject resolvedDefaultBreakExplosionEffectPrefab;
    private bool triedLoadDefaultLowHealthSparkPrefab;
    private bool triedLoadDefaultAttackUpEffectPrefab;
    private bool triedLoadDefaultDefenceUpEffectPrefab;
    private bool triedLoadDefaultSpeedUpEffectPrefab;
    private bool triedLoadDefaultDamageHitEffectPrefab;
    private bool triedLoadDefaultBreakExplosionEffectPrefab;
    private bool hadEquipmentLastFrame;
    private float attackBuffExpireTime;
    private float attackBuffMultiplier = 1f;
    private float damageReductionExpireTime;
    private float damageTakenMultiplier = 1f;
    private float moveSpeedBuffExpireTime;
    private float moveSpeedBuffMultiplier = 1f;
    private float autoFireBuffExpireTime;

    public float CurrentHealth => currentHealth.Value;
    public float MaxHealth => ResolveMaxHealthForEquipment();
    public bool HasHealth => equipmentState != null && equipmentState.HasEquipment;
    public float EquipmentHealthPercent => ResolveEquipmentHealthPercent();
    public bool IsInvincible => isInvincible.Value;
    public bool IsActionDisabled => isActionDisabled.Value;
    public bool CanBeHookStolen => CanLoseEquipmentToHook();
    public bool HasAutoFireBuff => autoFireBuffActive.Value;

    public override void OnNetworkSpawn()
    {
        // Register this combat state and initialize health when equipment exists.
        equipmentState = GetComponent<NetworkPlayerEquipmentState>();
        StatesByClientId[OwnerClientId] = this;
        hadEquipmentLastFrame = equipmentState != null && equipmentState.HasEquipment;
        currentHealth.OnValueChanged += OnHealthChanged;
        attackBuffActive.OnValueChanged += OnAttackBuffActiveChanged;
        damageReductionBuffActive.OnValueChanged += OnDamageReductionBuffActiveChanged;
        moveSpeedBuffActive.OnValueChanged += OnMoveSpeedBuffActiveChanged;

        if (IsServer)
        {
            RefreshHealthForEquipmentState();
        }

        TryBindLocalPlayerEquipment();
        UpdateLowHealthSparkEffect();
        UpdateBuffEffects();
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

        currentHealth.OnValueChanged -= OnHealthChanged;
        attackBuffActive.OnValueChanged -= OnAttackBuffActiveChanged;
        damageReductionBuffActive.OnValueChanged -= OnDamageReductionBuffActiveChanged;
        moveSpeedBuffActive.OnValueChanged -= OnMoveSpeedBuffActiveChanged;
        DestroyLowHealthSparkEffect();
        DestroyBuffEffects();
    }

    private void Update()
    {
        UpdateLowHealthSparkEffect();
        UpdateBuffEffects();

        // Server watches equipment transitions so health appears only while equipment is equipped.
        if (!IsServer)
        {
            return;
        }

        RefreshExpiredFunctionalBuffs();

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
        // Apply server-authoritative damage with fallback hit effect placement.
        return ApplyDamage(amount, attackerClientId, ResolveHitEffectPoint(), ResolveFallbackHitDirection());
    }

    public bool ApplyDamage(float amount, ulong attackerClientId, Vector3 hitPoint, Vector3 hitDirection)
    {
        // Apply server-authoritative damage only when this player has equipment-backed health.
        if (!IsServer || amount <= 0f || !CanReceiveDamage())
        {
            return false;
        }

        float resolvedDamage = ResolveDamageAfterDefense(amount, out float defense);
        currentHealth.Value = Mathf.Max(0f, currentHealth.Value - resolvedDamage);
        bool equipmentDestroyed = currentHealth.Value <= 0f;
        string damagedEquipmentId = equipmentState != null && equipmentState.CurrentEquipment != null
            ? equipmentState.CurrentEquipment.EquipmentId
            : string.Empty;
        Debug.Log($"[NetworkPlayerCombatState] Damage target={OwnerClientId} attacker={attackerClientId} raw={amount:0.0} defense={defense:0.0} amount={resolvedDamage:0.0} health={currentHealth.Value:0.0}");
        PlayDamageHitEffectClientRpc(ResolveEffectPoint(hitPoint), ResolveEffectDirection(hitDirection));
        PlayOwnerDamageFeedback(equipmentDestroyed, damagedEquipmentId);

        if (equipmentDestroyed)
        {
            BreakEquipmentAndDisableActions(attackerClientId, hitPoint, hitDirection);
        }

        return true;
    }

    public bool ApplyAttackBuff(float duration, float multiplier)
    {
        // Server-side pickup effect that refreshes a temporary outgoing damage multiplier.
        if (!IsServer || duration <= 0f || multiplier <= 0f || equipmentState == null || !equipmentState.HasEquipment)
        {
            return false;
        }

        attackBuffMultiplier = Mathf.Max(0.01f, multiplier);
        attackBuffExpireTime = Time.time + duration;
        attackBuffActive.Value = true;
        Debug.Log($"[NetworkPlayerCombatState] Attack buff applied target={OwnerClientId} multiplier={attackBuffMultiplier:0.00} duration={duration:0.0}s");
        return true;
    }

    public bool ApplyDamageReductionBuff(float duration, float incomingDamageMultiplier)
    {
        // Server-side pickup effect that refreshes temporary incoming damage reduction.
        if (!IsServer || duration <= 0f || incomingDamageMultiplier <= 0f || equipmentState == null || !equipmentState.HasEquipment)
        {
            return false;
        }

        damageTakenMultiplier = Mathf.Clamp(incomingDamageMultiplier, 0.01f, 1f);
        damageReductionExpireTime = Time.time + duration;
        damageReductionBuffActive.Value = true;
        Debug.Log($"[NetworkPlayerCombatState] Damage reduction buff applied target={OwnerClientId} damageTakenMultiplier={damageTakenMultiplier:0.00} duration={duration:0.0}s");
        return true;
    }

    public bool ApplyMoveSpeedBuff(float duration, float multiplier)
    {
        // Server-side pickup effect that refreshes a temporary local movement speed multiplier.
        if (!IsServer || duration <= 0f || multiplier <= 0f || equipmentState == null || !equipmentState.HasEquipment)
        {
            return false;
        }

        moveSpeedBuffMultiplier = Mathf.Max(0.01f, multiplier);
        moveSpeedBuffExpireTime = Time.time + duration;
        replicatedMoveSpeedBuffMultiplier.Value = moveSpeedBuffMultiplier;
        moveSpeedBuffActive.Value = true;
        Debug.Log($"[NetworkPlayerCombatState] Move speed buff applied target={OwnerClientId} multiplier={moveSpeedBuffMultiplier:0.00} duration={duration:0.0}s");
        return true;
    }

    public bool ApplyAutoFireBuff(float duration)
    {
        // Server-side pickup effect that lets the owner fire continuously without holding input.
        return ApplyAutoFireBuffUntil(Time.time + duration, "pickup");
    }

    public bool ApplyAutoFireBuffUntil(float endTime, string sourceLabel)
    {
        // Server-side helper that keeps auto-fire active until a shared event or pickup end time.
        if (!IsServer || endTime <= Time.time || equipmentState == null || !equipmentState.HasEquipment)
        {
            return false;
        }

        float resolvedEndTime = Mathf.Max(autoFireBuffExpireTime, endTime);
        if (resolvedEndTime <= Time.time)
        {
            return false;
        }

        bool shouldLog = !autoFireBuffActive.Value || resolvedEndTime > autoFireBuffExpireTime + 0.05f;
        autoFireBuffExpireTime = resolvedEndTime;
        autoFireBuffActive.Value = true;
        if (shouldLog)
        {
            Debug.Log($"[NetworkPlayerCombatState] Auto fire buff applied target={OwnerClientId} source={sourceLabel} remaining={autoFireBuffExpireTime - Time.time:0.0}s");
        }

        return true;
    }

    public bool HealByMaxHealthPercent(float maxHealthPercent, string sourceLabel)
    {
        // Consume a heal effect and restore equipment-backed health when there is missing health.
        if (!IsServer ||
            maxHealthPercent <= 0f ||
            equipmentState == null ||
            !equipmentState.HasEquipment ||
            currentHealth.Value <= 0f)
        {
            return false;
        }

        float maxHealth = ResolveMaxHealthForEquipment();
        float previousHealth = currentHealth.Value;
        currentHealth.Value = Mathf.Min(maxHealth, currentHealth.Value + maxHealth * Mathf.Clamp01(maxHealthPercent));
        Debug.Log($"[NetworkPlayerCombatState] Heal target={OwnerClientId} source={sourceLabel} amount={currentHealth.Value - previousHealth:0.0} health={currentHealth.Value:0.0}");
        return true;
    }

    public bool TryStealEquipmentByHook(ulong stealerClientId, out EquipmentDefinition stolenEquipment, out float stolenHealthPercent)
    {
        // Remove this player's low-health equipment for a successful hook steal and lock actions briefly.
        stolenEquipment = null;
        stolenHealthPercent = 0f;
        if (!IsServer || !CanLoseEquipmentToHook())
        {
            return false;
        }

        stolenEquipment = equipmentState.CurrentEquipment;
        stolenHealthPercent = ResolveEquipmentHealthPercent();
        currentHealth.Value = 0f;
        ClearFunctionalBuffs();
        isInvincible.Value = true;
        isActionDisabled.Value = true;
        equipmentState.Unequip();
        hadEquipmentLastFrame = false;

        if (actionLockRoutine != null)
        {
            StopCoroutine(actionLockRoutine);
        }

        actionLockRoutine = StartCoroutine(ClearActionLockAfterDelay());
        Debug.Log($"[NetworkPlayerCombatState] Equipment stolen victim={OwnerClientId} stealer={stealerClientId} equipment={stolenEquipment.EquipmentId} healthPercent={stolenHealthPercent:0.00}");
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
        ClearFunctionalBuffs();
        hadEquipmentLastFrame = equipmentState != null && equipmentState.HasEquipment;
        RefreshHealthForEquipmentState();
        TryBindLocalPlayerEquipment();
    }

    public void ResetForEquipmentHealthPercent(float healthPercent)
    {
        // Restore combat flags and set health from the equipped drop's stored health ratio.
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
        ApplyEquipmentHealthPercent(healthPercent);
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

    public static bool TryApplyDamage(ulong targetClientId, float amount, ulong attackerClientId, Vector3 hitPoint, Vector3 hitDirection)
    {
        // Static lookup helper used by attack systems that know the impact point and incoming direction.
        return StatesByClientId.TryGetValue(targetClientId, out NetworkPlayerCombatState state) &&
            state.ApplyDamage(amount, attackerClientId, hitPoint, hitDirection);
    }

    public static int ApplySplashDamage(Vector3 center, float radius, float baseDamage, ulong attackerClientId, float minimumMultiplier, float selfMultiplier)
    {
        // Apply distance-falloff splash damage to every equipped player inside the explosion radius.
        return ApplySplashDamage(
            center,
            radius,
            baseDamage,
            attackerClientId,
            minimumMultiplier,
            selfMultiplier,
            out _);
    }

    public static int ApplySplashDamage(
        Vector3 center,
        float radius,
        float baseDamage,
        ulong attackerClientId,
        float minimumMultiplier,
        float selfMultiplier,
        out int opposingPlayerHitCount)
    {
        // Apply splash damage and separately report opposing-player hits for attacker feedback.
        opposingPlayerHitCount = 0;
        if (baseDamage <= 0f || radius <= 0f)
        {
            return 0;
        }

        int hitCount = 0;
        float resolvedRadius = Mathf.Max(0.01f, radius);
        float resolvedMinimumMultiplier = Mathf.Clamp01(minimumMultiplier);
        float resolvedSelfMultiplier = Mathf.Clamp01(selfMultiplier);
        List<NetworkPlayerCombatState> states = new(StatesByClientId.Values);

        for (int i = 0; i < states.Count; i++)
        {
            NetworkPlayerCombatState state = states[i];
            if (state == null || !state.IsServer || !state.IsSpawned || !state.CanReceiveDamage())
            {
                continue;
            }

            if (!TryGetSplashTargetPoint(state, center, resolvedRadius, out float distance, out Vector3 hitPoint))
            {
                continue;
            }

            float normalizedDistance = Mathf.Clamp01(distance / resolvedRadius);
            float damageMultiplier = Mathf.Lerp(1f, resolvedMinimumMultiplier, normalizedDistance);
            if (state.OwnerClientId == attackerClientId)
            {
                damageMultiplier *= resolvedSelfMultiplier;
            }

            Vector3 hitDirection = ResolveSplashHitDirection(center, hitPoint);
            if (state.ApplyDamage(baseDamage * damageMultiplier, attackerClientId, hitPoint, hitDirection))
            {
                hitCount++;
                if (state.OwnerClientId != attackerClientId)
                {
                    opposingPlayerHitCount++;
                }
            }
        }

        return hitCount;
    }

    public static bool TryHealPercent(ulong targetClientId, float maxHealthPercent)
    {
        // Static lookup helper used by server-side functional pickup resolution.
        return TryHealPercent(targetClientId, maxHealthPercent, "pickup");
    }

    public static bool TryHealPercent(ulong targetClientId, float maxHealthPercent, string sourceLabel)
    {
        // Static lookup helper used by server-side healing effects with a custom log source.
        return StatesByClientId.TryGetValue(targetClientId, out NetworkPlayerCombatState state) &&
            state.HealByMaxHealthPercent(maxHealthPercent, sourceLabel);
    }

    public static bool TryApplyAttackBuff(ulong targetClientId, float duration, float multiplier)
    {
        // Static lookup helper used by server-side functional attack buff pickups.
        return StatesByClientId.TryGetValue(targetClientId, out NetworkPlayerCombatState state) &&
            state.ApplyAttackBuff(duration, multiplier);
    }

    public static bool TryApplyDamageReductionBuff(ulong targetClientId, float duration, float incomingDamageMultiplier)
    {
        // Static lookup helper used by server-side functional defense buff pickups.
        return StatesByClientId.TryGetValue(targetClientId, out NetworkPlayerCombatState state) &&
            state.ApplyDamageReductionBuff(duration, incomingDamageMultiplier);
    }

    public static bool TryApplyMoveSpeedBuff(ulong targetClientId, float duration, float multiplier)
    {
        // Static lookup helper used by server-side functional movement buff pickups.
        return StatesByClientId.TryGetValue(targetClientId, out NetworkPlayerCombatState state) &&
            state.ApplyMoveSpeedBuff(duration, multiplier);
    }

    public static bool TryApplyAutoFireBuff(ulong targetClientId, float duration)
    {
        // Static lookup helper used by server-side functional auto-fire pickups.
        return StatesByClientId.TryGetValue(targetClientId, out NetworkPlayerCombatState state) &&
            state.ApplyAutoFireBuff(duration);
    }

    public static void ApplyAutoFireBuffUntilForAll(float endTime, string sourceLabel)
    {
        // Apply a shared auto-fire end time to every currently equipped combatant.
        List<NetworkPlayerCombatState> states = new(StatesByClientId.Values);
        for (int i = 0; i < states.Count; i++)
        {
            NetworkPlayerCombatState state = states[i];
            if (state != null && state.IsServer)
            {
                state.ApplyAutoFireBuffUntil(endTime, sourceLabel);
            }
        }
    }

    public static float ApplyOutgoingDamageMultiplier(ulong clientId, float damage)
    {
        // Apply temporary attack buffs to server-authoritative outgoing damage.
        if (!StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) || state == null)
        {
            return damage;
        }

        return damage * state.ResolveAttackBuffMultiplier();
    }

    public static float ApplyLocalMoveSpeedMultiplier(float moveSpeed)
    {
        // Apply the owning client's replicated movement buff to local movement calculations.
        if (!PlayerStatsState.TryGetLocalClientId(out ulong clientId))
        {
            return moveSpeed;
        }

        return ApplyMoveSpeedMultiplier(clientId, moveSpeed);
    }

    public static float ApplyMoveSpeedMultiplier(ulong clientId, float moveSpeed)
    {
        // Apply temporary movement buffs for systems that know the target client id.
        if (!StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) || state == null)
        {
            return moveSpeed;
        }

        return moveSpeed * state.ResolveMoveSpeedBuffMultiplier();
    }

    public static bool LocalClientHasAutoFireBuff()
    {
        // Let local input code check whether the replicated auto-fire buff is active.
        if (!PlayerStatsState.TryGetLocalClientId(out ulong clientId))
        {
            return false;
        }

        return StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) &&
            state != null &&
            state.HasAutoFireBuff;
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

    public static float GetEquipmentHealthPercent(ulong clientId)
    {
        // Return the current equipment-backed health ratio for preserving swapped-out equipment state.
        if (!StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) || state == null)
        {
            return 1f;
        }

        return state.EquipmentHealthPercent;
    }

    public static void ResetClientForEquippedHealthPercent(ulong clientId, float healthPercent)
    {
        // Reset one client's combat state after equipping a drop that already has stored durability.
        if (StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) &&
            state != null &&
            state.IsServer)
        {
            state.ResetForEquipmentHealthPercent(healthPercent);
        }
    }

    public static float GetMaxHealthForClient(ulong clientId)
    {
        // Return one client's current max health so stat pickups can compare before and after values.
        if (!StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) || state == null)
        {
            return 0f;
        }

        return state.MaxHealth;
    }

    public static void AddCurrentHealthForMaxHealthGain(ulong clientId, float previousMaxHealth)
    {
        // Increase current health by the max-health delta caused by a newly collected Health stat.
        if (StatesByClientId.TryGetValue(clientId, out NetworkPlayerCombatState state) &&
            state != null &&
            state.IsServer)
        {
            state.AddCurrentHealthForMaxHealthGain(previousMaxHealth);
        }
    }

    private static bool TryGetSplashTargetPoint(NetworkPlayerCombatState state, Vector3 center, float radius, out float distance, out Vector3 hitPoint)
    {
        // Approximate a player's body as a vertical capsule so surface hits still count as splash hits.
        distance = float.MaxValue;
        hitPoint = default;
        if (state == null)
        {
            return false;
        }

        Vector3 bottom = state.transform.position;
        Vector3 top = bottom + Vector3.up * SplashTargetHeight;
        Vector3 bodySegment = top - bottom;
        float bodyLengthSquared = bodySegment.sqrMagnitude;
        float t = bodyLengthSquared > 0.0001f
            ? Mathf.Clamp01(Vector3.Dot(center - bottom, bodySegment) / bodyLengthSquared)
            : 0f;

        Vector3 centerLinePoint = bottom + bodySegment * t;
        float centerLineDistance = Vector3.Distance(center, centerLinePoint);
        distance = Mathf.Max(0f, centerLineDistance - state.ResolveSplashBodyRadius());
        hitPoint = ResolveSplashSurfacePoint(center, centerLinePoint, centerLineDistance, state.ResolveSplashBodyRadius());
        return distance <= radius;
    }

    private static Vector3 ResolveSplashSurfacePoint(Vector3 center, Vector3 centerLinePoint, float centerLineDistance, float bodyRadius)
    {
        // Place hit feedback near the body surface closest to the explosion center.
        if (centerLineDistance <= 0.0001f)
        {
            return centerLinePoint;
        }

        Vector3 towardExplosion = (center - centerLinePoint) / centerLineDistance;
        return centerLinePoint + towardExplosion * Mathf.Min(bodyRadius, centerLineDistance);
    }

    private float ResolveSplashBodyRadius()
    {
        // Prefer the controller/collider radius when present, falling back to an inspector-tunable value.
        CharacterController characterController = GetComponent<CharacterController>();
        if (characterController != null)
        {
            float largestHorizontalScale = Mathf.Max(
                Mathf.Abs(transform.lossyScale.x),
                Mathf.Abs(transform.lossyScale.z),
                0.01f);
            return Mathf.Max(0.01f, characterController.radius * largestHorizontalScale);
        }

        return Mathf.Max(0.01f, splashBodyRadius);
    }

    private static Vector3 ResolveSplashHitDirection(Vector3 center, Vector3 hitPoint)
    {
        // Convert an explosion center and target point into a stable outward hit direction.
        Vector3 direction = hitPoint - center;
        if (direction.sqrMagnitude <= 0.0001f)
        {
            return Vector3.up;
        }

        return direction.normalized;
    }

    private bool CanReceiveDamage()
    {
        // Unequipped players have no health, and invincible players ignore incoming damage.
        return equipmentState != null &&
            equipmentState.HasEquipment &&
            !isInvincible.Value &&
            currentHealth.Value > 0f;
    }

    private bool CanLoseEquipmentToHook()
    {
        // Hook stealing is allowed only while the same low-health condition that shows sparks is active.
        if (equipmentState == null ||
            !equipmentState.HasEquipment ||
            isInvincible.Value ||
            isActionDisabled.Value ||
            currentHealth.Value <= 0f)
        {
            return false;
        }

        float maxHealth = ResolveMaxHealthForEquipment();
        return maxHealth > 0f && currentHealth.Value / maxHealth <= Mathf.Clamp01(lowHealthSparkThreshold);
    }

    private void RefreshHealthForEquipmentState()
    {
        // Give health when equipment exists, otherwise clear health because unequipped players are not damageable.
        if (equipmentState != null && equipmentState.HasEquipment)
        {
            currentHealth.Value = ResolveMaxHealthForEquipment();
            return;
        }

        currentHealth.Value = 0f;
    }

    private void ApplyEquipmentHealthPercent(float healthPercent)
    {
        // Convert a stored equipment durability ratio into this player's current health value.
        if (equipmentState != null && equipmentState.HasEquipment)
        {
            currentHealth.Value = Mathf.Max(1f, ResolveMaxHealthForEquipment() * Mathf.Clamp01(healthPercent));
            return;
        }

        currentHealth.Value = 0f;
    }

    private float ResolveEquipmentHealthPercent()
    {
        // Calculate the current equipment durability ratio from networked health.
        if (equipmentState == null || !equipmentState.HasEquipment)
        {
            return 0f;
        }

        return Mathf.Clamp01(currentHealth.Value / ResolveMaxHealthForEquipment());
    }

    private void AddCurrentHealthForMaxHealthGain(float previousMaxHealth)
    {
        // Treat a max-health stat gain as a matching current-health increase.
        if (!IsServer || equipmentState == null || !equipmentState.HasEquipment || currentHealth.Value <= 0f)
        {
            return;
        }

        float newMaxHealth = ResolveMaxHealthForEquipment();
        float healthGain = Mathf.Max(0f, newMaxHealth - Mathf.Max(0f, previousMaxHealth));
        if (healthGain <= 0f)
        {
            return;
        }

        float previousHealth = currentHealth.Value;
        currentHealth.Value = Mathf.Min(newMaxHealth, currentHealth.Value + healthGain);
        Debug.Log($"[NetworkPlayerCombatState] Max health gain target={OwnerClientId} gain={healthGain:0.0} health={previousHealth:0.0}->{currentHealth.Value:0.0} max={newMaxHealth:0.0}");
    }

    private float ResolveMaxHealthForEquipment()
    {
        // Apply equipment health first, then collected Health stack bonuses.
        EquipmentDefinition equipment = equipmentState != null ? equipmentState.CurrentEquipment : null;
        float equipmentModifiedHealth = equipment != null
            ? equipment.ModifyStat(PlayerStatType.Health, defaultMaxHealth)
            : defaultMaxHealth;

        float collectedModifiedHealth = PlayerStatsState.ApplyCollectedStatBonus(OwnerClientId, PlayerStatType.Health, equipmentModifiedHealth);
        return Mathf.Max(1f, collectedModifiedHealth);
    }

    private float ResolveDefenseForEquipment()
    {
        // Apply equipment defense first, then collected Defense stack bonuses.
        EquipmentDefinition equipment = equipmentState != null ? equipmentState.CurrentEquipment : null;
        float equipmentModifiedDefense = equipment != null
            ? equipment.ModifyStat(PlayerStatType.Defense, defaultDefense)
            : defaultDefense;

        return Mathf.Max(0f, PlayerStatsState.ApplyCollectedStatBonus(OwnerClientId, PlayerStatType.Defense, equipmentModifiedDefense));
    }

    private float ResolveDamageAfterDefense(float rawDamage, out float defense)
    {
        // Reduce incoming damage with defense first, then temporary damage-reduction buffs.
        defense = ResolveDefenseForEquipment();
        float damageScale = 100f / (100f + defense);
        return Mathf.Max(0.1f, rawDamage * damageScale * ResolveDamageTakenMultiplier());
    }

    private float ResolveAttackBuffMultiplier()
    {
        // Return the active outgoing damage multiplier and ignore expired pickup buffs.
        if (Time.time > attackBuffExpireTime)
        {
            return 1f;
        }

        return Mathf.Max(0.01f, attackBuffMultiplier > 0f ? attackBuffMultiplier : defaultAttackBuffMultiplier);
    }

    private float ResolveDamageTakenMultiplier()
    {
        // Return the active incoming damage multiplier and ignore expired pickup buffs.
        if (Time.time > damageReductionExpireTime)
        {
            return 1f;
        }

        return Mathf.Clamp(damageTakenMultiplier > 0f ? damageTakenMultiplier : defaultDamageTakenMultiplier, 0.01f, 1f);
    }

    private float ResolveMoveSpeedBuffMultiplier()
    {
        // Return the active movement speed multiplier and ignore expired pickup buffs.
        if (!moveSpeedBuffActive.Value || (IsServer && Time.time > moveSpeedBuffExpireTime))
        {
            return 1f;
        }

        float resolvedMultiplier = IsServer ? moveSpeedBuffMultiplier : replicatedMoveSpeedBuffMultiplier.Value;
        if (resolvedMultiplier <= 0f)
        {
            resolvedMultiplier = defaultMoveSpeedBuffMultiplier;
        }

        return Mathf.Max(0.01f, resolvedMultiplier);
    }

    private void RefreshExpiredFunctionalBuffs()
    {
        // Server clears replicated buff-active flags when their authoritative duration expires.
        if (!IsServer)
        {
            return;
        }

        if (attackBuffActive.Value && Time.time > attackBuffExpireTime)
        {
            attackBuffActive.Value = false;
            attackBuffMultiplier = 1f;
        }

        if (damageReductionBuffActive.Value && Time.time > damageReductionExpireTime)
        {
            damageReductionBuffActive.Value = false;
            damageTakenMultiplier = 1f;
        }

        if (moveSpeedBuffActive.Value && Time.time > moveSpeedBuffExpireTime)
        {
            moveSpeedBuffActive.Value = false;
            moveSpeedBuffMultiplier = 1f;
            replicatedMoveSpeedBuffMultiplier.Value = 1f;
        }

        if (autoFireBuffActive.Value && Time.time > autoFireBuffExpireTime)
        {
            autoFireBuffActive.Value = false;
        }
    }

    private void ClearFunctionalBuffs()
    {
        // Clear temporary functional pickup buffs when combat is fully reset.
        attackBuffExpireTime = 0f;
        attackBuffMultiplier = 1f;
        attackBuffActive.Value = false;
        damageReductionExpireTime = 0f;
        damageTakenMultiplier = 1f;
        damageReductionBuffActive.Value = false;
        moveSpeedBuffExpireTime = 0f;
        moveSpeedBuffMultiplier = 1f;
        replicatedMoveSpeedBuffMultiplier.Value = 1f;
        moveSpeedBuffActive.Value = false;
        autoFireBuffExpireTime = 0f;
        autoFireBuffActive.Value = false;
    }

    private void BreakEquipmentAndDisableActions(ulong attackerClientId)
    {
        // On health depletion without hit data, use the fallback effect point and direction.
        BreakEquipmentAndDisableActions(attackerClientId, ResolveHitEffectPoint(), ResolveFallbackHitDirection());
    }

    private void BreakEquipmentAndDisableActions(ulong attackerClientId, Vector3 breakPoint, Vector3 hitDirection)
    {
        // On health depletion, play the break explosion, remove equipment, and lock actions briefly.
        string brokenEquipmentId = equipmentState != null && equipmentState.CurrentEquipment != null
            ? equipmentState.CurrentEquipment.EquipmentId
            : string.Empty;
        Debug.Log($"[NetworkPlayerCombatState] Equipment broken target={OwnerClientId} attacker={attackerClientId}");
        SendEquipmentBreakNotices(attackerClientId);
        PlayEquipmentBreakExplosionClientRpc(
            ResolveEffectPoint(breakPoint),
            ResolveEffectDirection(hitDirection),
            new Unity.Collections.FixedString64Bytes(brokenEquipmentId));
        currentHealth.Value = 0f;
        ClearFunctionalBuffs();
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

    private void SendEquipmentBreakNotices(ulong attackerClientId)
    {
        // Notify the attacker and victim when one player breaks another player's equipment.
        if (!IsServer || attackerClientId == OwnerClientId)
        {
            return;
        }

        MatchStateController controller = MatchStateController.Instance;
        if (controller == null || !controller.IsSpawned)
        {
            return;
        }

        controller.ShowNoticeToClient(attackerClientId, $"플레이어 {FormatClientId(OwnerClientId)}를 파괴하였습니다!", 4f);
        controller.ShowNoticeToClient(OwnerClientId, $"플레이어 {FormatClientId(attackerClientId)}에게 장비가 파괴당하였습니다..", 4f);
    }

    private static string FormatClientId(ulong clientId)
    {
        // Format a client id for temporary player-facing notices until player names exist.
        return clientId.ToString();
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

    private void OnHealthChanged(float previousHealth, float currentHealthValue)
    {
        // React immediately when replicated health crosses the low-health visual threshold.
        if (currentHealthValue < previousHealth)
        {
            TriggerDamagedAnimation();
        }

        UpdateLowHealthSparkEffect();
    }

    private void OnAttackBuffActiveChanged(bool previousValue, bool currentValue)
    {
        // React immediately when the replicated attack buff starts or expires.
        UpdateBuffEffects();
    }

    private void OnDamageReductionBuffActiveChanged(bool previousValue, bool currentValue)
    {
        // React immediately when the replicated defense buff starts or expires.
        UpdateBuffEffects();
    }

    private void OnMoveSpeedBuffActiveChanged(bool previousValue, bool currentValue)
    {
        // React immediately when the replicated movement buff starts or expires.
        UpdateBuffEffects();
    }

    private void TriggerDamagedAnimation()
    {
        // Play damaged animation on this network avatar and on the local owner controller when applicable.
        PlayableCharacterAnimationDriver networkDriver = GetComponent<PlayableCharacterAnimationDriver>();
        if (networkDriver != null)
        {
            networkDriver.TriggerDamaged();
        }

        if (!IsOwner)
        {
            return;
        }

        ThirdPersonController localController = GetComponent<ThirdPersonController>();
        if (localController == null)
        {
            localController = FindFirstObjectByType<ThirdPersonController>();
        }

        if (localController != null && localController.TryGetComponent(out PlayableCharacterAnimationDriver localDriver))
        {
            if (localDriver != networkDriver)
            {
                localDriver.TriggerDamaged();
            }
        }
    }

    private void PlayOwnerDamageFeedback(bool equipmentDestroyed, string equipmentId)
    {
        // Target damage audio at the victim only and prefer the break cue on a lethal equipment hit.
        if (!IsServer || NetworkManager.Singleton == null ||
            !NetworkManager.Singleton.ConnectedClients.ContainsKey(OwnerClientId))
        {
            return;
        }

        ClientRpcParams rpcParams = new()
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        };
        PlayOwnerDamageFeedbackClientRpc(
            equipmentDestroyed,
            new Unity.Collections.FixedString64Bytes(equipmentId ?? string.Empty),
            rpcParams);
    }

    [ClientRpc]
    private void PlayOwnerDamageFeedbackClientRpc(
        bool equipmentDestroyed,
        Unity.Collections.FixedString64Bytes equipmentId,
        ClientRpcParams rpcParams = default)
    {
        // Play one local feedback cue without exposing the victim sound to other clients.
        SoundManager soundManager = SoundManager.Instance;
        if (soundManager == null)
        {
            return;
        }

        if (equipmentDestroyed)
        {
            EquipmentDefinition equipment = EquipmentCatalog.Get(equipmentId.ToString());
            soundManager.PlayLocalEquipmentBreakSfx(equipment != null ? equipment.BreakSfxClip : null);
            return;
        }

        soundManager.PlayLocalPlayerHitSfx();
    }

    [ClientRpc]
    private void PlayDamageHitEffectClientRpc(Vector3 hitPoint, Vector3 hitDirection)
    {
        // Spawn the green hit VFX on every client using the server-approved impact point and direction.
        PlayOneShotEffect(
            ResolveDamageHitEffectPrefab(),
            ResolveEffectPoint(hitPoint),
            ResolveEffectDirection(hitDirection),
            damageHitEffectEulerOffset,
            damageHitEffectScale,
            damageHitEffectLifetime,
            "DamageGreenHitEffect");
    }

    [ClientRpc]
    private void PlayEquipmentBreakExplosionClientRpc(
        Vector3 breakPoint,
        Vector3 hitDirection,
        Unity.Collections.FixedString64Bytes equipmentId)
    {
        // Spawn break VFX for everyone and play positional audio for clients other than the owner.
        PlayOneShotEffect(
            ResolveBreakExplosionEffectPrefab(),
            ResolveEffectPoint(breakPoint),
            ResolveEffectDirection(hitDirection),
            breakExplosionEulerOffset,
            breakExplosionScale,
            breakExplosionLifetime,
            "EquipmentBreakExplosionEffect");

        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsClient &&
            NetworkManager.Singleton.LocalClientId == OwnerClientId)
        {
            return;
        }

        EquipmentDefinition equipment = EquipmentCatalog.Get(equipmentId.ToString());
        SoundManager.Instance?.PlayWorldEquipmentBreakSfx(
            breakPoint,
            equipment != null ? equipment.BreakSfxClip : null);
    }

    private void PlayOneShotEffect(GameObject effectPrefab, Vector3 position, Vector3 direction, Vector3 eulerOffset, float scale, float lifetime, string effectName)
    {
        // Instantiate a temporary effect prefab, force it to play once, and clean it up after a short lifetime.
        if (effectPrefab == null)
        {
            return;
        }

        Quaternion rotation = Quaternion.LookRotation(ResolveEffectDirection(direction), Vector3.up) * Quaternion.Euler(eulerOffset);
        GameObject effectObject = Instantiate(effectPrefab, position, rotation);
        effectObject.name = effectName;
        effectObject.transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);

        ParticleSystem[] particleSystems = effectObject.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
        for (int i = 0; i < particleSystems.Length; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null)
            {
                continue;
            }

            particleSystem.gameObject.SetActive(true);
            ParticleSystem.MainModule main = particleSystem.main;
            main.loop = false;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }

        Destroy(effectObject, Mathf.Max(0.1f, lifetime));
    }

    private GameObject ResolveDamageHitEffectPrefab()
    {
        // Use the assigned green-hit prefab first, then fall back to the shared Resources VFX asset.
        if (damageHitEffectPrefab != null)
        {
            return damageHitEffectPrefab;
        }

        if (!triedLoadDefaultDamageHitEffectPrefab)
        {
            triedLoadDefaultDamageHitEffectPrefab = true;
            resolvedDefaultDamageHitEffectPrefab = Resources.Load<GameObject>(DefaultDamageHitEffectResourcePath);
        }

        return resolvedDefaultDamageHitEffectPrefab;
    }

    private GameObject ResolveBreakExplosionEffectPrefab()
    {
        // Use the assigned explosion prefab first, then fall back to the shared Resources VFX asset.
        if (equipmentBreakExplosionPrefab != null)
        {
            return equipmentBreakExplosionPrefab;
        }

        if (!triedLoadDefaultBreakExplosionEffectPrefab)
        {
            triedLoadDefaultBreakExplosionEffectPrefab = true;
            resolvedDefaultBreakExplosionEffectPrefab = Resources.Load<GameObject>(DefaultBreakExplosionEffectResourcePath);
        }

        return resolvedDefaultBreakExplosionEffectPrefab;
    }

    private Vector3 ResolveEffectPoint(Vector3 requestedPoint)
    {
        // Use a provided world impact point when valid, otherwise fall back to the character hit anchor.
        if (IsFinite(requestedPoint))
        {
            return requestedPoint;
        }

        return ResolveHitEffectPoint();
    }

    private Vector3 ResolveHitEffectPoint()
    {
        // Prefer the editable hit-effect anchor and otherwise place effects near the player's upper body.
        Transform hitEffectAnchor = ResolveNamedChildTransform(HitEffectAnchorName);
        return hitEffectAnchor != null ? hitEffectAnchor.position : transform.position + Vector3.up;
    }

    private Vector3 ResolveEffectDirection(Vector3 requestedDirection)
    {
        // Normalize incoming effect direction and fall back to this player's forward direction when needed.
        if (IsFinite(requestedDirection) && requestedDirection.sqrMagnitude > 0.0001f)
        {
            return requestedDirection.normalized;
        }

        return ResolveFallbackHitDirection();
    }

    private Vector3 ResolveFallbackHitDirection()
    {
        // Provide a stable non-zero direction for effects that were triggered without projectile hit data.
        Vector3 fallbackDirection = transform.forward;
        return fallbackDirection.sqrMagnitude > 0.0001f ? fallbackDirection.normalized : Vector3.forward;
    }

    private void UpdateLowHealthSparkEffect()
    {
        // Show or hide the local red spark effect based on the replicated equipment health ratio.
        if (!IsClient)
        {
            return;
        }

        bool shouldShowSpark = ShouldShowLowHealthSpark();
        if (shouldShowSpark)
        {
            EnsureLowHealthSparkEffect();
            if (lowHealthSparkEffect != null && !lowHealthSparkEffect.isPlaying)
            {
                lowHealthSparkEffect.Play();
            }

            return;
        }

        if (lowHealthSparkEffect != null && lowHealthSparkEffect.isPlaying)
        {
            lowHealthSparkEffect.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    private void UpdateBuffEffects()
    {
        // Keep functional buff VFX alive only while their replicated buff flags remain active.
        if (!IsClient)
        {
            return;
        }

        UpdateAttackBuffEffect();
        UpdateDamageReductionBuffEffect();
        UpdateMoveSpeedBuffEffect();
    }

    private void UpdateAttackBuffEffect()
    {
        // Create or remove the attack-up effect based on the active attack buff flag.
        if (ShouldShowAttackBuffEffect())
        {
            attackUpEffectObject = EnsurePersistentBuffEffect(
                attackUpEffectObject,
                ResolveAttackUpEffectPrefab(),
                "AttackUpPersistentEffect");
            return;
        }

        DestroyPersistentEffect(ref attackUpEffectObject);
    }

    private void UpdateDamageReductionBuffEffect()
    {
        // Create or remove the defense-up effect based on the active damage reduction buff flag.
        if (ShouldShowDamageReductionBuffEffect())
        {
            defenceUpEffectObject = EnsurePersistentBuffEffect(
                defenceUpEffectObject,
                ResolveDefenceUpEffectPrefab(),
                "DefenceUpPersistentEffect");
            return;
        }

        DestroyPersistentEffect(ref defenceUpEffectObject);
    }

    private void UpdateMoveSpeedBuffEffect()
    {
        // Create or remove the speed-up effect based on the active movement buff flag.
        if (ShouldShowMoveSpeedBuffEffect())
        {
            speedUpEffectObject = EnsurePersistentBuffEffect(
                speedUpEffectObject,
                ResolveSpeedUpEffectPrefab(),
                "SpeedUpPersistentEffect");
            return;
        }

        DestroyPersistentEffect(ref speedUpEffectObject);
    }

    private bool ShouldShowAttackBuffEffect()
    {
        // Attack-up VFX is visible only while the buff is active on an equipped player.
        return attackBuffActive.Value &&
            equipmentState != null &&
            equipmentState.HasEquipment;
    }

    private bool ShouldShowDamageReductionBuffEffect()
    {
        // Defense-up VFX is visible only while the buff is active on an equipped player.
        return damageReductionBuffActive.Value &&
            equipmentState != null &&
            equipmentState.HasEquipment;
    }

    private bool ShouldShowMoveSpeedBuffEffect()
    {
        // Speed-up VFX is visible only while the buff is active on an equipped player.
        return moveSpeedBuffActive.Value &&
            equipmentState != null &&
            equipmentState.HasEquipment;
    }

    private GameObject EnsurePersistentBuffEffect(GameObject effectObject, GameObject effectPrefab, string effectName)
    {
        // Instantiate a looping buff VFX under the player so it follows the character while active.
        if (effectObject != null || effectPrefab == null)
        {
            return effectObject;
        }

        Transform effectParent = ResolveBuffEffectParent();
        GameObject createdEffect = Instantiate(effectPrefab, effectParent);
        createdEffect.name = effectName;
        ApplyPersistentBuffEffectTransform(createdEffect.transform, effectParent);
        ConfigurePersistentLoopingEffect(createdEffect);
        return createdEffect;
    }

    private void ApplyPersistentBuffEffectTransform(Transform effectTransform, Transform effectParent)
    {
        // Place attack/defense buff effects lower on the player by default while still allowing anchor overrides.
        if (effectTransform == null)
        {
            return;
        }

        effectTransform.localPosition = effectParent == transform ? buffEffectLocalOffset : Vector3.zero;
        effectTransform.localRotation = Quaternion.Euler(buffEffectLocalEulerAngles);
        effectTransform.localScale = Vector3.one * Mathf.Max(0.01f, buffEffectScale);
    }

    private Transform ResolveBuffEffectParent()
    {
        // Prefer an explicit buff anchor, then the prefab BuffEffectPoint, then this network player root.
        if (buffEffectAnchor != null)
        {
            return buffEffectAnchor;
        }

        Transform buffAnchor = ResolveNamedChildTransform(BuffEffectAnchorName);
        return buffAnchor != null ? buffAnchor : transform;
    }

    private void ConfigurePersistentLoopingEffect(GameObject effectObject)
    {
        // Force particle children to loop so pickup effects can serve as persistent buff VFX.
        if (effectObject == null)
        {
            return;
        }

        effectObject.SetActive(true);
        ParticleSystem[] particleSystems = effectObject.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
        for (int i = 0; i < particleSystems.Length; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null)
            {
                continue;
            }

            particleSystem.gameObject.SetActive(true);
            ParticleSystem.MainModule main = particleSystem.main;
            main.loop = true;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }
    }

    private bool ShouldShowLowHealthSpark()
    {
        // Low-health warning only applies while the player has damageable equipment-backed health.
        if (equipmentState == null || !equipmentState.HasEquipment || currentHealth.Value <= 0f)
        {
            return false;
        }

        float maxHealth = ResolveMaxHealthForEquipment();
        return maxHealth > 0f && currentHealth.Value / maxHealth < Mathf.Clamp01(lowHealthSparkThreshold);
    }

    private void EnsureLowHealthSparkEffect()
    {
        // Create the configured low-health VFX under the character-specific spark anchor.
        if (lowHealthSparkEffect != null)
        {
            return;
        }

        Transform sparkParent = ResolveLowHealthSparkParent();
        ParticleSystem sparkPrefab = ResolveLowHealthSparkPrefab();
        if (sparkPrefab != null)
        {
            lowHealthSparkEffect = Instantiate(sparkPrefab, sparkParent);
            lowHealthSparkEffect.name = "LowHealthRedSparkEffect";
            ApplyLowHealthSparkTransform(lowHealthSparkEffect.transform, sparkParent);
            return;
        }

        GameObject sparkObject = new("LowHealthRedSparkEffect");
        sparkObject.transform.SetParent(sparkParent, false);
        ApplyLowHealthSparkTransform(sparkObject.transform, sparkParent);

        lowHealthSparkEffect = sparkObject.AddComponent<ParticleSystem>();
        ConfigureLowHealthSparkEffect(lowHealthSparkEffect);
    }

    private void ApplyLowHealthSparkTransform(Transform sparkTransform, Transform sparkParent)
    {
        // Apply character-specific VFX placement, direction, and size without editing the source effect prefab.
        if (sparkTransform == null)
        {
            return;
        }

        sparkTransform.localPosition = sparkParent == transform ? lowHealthSparkLocalOffset : Vector3.zero;
        sparkTransform.localRotation = Quaternion.Euler(lowHealthSparkLocalEulerAngles);
        sparkTransform.localScale = Vector3.one * Mathf.Max(0.01f, lowHealthSparkScale);
    }

    private Transform ResolveLowHealthSparkParent()
    {
        // Prefer an explicit prefab anchor so character-specific effects can be tuned in the editor.
        if (lowHealthSparkAnchor != null)
        {
            return lowHealthSparkAnchor;
        }

        Transform discoveredAnchor = ResolveLowHealthSparkAnchorByName();
        return discoveredAnchor != null ? discoveredAnchor : transform;
    }

    private Transform ResolveLowHealthSparkAnchorByName()
    {
        // Find the convention-based spark anchor on editable character prefabs when no reference is assigned.
        return ResolveNamedChildTransform(LowHealthSparkAnchorName);
    }

    private Transform ResolveNamedChildTransform(string childName)
    {
        // Find a named child transform in active or inactive character prefab hierarchies.
        Transform[] childTransforms = GetComponentsInChildren<Transform>(includeInactive: true);
        for (int i = 0; i < childTransforms.Length; i++)
        {
            if (childTransforms[i] != null && childTransforms[i].name == childName)
            {
                return childTransforms[i];
            }
        }

        return null;
    }

    private static bool IsFinite(Vector3 value)
    {
        // Check world-space VFX inputs before using them for effect placement or rotation.
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        // Reject NaN and infinity values from network-provided effect data.
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private ParticleSystem ResolveLowHealthSparkPrefab()
    {
        // Use the assigned VFX prefab first, then fall back to the shared Resources spark asset.
        if (lowHealthSparkPrefab != null)
        {
            return lowHealthSparkPrefab;
        }

        if (!triedLoadDefaultLowHealthSparkPrefab)
        {
            triedLoadDefaultLowHealthSparkPrefab = true;
            GameObject sparkPrefabObject = Resources.Load<GameObject>(DefaultLowHealthSparkResourcePath);
            resolvedDefaultLowHealthSparkPrefab = sparkPrefabObject != null
                ? sparkPrefabObject.GetComponentInChildren<ParticleSystem>(true)
                : null;
        }

        return resolvedDefaultLowHealthSparkPrefab;
    }

    private GameObject ResolveAttackUpEffectPrefab()
    {
        // Use the assigned persistent attack-up VFX first, then fall back to CustomEffects/AttackUp.
        if (attackUpEffectPrefab != null)
        {
            return attackUpEffectPrefab;
        }

        if (!triedLoadDefaultAttackUpEffectPrefab)
        {
            triedLoadDefaultAttackUpEffectPrefab = true;
            resolvedDefaultAttackUpEffectPrefab = Resources.Load<GameObject>(DefaultAttackUpEffectResourcePath);
        }

        return resolvedDefaultAttackUpEffectPrefab;
    }

    private GameObject ResolveDefenceUpEffectPrefab()
    {
        // Use the assigned persistent defense-up VFX first, then fall back to CustomEffects/DefenceUp.
        if (defenceUpEffectPrefab != null)
        {
            return defenceUpEffectPrefab;
        }

        if (!triedLoadDefaultDefenceUpEffectPrefab)
        {
            triedLoadDefaultDefenceUpEffectPrefab = true;
            resolvedDefaultDefenceUpEffectPrefab = Resources.Load<GameObject>(DefaultDefenceUpEffectResourcePath);
        }

        return resolvedDefaultDefenceUpEffectPrefab;
    }

    private GameObject ResolveSpeedUpEffectPrefab()
    {
        // Use the assigned persistent speed-up VFX first, then fall back to CustomEffects/SpeedUp.
        if (speedUpEffectPrefab != null)
        {
            return speedUpEffectPrefab;
        }

        if (!triedLoadDefaultSpeedUpEffectPrefab)
        {
            triedLoadDefaultSpeedUpEffectPrefab = true;
            resolvedDefaultSpeedUpEffectPrefab = Resources.Load<GameObject>(DefaultSpeedUpEffectResourcePath);
        }

        return resolvedDefaultSpeedUpEffectPrefab;
    }

    private void ConfigureLowHealthSparkEffect(ParticleSystem sparkEffect)
    {
        // Configure small red particles that pop around the damaged player at a steady warning rate.
        ParticleSystem.MainModule main = sparkEffect.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 3f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.08f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.02f, 0f, 1f), new Color(1f, 0.35f, 0.1f, 1f));

        ParticleSystem.EmissionModule emission = sparkEffect.emission;
        emission.rateOverTime = Mathf.Max(0f, lowHealthSparkRate);

        ParticleSystem.ShapeModule shape = sparkEffect.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.55f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = sparkEffect.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient fadeGradient = new();
        fadeGradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.02f, 0f), 0f),
                new GradientColorKey(new Color(1f, 0.35f, 0.1f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(fadeGradient);

        ParticleSystemRenderer renderer = sparkEffect.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        Shader particleShader = Shader.Find("Sprites/Default");
        if (particleShader != null)
        {
            renderer.material = new Material(particleShader);
        }
    }

    private void DestroyLowHealthSparkEffect()
    {
        // Clean up the generated warning effect when the network player despawns.
        if (lowHealthSparkEffect == null)
        {
            return;
        }

        Destroy(lowHealthSparkEffect.gameObject);
        lowHealthSparkEffect = null;
    }

    private void DestroyBuffEffects()
    {
        // Remove persistent buff effects during despawn or full combat cleanup.
        DestroyPersistentEffect(ref attackUpEffectObject);
        DestroyPersistentEffect(ref defenceUpEffectObject);
        DestroyPersistentEffect(ref speedUpEffectObject);
    }

    private void DestroyPersistentEffect(ref GameObject effectObject)
    {
        // Destroy a tracked persistent effect object and clear its reference.
        if (effectObject == null)
        {
            return;
        }

        Destroy(effectObject);
        effectObject = null;
    }

    private void TryBindLocalPlayerEquipment()
    {
        // Owners mirror combat state locally so movement, collection, and attack can respect action lock.
        if (!IsOwner)
        {
            return;
        }

        PlayerEquipment playerEquipment = GetComponent<PlayerEquipment>();
        if (playerEquipment == null)
        {
            playerEquipment = FindFirstObjectByType<PlayerEquipment>();
        }

        if (playerEquipment != null)
        {
            playerEquipment.BindCombatState(this);
        }
    }
}
