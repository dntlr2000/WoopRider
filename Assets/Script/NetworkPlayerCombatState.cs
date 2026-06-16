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
    private const string DefaultDamageHitEffectResourcePath = "Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Green hit";
    private const string DefaultBreakExplosionEffectResourcePath = "Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Explosion";
    private const string LowHealthSparkAnchorName = "EffectPoint_Spark";
    private const string HitEffectAnchorName = "HitEffectPoint";

    [Header("Health")]
    [SerializeField] private float defaultMaxHealth = 100f;

    [Header("Defense")]
    [SerializeField] private float defaultDefense = 10f;

    [Header("Break State")]
    [SerializeField] private float equipmentBreakActionLockDuration = 3f;

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
    private ParticleSystem lowHealthSparkEffect;
    private ParticleSystem resolvedDefaultLowHealthSparkPrefab;
    private GameObject resolvedDefaultDamageHitEffectPrefab;
    private GameObject resolvedDefaultBreakExplosionEffectPrefab;
    private bool triedLoadDefaultLowHealthSparkPrefab;
    private bool triedLoadDefaultDamageHitEffectPrefab;
    private bool triedLoadDefaultBreakExplosionEffectPrefab;
    private bool hadEquipmentLastFrame;

    public float CurrentHealth => currentHealth.Value;
    public float MaxHealth => ResolveMaxHealthForEquipment();
    public bool HasHealth => equipmentState != null && equipmentState.HasEquipment;
    public float EquipmentHealthPercent => ResolveEquipmentHealthPercent();
    public bool IsInvincible => isInvincible.Value;
    public bool IsActionDisabled => isActionDisabled.Value;
    public bool CanBeHookStolen => CanLoseEquipmentToHook();

    public override void OnNetworkSpawn()
    {
        // Register this combat state and initialize health when equipment exists.
        equipmentState = GetComponent<NetworkPlayerEquipmentState>();
        StatesByClientId[OwnerClientId] = this;
        hadEquipmentLastFrame = equipmentState != null && equipmentState.HasEquipment;
        currentHealth.OnValueChanged += OnHealthChanged;

        if (IsServer)
        {
            RefreshHealthForEquipmentState();
        }

        TryBindLocalPlayerEquipment();
        UpdateLowHealthSparkEffect();
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
        DestroyLowHealthSparkEffect();
    }

    private void Update()
    {
        UpdateLowHealthSparkEffect();

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
        Debug.Log($"[NetworkPlayerCombatState] Damage target={OwnerClientId} attacker={attackerClientId} raw={amount:0.0} defense={defense:0.0} amount={resolvedDamage:0.0} health={currentHealth.Value:0.0}");
        PlayDamageHitEffectClientRpc(ResolveEffectPoint(hitPoint), ResolveEffectDirection(hitDirection));

        if (currentHealth.Value <= 0f)
        {
            BreakEquipmentAndDisableActions(attackerClientId, hitPoint, hitDirection);
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
        // Reduce incoming damage with a soft defense curve that never reaches full immunity.
        defense = ResolveDefenseForEquipment();
        float damageScale = 100f / (100f + defense);
        return Mathf.Max(0.1f, rawDamage * damageScale);
    }

    private void BreakEquipmentAndDisableActions(ulong attackerClientId)
    {
        // On health depletion without hit data, use the fallback effect point and direction.
        BreakEquipmentAndDisableActions(attackerClientId, ResolveHitEffectPoint(), ResolveFallbackHitDirection());
    }

    private void BreakEquipmentAndDisableActions(ulong attackerClientId, Vector3 breakPoint, Vector3 hitDirection)
    {
        // On health depletion, play the break explosion, remove equipment, and lock actions briefly.
        Debug.Log($"[NetworkPlayerCombatState] Equipment broken target={OwnerClientId} attacker={attackerClientId}");
        SendEquipmentBreakNotices(attackerClientId);
        PlayEquipmentBreakExplosionClientRpc(ResolveEffectPoint(breakPoint), ResolveEffectDirection(hitDirection));
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
    private void PlayEquipmentBreakExplosionClientRpc(Vector3 breakPoint, Vector3 hitDirection)
    {
        // Spawn the equipment-break explosion VFX on every client before the visual state changes.
        PlayOneShotEffect(
            ResolveBreakExplosionEffectPrefab(),
            ResolveEffectPoint(breakPoint),
            ResolveEffectDirection(hitDirection),
            breakExplosionEulerOffset,
            breakExplosionScale,
            breakExplosionLifetime,
            "EquipmentBreakExplosionEffect");
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
