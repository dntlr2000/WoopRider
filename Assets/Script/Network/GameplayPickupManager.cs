using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class GameplayPickupManager : NetworkBehaviour
{
    public static GameplayPickupManager Instance { get; private set; }

    public enum PickupKind : byte
    {
        Stat = 0,
        FinalObjective = 1,
        Equipment = 2,
        Functional = 3
    }

    public enum FunctionalPickupType : byte
    {
        None = 0,
        BasicHeal = 1
    }

    private class PickupSlot
    {
        public bool Active;
        public bool Hooked;
        public bool RespawnOnCollect;
        public PickupKind Kind;
        public PlayerStatType StatType;
        public FunctionalPickupType FunctionalType;
        public string EquipmentId;
        public float EquipmentHealthPercent;
        public float EquipmentCurrentHealth;
        public float EquipmentMaxHealth;
        public Vector3 Position;
        public GameObject Visual;
        public PickupKind VisualKind;
        public string VisualEquipmentId;
        public Coroutine RespawnRoutine;
        public Coroutine HookRoutine;
        public Coroutine DespawnRoutine;
        public Coroutine PhysicsRoutine;
        public Coroutine BlinkRoutine;
        public ParticleSystem EquipmentLowHealthSparkEffect;
        public Vector3 PhysicsVelocity;
        public bool Blinking;
    }

    private class BoxSlot
    {
        public bool Active;
        public string BoxId;
        public float CurrentHealth;
        public float MaxHealth;
        public PlayerStatType[] LootStats;
        public Vector3 Position;
        public GameObject Visual;
        public Coroutine RespawnRoutine;
        public Coroutine DespawnRoutine;
        public Coroutine BlinkRoutine;
        public bool Blinking;
    }

    [Header("Main Match Pickups")]
    [SerializeField] private int statPickupCount = 12;
    [SerializeField] private float statRespawnDelay = 5f;
    [SerializeField] private int equipmentPickupCount = 3;
    [SerializeField] private float equipmentRespawnDelay = 12f;
    [SerializeField] private int equipmentSlotIdBase = 2000;
    [SerializeField] private int lootPickupSlotIdBase = 5000;

    [Header("Functional Pickups")]
    [Range(0f, 1f)]
    [SerializeField] private float functionalPickupChance = 0.25f;
    [SerializeField] private float basicHealPercent = 0.2f;

    [Header("Box Items")]
    [SerializeField] private int boxItemCount = 3;
    [SerializeField] private int boxSlotIdBase = 4000;
    [SerializeField] private float boxRespawnDelay = 15f;
    [SerializeField] private float boxHitRadius = 1.2f;
    [SerializeField] private float boxTargetHeight = 1.2f;
    [SerializeField] private float boxLootScatterRadius = 1.4f;
    [SerializeField] private string basicBoxVisualResourcePath = "fbx/Bangae_Statue";
    [SerializeField] private Vector3 basicBoxVisualScale = Vector3.one;
    [SerializeField] private float basicBoxMaxHealth = 100f;
    [SerializeField] private int basicBoxLootCount = 3;

    [Header("Final Match Objective")]
    [SerializeField] private int finalObjectiveSlotIndex = 1000;

    [Header("Spawn Area")]
    [SerializeField] private Vector2 xRange = new(-18f, 18f);
    [SerializeField] private Vector2 zRange = new(-18f, 18f);
    [SerializeField] private float spawnY = 0.75f;

    [Header("Collection")]
    [SerializeField] private float collectRadius = 1.4f;
    [SerializeField] private float scanInterval = 0.1f;

    [Header("Timed Despawn")]
    [SerializeField] private Vector2 despawnLifetimeRange = new(15f, 20f);
    [SerializeField] private float despawnBlinkLeadTime = 2f;
    [SerializeField] private float despawnBlinkInterval = 0.15f;

    [Header("Equipment Damage")]
    [SerializeField] private float equipmentDropBaseHealth = 100f;
    [SerializeField] private float equipmentHitRadius = 0.85f;
    [SerializeField] private float equipmentTargetHeight = 0.35f;

    [Header("Equipment Low Health Effect")]
    [SerializeField] private ParticleSystem equipmentLowHealthSparkPrefab;
    [Range(0f, 1f)]
    [SerializeField] private float equipmentLowHealthSparkThreshold = 0.2f;
    [SerializeField] private Vector3 equipmentLowHealthSparkLocalOffset = new(0f, 0.35f, 0f);
    [SerializeField] private float equipmentLowHealthSparkRate = 16f;

    [Header("Pickup Physics")]
    [SerializeField] private bool enablePickupGravity = true;
    [SerializeField] private LayerMask pickupGroundMask = ~0;
    [SerializeField] private float pickupGravity = 18f;
    [SerializeField] private float pickupRestHeight = 0.5f;
    [SerializeField] private float pickupGroundRaycastHeight = 4f;
    [SerializeField] private float pickupGroundRaycastDistance = 10f;
    [SerializeField] private float pickupBounceDamping = 0.35f;
    [SerializeField] private float pickupGroundFriction = 6f;
    [SerializeField] private float pickupStopSpeed = 0.18f;
    [SerializeField] private float pickupPhysicsSyncInterval = 0.05f;
    [SerializeField] private float boxLootSpawnHeight = 1.1f;
    [SerializeField] private Vector2 boxLootHorizontalSpeedRange = new(2.2f, 3.6f);
    [SerializeField] private Vector2 boxLootUpwardSpeedRange = new(4.2f, 5.8f);

    [Header("Equipment Hook")]
    [SerializeField] private float hookRange = 45f;
    [SerializeField] private float hookSelectRadius = 0.75f;
    [Min(1f)]
    [Tooltip("Controls both the temporary hook visual speed and the equipment pull speed.")]
    [SerializeField] private float hookPullSpeed = 40f;
    [SerializeField] private float hookEquipRadius = 1.1f;
    [SerializeField] private float hookServerCooldown = 0.5f;
    [SerializeField] private float hookOriginTolerance = 4f;

    private readonly Dictionary<int, PickupSlot> slots = new();
    private readonly Dictionary<int, BoxSlot> boxSlots = new();
    private readonly Dictionary<ulong, float> nextHookRequestTimes = new();
    private PlayerStatsState statsState;
    private MatchStateController matchStateController;
    private float nextScanTime;
    private float nextLocalRequestTime;
    private int nextHookVisualId;
    private int nextLootPickupSlotId;

    private void Awake()
    {
        // 아이템 매니저를 다른 게임플레이 스크립트에서 참조하기 위한 싱글턴 설정.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        // 네트워크 스폰 후 상태 컨트롤러/스탯 상태를 연결하고 현재 경기 상태에 맞춰 초기화.
        statsState = PlayerStatsState.Instance;
        matchStateController = MatchStateController.Instance;

        CreateLocalVisualSlots();

        if (!IsServer)
        {
            return;
        }

        if (matchStateController != null && matchStateController.IsSpawned)
        {
            matchStateController.State.OnValueChanged += OnMatchStateChanged;
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }

        HandleStateEntered(matchStateController != null ? matchStateController.State.Value : NetworkMatchState.Lobby);
    }

    public override void OnNetworkDespawn()
    {
        // 서버 전용 이벤트 구독을 해제해 재시작/씬 정리 때 중복 호출을 방지.
        if (IsServer && matchStateController != null && matchStateController.IsSpawned)
        {
            matchStateController.State.OnValueChanged -= OnMatchStateChanged;
        }

        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
    }

    private void Update()
    {
        // 서버는 서버 기준 위치로 자동 획득을 시도하고, 클라이언트는 자신의 위치 기준으로 획득 요청을 보낸다.
        if (IsServer && Time.time >= nextScanTime)
        {
            nextScanTime = Time.time + scanInterval;
            ScanPickupCollection();
        }

        if (IsClient && !IsServer && Time.time >= nextLocalRequestTime)
        {
            nextLocalRequestTime = Time.time + scanInterval;
            TryRequestLocalPickup();
        }
    }

    private void OnMatchStateChanged(NetworkMatchState previous, NetworkMatchState current)
    {
        // 경기 상태가 바뀔 때 해당 상태에 필요한 아이템 배치/정리를 수행.
        HandleStateEntered(current);
    }

    private void HandleStateEntered(NetworkMatchState state)
    {
        // 서버 기준으로 상태별 아이템/스탯 초기화 정책을 적용.
        if (!IsServer)
        {
            return;
        }

        switch (state)
        {
            case NetworkMatchState.Lobby:
                ClearAllPickups();
                ClearAllBoxItems();
                statsState?.ResetStats();
                break;
            case NetworkMatchState.MatchMain:
                ClearAllPickups();
                ClearAllBoxItems();
                NetworkPlayerEquipmentState.EquipDefaultForAll();
                NetworkPlayerCombatState.ResetForMatchStartForAll();
                statsState?.ResetStats();
                SpawnMainMatchPickups();
                SpawnEquipmentPickups();
                SpawnBoxItems();
                break;
            case NetworkMatchState.FinalTransition:
                ClearAllPickups();
                ClearAllBoxItems();
                break;
            case NetworkMatchState.FinalMatch:
                ClearAllPickups();
                ClearAllBoxItems();
                List<ulong> restoredClientIds = NetworkPlayerEquipmentState.EquipDefaultForUnequippedAll();
                NetworkPlayerCombatState.ResetForClients(restoredClientIds);
                SpawnFinalObjective();
                break;
            case NetworkMatchState.Result:
                ClearAllPickups();
                ClearAllBoxItems();
                break;
        }
    }

    private void SpawnMainMatchPickups()
    {
        // 메인 경기용 스탯 아이템 슬롯을 랜덤 위치와 랜덤 스탯으로 활성화.
        for (int i = 0; i < statPickupCount; i++)
        {
            ActivateRandomContactPickup(i, GetRandomSpawnPosition());
        }

        Debug.Log($"[GameplayPickupManager] Main match pickups spawned count={statPickupCount}");
    }

    private void SpawnFinalObjective()
    {
        // 최종전 승리 조건인 단일 목표 아이템을 필드에 배치.
        ActivateFinalObjective(finalObjectiveSlotIndex, GetRandomSpawnPosition());
        Debug.Log("[GameplayPickupManager] Final objective spawned.");
    }

    private void ActivateStatPickup(int slotId, PlayerStatType statType, Vector3 position, bool respawnOnCollect = true)
    {
        // Activate a stat pickup with normal gravity and no launch impulse.
        ActivateStatPickup(slotId, statType, position, respawnOnCollect, Vector3.zero);
    }

    private void ActivateStatPickup(int slotId, PlayerStatType statType, Vector3 position, bool respawnOnCollect, Vector3 initialVelocity)
    {
        // 서버 슬롯 상태를 활성화하고 모든 클라이언트에 비주얼 표시를 요청.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = true;
        slot.Hooked = false;
        slot.RespawnOnCollect = respawnOnCollect;
        slot.Kind = PickupKind.Stat;
        slot.StatType = statType;
        slot.FunctionalType = FunctionalPickupType.None;
        slot.EquipmentId = string.Empty;
        slot.Position = position;

        SetPickupVisualClientRpc(slotId, true, position, statType, PickupKind.Stat, default, FunctionalPickupType.None);
        StartPickupDespawnTimer(slotId);
        StartPickupPhysics(slotId, initialVelocity);
    }

    private void ActivateRandomContactPickup(int slotId, Vector3 position, bool respawnOnCollect = true)
    {
        // Fill a contact pickup slot with either a stat item or a functional item from the current pool.
        if (ShouldSpawnFunctionalPickup())
        {
            ActivateFunctionalPickup(slotId, GetRandomFunctionalPickupType(), position, respawnOnCollect);
            return;
        }

        ActivateStatPickup(slotId, GetRandomStatType(), position, respawnOnCollect);
    }

    private void ActivateFunctionalPickup(int slotId, FunctionalPickupType functionalType, Vector3 position, bool respawnOnCollect = true)
    {
        // Activate a contact-collected functional item such as the current basic heal pickup.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = true;
        slot.Hooked = false;
        slot.RespawnOnCollect = respawnOnCollect;
        slot.Kind = PickupKind.Functional;
        slot.StatType = PlayerStatType.Health;
        slot.FunctionalType = functionalType;
        slot.EquipmentId = string.Empty;
        slot.Position = position;

        SetPickupVisualClientRpc(slotId, true, position, slot.StatType, PickupKind.Functional, default, functionalType);
        StartPickupDespawnTimer(slotId);
        StartPickupPhysics(slotId, Vector3.zero);
    }

    private void SpawnEquipmentPickups()
    {
        // Spawn hook-only equipment drops from the Resources equipment catalog.
        IReadOnlyList<EquipmentDefinition> equipmentDefinitions = EquipmentCatalog.GetAll();
        if (equipmentDefinitions.Count == 0)
        {
            Debug.LogWarning("[GameplayPickupManager] Equipment pickups skipped because no equipment definitions were found.");
            return;
        }

        for (int i = 0; i < equipmentPickupCount; i++)
        {
            EquipmentDefinition equipment = EquipmentCatalog.GetRandom();
            if (equipment != null)
            {
                ActivateEquipmentPickup(equipmentSlotIdBase + i, equipment, GetRandomSpawnPosition());
            }
        }

        Debug.Log($"[GameplayPickupManager] Equipment pickups spawned count={equipmentPickupCount}");
    }

    private void SpawnBoxItems()
    {
        // Spawn destructible basic boxes that pre-roll their stat loot on the server.
        for (int i = 0; i < boxItemCount; i++)
        {
            ActivateBasicBoxItem(boxSlotIdBase + i, GetRandomSpawnPosition());
        }

        Debug.Log($"[GameplayPickupManager] Box items spawned count={boxItemCount}");
    }

    private void ActivateBasicBoxItem(int slotId, Vector3 position)
    {
        // Initialize a basic destructible box with enough health for five basic weapon hits.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        slot.Active = true;
        slot.BoxId = "basic_box";
        slot.MaxHealth = Mathf.Max(1f, basicBoxMaxHealth);
        slot.CurrentHealth = slot.MaxHealth;
        slot.LootStats = GenerateStatLoot(Mathf.Clamp(basicBoxLootCount, 0, 3));
        slot.Position = position;

        SetBoxVisualClientRpc(slotId, true, position, 1f);
        StartBoxDespawnTimer(slotId);
    }

    private void DeactivateBoxItem(int slotId, bool stopDespawnTimer = true)
    {
        // Hide a server-managed box slot on every client.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        if (stopDespawnTimer)
        {
            StopBoxDespawnTimer(slotId, syncBlink: true);
        }

        slot.Active = false;
        SetBoxVisualClientRpc(slotId, false, Vector3.zero, 0f);
    }

    private void ClearAllBoxItems()
    {
        // Stop box respawns and hide all active box visuals when the match state changes.
        foreach (KeyValuePair<int, BoxSlot> pair in boxSlots)
        {
            BoxSlot slot = pair.Value;
            if (slot.RespawnRoutine != null)
            {
                StopCoroutine(slot.RespawnRoutine);
                slot.RespawnRoutine = null;
            }

            if (slot.DespawnRoutine != null)
            {
                StopCoroutine(slot.DespawnRoutine);
                slot.DespawnRoutine = null;
            }

            slot.Active = false;
            SetBoxBlinkClientRpc(pair.Key, false);
            SetBoxVisualClientRpc(pair.Key, false, Vector3.zero, 0f);
        }
    }

    private void ActivateFinalObjective(int slotId, Vector3 position)
    {
        // Activate the final objective pickup that still uses contact collection.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = true;
        slot.Hooked = false;
        slot.RespawnOnCollect = false;
        slot.Kind = PickupKind.FinalObjective;
        slot.StatType = PlayerStatType.MoveSpeed;
        slot.FunctionalType = FunctionalPickupType.None;
        slot.EquipmentId = string.Empty;
        slot.Position = position;

        SetPickupVisualClientRpc(slotId, true, position, slot.StatType, PickupKind.FinalObjective, default, FunctionalPickupType.None);
        StartPickupPhysics(slotId, Vector3.zero);
    }

    private void ActivateEquipmentPickup(int slotId, EquipmentDefinition equipment, Vector3 position, float healthPercent = 1f)
    {
        // Activate a hook-only equipment pickup slot, keeping the equipment's stored health ratio.
        if (equipment == null)
        {
            return;
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = true;
        slot.Hooked = false;
        slot.RespawnOnCollect = false;
        slot.Kind = PickupKind.Equipment;
        slot.StatType = PlayerStatType.AttackPower;
        slot.FunctionalType = FunctionalPickupType.None;
        slot.EquipmentId = equipment.EquipmentId;
        slot.EquipmentMaxHealth = ResolveEquipmentDropMaxHealth(equipment);
        slot.EquipmentCurrentHealth = Mathf.Max(0f, slot.EquipmentMaxHealth * Mathf.Clamp01(healthPercent));
        slot.EquipmentHealthPercent = ResolveEquipmentHealthPercent(slot);
        slot.Position = position;

        SetPickupVisualClientRpc(slotId, true, position, slot.StatType, PickupKind.Equipment, new FixedString64Bytes(equipment.EquipmentId), FunctionalPickupType.None);
        SetEquipmentHealthVisualClientRpc(slotId, slot.EquipmentHealthPercent);
        StartPickupDespawnTimer(slotId);
        StartPickupPhysics(slotId, Vector3.zero);
    }

    private void DeactivatePickup(int slotId, bool stopDespawnTimer = true)
    {
        // 서버 슬롯 상태를 비활성화하고 모든 클라이언트에서 비주얼을 숨김.
        PickupSlot slot = GetOrCreateSlot(slotId);
        if (stopDespawnTimer)
        {
            StopPickupDespawnTimer(slotId, syncBlink: true);
        }

        slot.Active = false;
        slot.Hooked = false;
        StopPickupPhysics(slot);
        SetPickupVisualClientRpc(slotId, false, Vector3.zero, slot.StatType, slot.Kind, new FixedString64Bytes(slot.EquipmentId ?? string.Empty), slot.FunctionalType);
    }

    private void ClearAllPickups()
    {
        // 진행 중인 리스폰 예약을 취소하고 모든 아이템 슬롯을 비활성화.
        nextLootPickupSlotId = lootPickupSlotIdBase;
        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (slot.RespawnRoutine != null)
            {
                StopCoroutine(slot.RespawnRoutine);
                slot.RespawnRoutine = null;
            }

            slot.Active = false;
            slot.Hooked = false;
            if (slot.HookRoutine != null)
            {
                StopCoroutine(slot.HookRoutine);
                slot.HookRoutine = null;
            }

            if (slot.DespawnRoutine != null)
            {
                StopCoroutine(slot.DespawnRoutine);
                slot.DespawnRoutine = null;
            }

            StopPickupPhysics(slot);
            SetPickupBlinkClientRpc(pair.Key, false);
            SetPickupVisualClientRpc(pair.Key, false, Vector3.zero, slot.StatType, slot.Kind, new FixedString64Bytes(slot.EquipmentId ?? string.Empty), slot.FunctionalType);
        }
    }

    private void ScanPickupCollection()
    {
        // 서버가 네트워크 PlayerObject 위치 기준으로 아이템 획득 거리를 검사.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (!slot.Active)
            {
                continue;
            }

            if (!IsContactCollectable(slot))
            {
                continue;
            }

            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                if (!NetworkPlayerEquipmentState.ClientCanCollectItems(clientId))
                {
                    continue;
                }

                if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client))
                {
                    continue;
                }

                NetworkObject playerObject = client.PlayerObject;
                if (playerObject == null)
                {
                    continue;
                }

                float sqrDistance = (playerObject.transform.position - slot.Position).sqrMagnitude;
                if (sqrDistance <= collectRadius * collectRadius)
                {
                    CollectPickup(pair.Key, clientId);
                    break;
                }
            }
        }
    }

    private void CollectPickup(int slotId, ulong clientId)
    {
        // 최종 목표/스탯 아이템 종류에 따라 승자 확정 또는 스탯 증가를 처리.
        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!slot.Active)
        {
            return;
        }

        if (!IsContactCollectable(slot))
        {
            Debug.LogWarning($"[GameplayPickupManager] Contact pickup ignored for non-contact slot clientId={clientId} slot={slotId} kind={slot.Kind}");
            return;
        }

        if (!NetworkPlayerEquipmentState.ClientCanCollectItems(clientId))
        {
            Debug.LogWarning($"[GameplayPickupManager] Pickup rejected because client has no collecting equipment clientId={clientId}");
            return;
        }

        if (slot.Kind == PickupKind.FinalObjective)
        {
            DeactivatePickup(slotId);
            matchStateController?.CompleteFinalObjectiveByClient(clientId);
            Debug.Log($"[GameplayPickupManager] Final objective collected clientId={clientId}");
            return;
        }

        if (slot.Kind == PickupKind.Functional)
        {
            if (!ApplyFunctionalPickup(slot, clientId))
            {
                return;
            }

            DeactivatePickup(slotId);
            ScheduleContactPickupRespawn(slotId, slot);
            Debug.Log($"[GameplayPickupManager] Functional pickup collected clientId={clientId} type={slot.FunctionalType}");
            return;
        }

        float previousMaxHealth = slot.StatType == PlayerStatType.Health
            ? NetworkPlayerCombatState.GetMaxHealthForClient(clientId)
            : 0f;
        statsState?.AddStat(clientId, slot.StatType, 1);
        if (slot.StatType == PlayerStatType.Health)
        {
            NetworkPlayerCombatState.AddCurrentHealthForMaxHealthGain(clientId, previousMaxHealth);
        }

        DeactivatePickup(slotId);
        ScheduleContactPickupRespawn(slotId, slot);

        Debug.Log($"[GameplayPickupManager] Stat pickup collected clientId={clientId} stat={slot.StatType}");
    }

    private void ScheduleContactPickupRespawn(int slotId, PickupSlot slot)
    {
        // Respawn contact-collected stat or functional pickups through the shared random contact pool.
        if (slot.RespawnOnCollect &&
            matchStateController != null &&
            matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnContactPickupAfterDelay(slotId));
        }
    }

    private bool ApplyFunctionalPickup(PickupSlot slot, ulong clientId)
    {
        // Apply the server-side effect for a functional pickup and report whether it should be consumed.
        return slot.FunctionalType switch
        {
            FunctionalPickupType.BasicHeal => NetworkPlayerCombatState.TryHealPercent(clientId, Mathf.Max(0f, basicHealPercent)),
            _ => false
        };
    }

    private void TryRequestLocalPickup()
    {
        // 클라이언트 로컬 조작 캐릭터 위치 기준으로 가까운 아이템 획득을 서버에 요청.
        if (!LocalPlayerCanCollectItems())
        {
            return;
        }

        if (NetworkManager.Singleton == null || !TryGetLocalCollectionPosition(out Vector3 playerPosition))
        {
            return;
        }

        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (!slot.Active)
            {
                continue;
            }

            if (!IsContactCollectable(slot))
            {
                continue;
            }

            float sqrDistance = (playerPosition - slot.Position).sqrMagnitude;
            if (sqrDistance <= collectRadius * collectRadius)
            {
                RequestPickupServerRpc(pair.Key, playerPosition);
                return;
            }
        }
    }

    public bool RequestEquipmentHook(Vector3 origin, Vector3 targetPoint)
    {
        // Local entry point used by right-click hook input to ask the server for equipment collection.
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            return false;
        }

        if (IsServer)
        {
            TryStartEquipmentHook(NetworkManager.Singleton.LocalClientId, origin, targetPoint, "local-server");
            return true;
        }

        RequestEquipmentHookServerRpc(origin, targetPoint);
        return true;
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestEquipmentHookServerRpc(Vector3 origin, Vector3 targetPoint, ServerRpcParams rpcParams = default)
    {
        // ServerRpc wrapper that validates the sender before resolving a hook target.
        TryStartEquipmentHook(rpcParams.Receive.SenderClientId, origin, targetPoint, $"client={rpcParams.Receive.SenderClientId}");
    }

    private bool TryStartEquipmentHook(ulong clientId, Vector3 origin, Vector3 targetPoint, string requesterLabel)
    {
        // Fire a server-authoritative hook; equipment starts moving only after the hook reaches it.
        if (!CanUseEquipmentHook(clientId, out NetworkObject playerObject))
        {
            Debug.Log($"[GameplayPickupManager] Equipment hook rejected requester={requesterLabel}");
            return false;
        }

        if (!TryConsumeHookCooldown(clientId, out float cooldownRemaining))
        {
            Debug.Log($"[GameplayPickupManager] Equipment hook rejected by cooldown requester={requesterLabel} remaining={cooldownRemaining:0.00}s");
            return false;
        }

        ApplyHookOriginCorrection(playerObject, ref origin, ref targetPoint);
        Vector3 hookTargetPoint = ResolveHookTargetPoint(origin, targetPoint, playerObject.transform.forward);
        int hookVisualId = GetNextHookVisualId();
        SpawnEquipmentHookVisualClientRpc(hookVisualId, origin, hookTargetPoint, playerObject.transform.position, hookPullSpeed);
        StartCoroutine(ResolveEquipmentHookTravel(clientId, origin, hookTargetPoint, hookVisualId));
        Debug.Log($"[GameplayPickupManager] Equipment hook fired clientId={clientId} requester={requesterLabel}");
        return true;
    }

    private bool CanUseEquipmentHook(ulong clientId, out NetworkObject playerObject)
    {
        // Check match state, action lock, and player object availability for hook use.
        playerObject = null;
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return false;
        }

        if (matchStateController == null || matchStateController.State.Value != NetworkMatchState.MatchMain)
        {
            return false;
        }

        if (!NetworkPlayerCombatState.ClientCanAct(clientId))
        {
            return false;
        }

        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client) ||
            client.PlayerObject == null)
        {
            return false;
        }

        playerObject = client.PlayerObject;
        return true;
    }

    private bool TryGetPlayerObject(ulong clientId, out NetworkObject playerObject)
    {
        // Resolve a connected client's player object for hook visual and pickup follow targets.
        playerObject = null;
        if (NetworkManager.Singleton == null ||
            !NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client) ||
            client.PlayerObject == null)
        {
            return false;
        }

        playerObject = client.PlayerObject;
        return true;
    }

    private bool TryConsumeHookCooldown(ulong clientId, out float cooldownRemaining)
    {
        // Rate-limit hook requests on the server so clients cannot spam target resolution.
        nextHookRequestTimes.TryGetValue(clientId, out float nextAllowedTime);
        cooldownRemaining = nextAllowedTime - Time.time;
        if (cooldownRemaining > 0f)
        {
            return false;
        }

        nextHookRequestTimes[clientId] = Time.time + Mathf.Max(0.05f, hookServerCooldown);
        cooldownRemaining = 0f;
        return true;
    }

    private int GetNextHookVisualId()
    {
        // Allocate a lightweight id so later server latch messages can retarget the matching visual.
        nextHookVisualId++;
        if (nextHookVisualId <= 0)
        {
            nextHookVisualId = 1;
        }

        return nextHookVisualId;
    }

    private void ApplyHookOriginCorrection(NetworkObject playerObject, ref Vector3 origin, ref Vector3 targetPoint)
    {
        // Keep client-provided hook origins near the server-known player position.
        Vector3 serverOrigin = playerObject.transform.position + Vector3.up * 1.1f;
        float tolerance = Mathf.Max(0f, hookOriginTolerance);
        if (tolerance > 0f && (origin - serverOrigin).sqrMagnitude <= tolerance * tolerance)
        {
            return;
        }

        Vector3 direction = targetPoint - origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return;
        }

        origin = serverOrigin;
        targetPoint = serverOrigin + direction.normalized * distance;
    }

    private Vector3 ResolveHookTargetPoint(Vector3 origin, Vector3 targetPoint, Vector3 fallbackDirection)
    {
        // Clamp the requested hook endpoint so missed hooks still travel forward up to the allowed range.
        Vector3 toTarget = targetPoint - origin;
        float requestedDistance = toTarget.magnitude;
        if (requestedDistance <= 0.001f)
        {
            Vector3 safeDirection = fallbackDirection.sqrMagnitude > 0.001f ? fallbackDirection.normalized : Vector3.forward;
            return origin + safeDirection * Mathf.Max(0.1f, hookRange);
        }

        float maxDistance = Mathf.Min(requestedDistance, Mathf.Max(0.1f, hookRange));
        return origin + toTarget.normalized * maxDistance;
    }

    private IEnumerator ResolveEquipmentHookTravel(ulong clientId, Vector3 origin, Vector3 targetPoint, int hookVisualId)
    {
        // Advance the server hook tip over time and latch only when it reaches an equipment drop.
        Vector3 currentTip = origin;
        float speed = Mathf.Max(0.1f, hookPullSpeed);

        while ((targetPoint - currentTip).sqrMagnitude > 0.0001f)
        {
            Vector3 previousTip = currentTip;
            currentTip = Vector3.MoveTowards(currentTip, targetPoint, speed * Time.deltaTime);

            if (TryFindHookContact(previousTip, currentTip, out int slotId))
            {
                BeginPullEquipmentToClient(slotId, clientId, hookVisualId);
                yield break;
            }

            yield return null;
        }

        if (TryFindHookContact(currentTip, targetPoint, out int finalSlotId))
        {
            BeginPullEquipmentToClient(finalSlotId, clientId, hookVisualId);
            yield break;
        }

        Debug.Log($"[GameplayPickupManager] Equipment hook missed clientId={clientId}");
    }

    private bool TryFindHookContact(Vector3 segmentStart, Vector3 segmentEnd, out int slotId)
    {
        // Find an active equipment drop touched by the current hook-tip movement segment.
        slotId = -1;
        Vector3 segment = segmentEnd - segmentStart;
        float segmentLength = segment.magnitude;
        if (segmentLength <= 0.001f)
        {
            return false;
        }

        Vector3 direction = segment / segmentLength;
        float nearestAlongSegment = float.MaxValue;
        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (slot == null ||
                !slot.Active ||
                slot.Hooked ||
                slot.Kind != PickupKind.Equipment ||
                slot.EquipmentCurrentHealth <= 0f ||
                !EquipmentCatalog.TryGet(slot.EquipmentId, out _))
            {
                continue;
            }

            float distanceToRay = DistanceFromRaySegment(segmentStart, direction, slot.Position, segmentLength, out float alongSegment);
            if (distanceToRay <= hookSelectRadius && alongSegment < nearestAlongSegment)
            {
                nearestAlongSegment = alongSegment;
                slotId = pair.Key;
            }
        }

        return slotId >= 0;
    }

    private void BeginPullEquipmentToClient(int slotId, ulong clientId, int hookVisualId)
    {
        // Mark a contacted equipment drop as hooked and start pulling it toward the requesting player.
        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!slot.Active || slot.Kind != PickupKind.Equipment || slot.Hooked || slot.EquipmentCurrentHealth <= 0f)
        {
            return;
        }

        slot.Hooked = true;
        StopPickupDespawnTimer(slotId, syncBlink: true);
        StopPickupPhysics(slot);
        if (slot.HookRoutine != null)
        {
            StopCoroutine(slot.HookRoutine);
        }

        if (TryGetPlayerObject(clientId, out NetworkObject playerObject))
        {
            LatchEquipmentHookVisualClientRpc(hookVisualId, slot.Position, playerObject.transform.position, hookPullSpeed);
        }

        slot.HookRoutine = StartCoroutine(PullEquipmentToClient(slotId, clientId));
        Debug.Log($"[GameplayPickupManager] Equipment hook latched clientId={clientId} slot={slotId} equipment={slot.EquipmentId}");
    }

    private IEnumerator PullEquipmentToClient(int slotId, ulong clientId)
    {
        // Move a hooked equipment drop toward the owning player until it is close enough to equip.
        PickupSlot slot = GetOrCreateSlot(slotId);
        while (slot.Active && slot.Kind == PickupKind.Equipment)
        {
            if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client) ||
                client.PlayerObject == null)
            {
                slot.Hooked = false;
                slot.HookRoutine = null;
                StartPickupDespawnTimer(slotId);
                StartPickupPhysics(slotId, Vector3.zero);
                yield break;
            }

            Vector3 targetPosition = client.PlayerObject.transform.position + Vector3.up * 0.6f;
            slot.Position = Vector3.MoveTowards(slot.Position, targetPosition, Mathf.Max(0.1f, hookPullSpeed) * Time.deltaTime);
            SetPickupVisualClientRpc(slotId, true, slot.Position, slot.StatType, slot.Kind, new FixedString64Bytes(slot.EquipmentId ?? string.Empty), slot.FunctionalType);

            if ((slot.Position - targetPosition).sqrMagnitude <= hookEquipRadius * hookEquipRadius)
            {
                EquipHookedEquipment(slotId, clientId);
                yield break;
            }

            yield return null;
        }

        slot.Hooked = false;
        slot.HookRoutine = null;
        StartPickupDespawnTimer(slotId);
        StartPickupPhysics(slotId, Vector3.zero);
    }

    private void EquipHookedEquipment(int slotId, ulong clientId)
    {
        // Swap the hooked equipment with the current one while preserving each equipment's health ratio.
        PickupSlot slot = GetOrCreateSlot(slotId);
        EquipmentDefinition incomingEquipment = EquipmentCatalog.Get(slot.EquipmentId);
        float incomingHealthPercent = ResolveEquipmentHealthPercent(slot);
        NetworkPlayerEquipmentState.TryGetClientEquipment(clientId, out EquipmentDefinition previousEquipment);
        float previousHealthPercent = previousEquipment != null ?
            NetworkPlayerCombatState.GetEquipmentHealthPercent(clientId) :
            0f;

        if (incomingEquipment != null && NetworkPlayerEquipmentState.TryEquipClient(clientId, incomingEquipment))
        {
            NetworkPlayerCombatState.ResetClientForEquippedHealthPercent(clientId, incomingHealthPercent);
            Debug.Log($"[GameplayPickupManager] Hooked equipment equipped clientId={clientId} equipment={incomingEquipment.EquipmentId} healthPercent={incomingHealthPercent:0.00}");
        }
        else
        {
            slot.Hooked = false;
            slot.HookRoutine = null;
            StartPickupDespawnTimer(slotId);
            StartPickupPhysics(slotId, Vector3.zero);
            return;
        }

        slot.HookRoutine = null;
        if (previousEquipment != null &&
            NetworkManager.Singleton != null &&
            NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client) &&
            client.PlayerObject != null)
        {
            ActivateEquipmentPickup(slotId, previousEquipment, ResolveEquipmentDropPosition(client.PlayerObject), previousHealthPercent);
            Debug.Log($"[GameplayPickupManager] Previous equipment dropped clientId={clientId} equipment={previousEquipment.EquipmentId} healthPercent={previousHealthPercent:0.00}");
            return;
        }

        DeactivatePickup(slotId);
        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnEquipmentPickupAfterDelay(slotId));
        }
    }

    private Vector3 ResolveEquipmentDropPosition(NetworkObject playerObject)
    {
        // Place swapped-out equipment just in front of the player so it remains visible and hookable.
        Vector3 position = playerObject.transform.position + playerObject.transform.forward * 1.2f;
        position.y = spawnY;
        return position;
    }

    private IEnumerator RespawnEquipmentPickupAfterDelay(int slotId)
    {
        // Respawn a hook-only equipment drop after a short delay during the main match.
        yield return new WaitForSeconds(equipmentRespawnDelay);

        if (!IsServer || matchStateController == null || matchStateController.State.Value != NetworkMatchState.MatchMain)
        {
            yield break;
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.RespawnRoutine = null;
        EquipmentDefinition equipment = EquipmentCatalog.GetRandom();
        if (equipment != null)
        {
            ActivateEquipmentPickup(slotId, equipment, GetRandomSpawnPosition(), 1f);
        }
    }

    public bool TryFindDamageableBox(Vector3 origin, Vector3 direction, float maxDistance, float projectileRadius, out int slotId, out float hitDistance, out Vector3 hitPoint)
    {
        // Find the nearest active destructible box touched by a server-approved projectile path.
        slotId = -1;
        hitDistance = float.MaxValue;
        hitPoint = default;
        if (!IsServer || direction.sqrMagnitude <= 0.0001f || maxDistance <= 0f)
        {
            return false;
        }

        Vector3 normalizedDirection = direction.normalized;
        float combinedRadius = Mathf.Max(0f, projectileRadius) + Mathf.Max(0.1f, boxHitRadius);
        foreach (KeyValuePair<int, BoxSlot> pair in boxSlots)
        {
            BoxSlot slot = pair.Value;
            if (slot == null || !slot.Active || slot.CurrentHealth <= 0f)
            {
                continue;
            }

            Vector3 targetPoint = slot.Position + Vector3.up * Mathf.Max(0f, boxTargetHeight);
            float distanceToPath = DistanceFromRaySegment(origin, normalizedDirection, targetPoint, maxDistance, out float alongRay);
            if (distanceToPath <= combinedRadius && alongRay < hitDistance)
            {
                slotId = pair.Key;
                hitDistance = alongRay;
                hitPoint = origin + normalizedDirection * alongRay;
            }
        }

        return slotId >= 0;
    }

    public bool TryApplyBoxDamage(int slotId, float damage, ulong attackerClientId)
    {
        // Apply server-authoritative damage to a destructible box and drop its pre-rolled loot on break.
        if (!IsServer || damage <= 0f)
        {
            return false;
        }

        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        if (!slot.Active || slot.CurrentHealth <= 0f)
        {
            return false;
        }

        slot.CurrentHealth = Mathf.Max(0f, slot.CurrentHealth - damage);
        float healthPercent = slot.MaxHealth > 0f ? Mathf.Clamp01(slot.CurrentHealth / slot.MaxHealth) : 0f;
        SetBoxVisualClientRpc(slotId, true, slot.Position, healthPercent);
        Debug.Log($"[GameplayPickupManager] Box damaged slot={slotId} attacker={attackerClientId} damage={damage:0.0} health={slot.CurrentHealth:0.0}");

        if (slot.CurrentHealth <= 0f)
        {
            BreakBoxItem(slotId, attackerClientId);
            return true;
        }

        StartBoxDespawnTimer(slotId);
        return true;
    }

    public bool TryFindDamageableEquipment(Vector3 origin, Vector3 direction, float maxDistance, float projectileRadius, out int slotId, out float hitDistance, out Vector3 hitPoint)
    {
        // Find the nearest active field equipment drop touched by a server-approved projectile path.
        slotId = -1;
        hitDistance = float.MaxValue;
        hitPoint = default;
        if (!IsServer || direction.sqrMagnitude <= 0.0001f || maxDistance <= 0f)
        {
            return false;
        }

        Vector3 normalizedDirection = direction.normalized;
        float combinedRadius = Mathf.Max(0f, projectileRadius) + Mathf.Max(0.1f, equipmentHitRadius);
        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            if (!IsDamageableEquipmentSlot(slot))
            {
                continue;
            }

            Vector3 targetPoint = slot.Position + Vector3.up * Mathf.Max(0f, equipmentTargetHeight);
            float distanceToPath = DistanceFromRaySegment(origin, normalizedDirection, targetPoint, maxDistance, out float alongRay);
            if (distanceToPath <= combinedRadius && alongRay < hitDistance)
            {
                slotId = pair.Key;
                hitDistance = alongRay;
                hitPoint = origin + normalizedDirection * alongRay;
            }
        }

        return slotId >= 0;
    }

    public bool TryApplyEquipmentDamage(int slotId, float damage, ulong attackerClientId)
    {
        // Apply server-authoritative damage to a field equipment drop and destroy it at zero health.
        if (!IsServer || damage <= 0f)
        {
            return false;
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!IsDamageableEquipmentSlot(slot))
        {
            return false;
        }

        EnsureEquipmentDropHealth(slot);
        slot.EquipmentCurrentHealth = Mathf.Max(0f, slot.EquipmentCurrentHealth - damage);
        slot.EquipmentHealthPercent = ResolveEquipmentHealthPercent(slot);
        SetEquipmentHealthVisualClientRpc(slotId, slot.EquipmentHealthPercent);
        Debug.Log($"[GameplayPickupManager] Equipment damaged slot={slotId} attacker={attackerClientId} equipment={slot.EquipmentId} damage={damage:0.0} health={slot.EquipmentCurrentHealth:0.0}/{slot.EquipmentMaxHealth:0.0}");

        if (slot.EquipmentCurrentHealth <= 0f)
        {
            DestroyEquipmentPickup(slotId, attackerClientId);
        }

        return true;
    }

    private void BreakBoxItem(int slotId, ulong attackerClientId)
    {
        // Convert a destroyed box into its pre-selected stat loot and schedule a replacement box.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        Vector3 dropCenter = slot.Position;
        PlayerStatType[] lootStats = slot.LootStats ?? GenerateStatLoot(Mathf.Clamp(basicBoxLootCount, 0, 3));

        DeactivateBoxItem(slotId);
        for (int i = 0; i < lootStats.Length; i++)
        {
            ActivateStatPickup(
                GetNextLootPickupSlotId(),
                lootStats[i],
                ResolveBoxLootLaunchPosition(dropCenter),
                respawnOnCollect: false,
                initialVelocity: ResolveBoxLootLaunchVelocity(i, lootStats.Length));
        }

        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnBoxItemAfterDelay(slotId));
        }

        Debug.Log($"[GameplayPickupManager] Box broken slot={slotId} attacker={attackerClientId} lootCount={lootStats.Length}");
    }

    private IEnumerator RespawnBoxItemAfterDelay(int slotId)
    {
        // Respawn a basic destructible box after a short delay during the main match.
        yield return new WaitForSeconds(boxRespawnDelay);

        if (!IsServer || matchStateController == null || matchStateController.State.Value != NetworkMatchState.MatchMain)
        {
            yield break;
        }

        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        slot.RespawnRoutine = null;
        ActivateBasicBoxItem(slotId, GetRandomSpawnPosition());
    }

    private PlayerStatType[] GenerateStatLoot(int lootCount)
    {
        // Pre-roll the stat items that a box will spill when destroyed.
        int resolvedCount = Mathf.Max(0, lootCount);
        PlayerStatType[] lootStats = new PlayerStatType[resolvedCount];
        for (int i = 0; i < resolvedCount; i++)
        {
            lootStats[i] = GetRandomStatType();
        }

        return lootStats;
    }

    private Vector3 ResolveBoxLootLaunchPosition(Vector3 center)
    {
        // Start box loot slightly above the broken box so gravity can arc it outward.
        return center + Vector3.up * Mathf.Max(0f, boxLootSpawnHeight);
    }

    private Vector3 ResolveBoxLootLaunchVelocity(int index, int count)
    {
        // Give each box loot item a small outward-and-up impulse for a readable spill effect.
        float angle = count > 0
            ? (Mathf.PI * 2f * index / count) + Random.Range(-0.35f, 0.35f)
            : Random.Range(0f, Mathf.PI * 2f);
        float horizontalSpeed = Random.Range(
            Mathf.Min(boxLootHorizontalSpeedRange.x, boxLootHorizontalSpeedRange.y),
            Mathf.Max(boxLootHorizontalSpeedRange.x, boxLootHorizontalSpeedRange.y));
        horizontalSpeed *= Mathf.Max(0.1f, boxLootScatterRadius) / 1.4f;
        float upwardSpeed = Random.Range(
            Mathf.Min(boxLootUpwardSpeedRange.x, boxLootUpwardSpeedRange.y),
            Mathf.Max(boxLootUpwardSpeedRange.x, boxLootUpwardSpeedRange.y));
        Vector3 horizontalDirection = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        return horizontalDirection * Mathf.Max(0f, horizontalSpeed) + Vector3.up * Mathf.Max(0f, upwardSpeed);
    }

    private int GetNextLootPickupSlotId()
    {
        // Allocate non-respawning loot pickup slots above the normal pickup and box id ranges.
        if (nextLootPickupSlotId < lootPickupSlotIdBase)
        {
            nextLootPickupSlotId = lootPickupSlotIdBase;
        }

        return nextLootPickupSlotId++;
    }

    private void StartPickupDespawnTimer(int slotId)
    {
        // Start or reset the timed despawn for stat and equipment pickups.
        if (!IsServer)
        {
            return;
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!slot.Active || slot.Kind == PickupKind.FinalObjective)
        {
            return;
        }

        StopPickupDespawnTimer(slotId, syncBlink: true);
        slot.DespawnRoutine = StartCoroutine(DespawnPickupAfterLifetime(slotId, GetRandomDespawnLifetime()));
    }

    private void StopPickupDespawnTimer(int slotId, bool syncBlink)
    {
        // Cancel the server despawn timer and optionally stop client blink visuals.
        PickupSlot slot = GetOrCreateSlot(slotId);
        if (slot.DespawnRoutine != null)
        {
            StopCoroutine(slot.DespawnRoutine);
            slot.DespawnRoutine = null;
        }

        if (syncBlink)
        {
            SetPickupBlinkClientRpc(slotId, false);
        }
    }

    private IEnumerator DespawnPickupAfterLifetime(int slotId, float lifetime)
    {
        // Blink shortly before a timed pickup disappears, then despawn or respawn it by slot rules.
        float blinkDelay = Mathf.Max(0f, lifetime - Mathf.Max(0f, despawnBlinkLeadTime));
        if (blinkDelay > 0f)
        {
            yield return new WaitForSeconds(blinkDelay);
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!slot.Active || slot.Hooked)
        {
            slot.DespawnRoutine = null;
            yield break;
        }

        SetPickupBlinkClientRpc(slotId, true);
        float remainingLifetime = Mathf.Max(0f, lifetime - blinkDelay);
        if (remainingLifetime > 0f)
        {
            yield return new WaitForSeconds(remainingLifetime);
        }

        slot = GetOrCreateSlot(slotId);
        slot.DespawnRoutine = null;
        if (!slot.Active || slot.Hooked)
        {
            SetPickupBlinkClientRpc(slotId, false);
            yield break;
        }

        DespawnPickupByTimer(slotId);
    }

    private void DespawnPickupByTimer(int slotId)
    {
        // Remove a pickup due to lifetime expiry and schedule the appropriate replacement.
        PickupSlot slot = GetOrCreateSlot(slotId);
        PickupKind despawnedKind = slot.Kind;
        bool shouldRespawnContact = slot.RespawnOnCollect &&
            (despawnedKind == PickupKind.Stat || despawnedKind == PickupKind.Functional);
        bool shouldRespawnEquipment = despawnedKind == PickupKind.Equipment;

        SetPickupBlinkClientRpc(slotId, false);
        DeactivatePickup(slotId, stopDespawnTimer: false);

        if (matchStateController == null || matchStateController.State.Value != NetworkMatchState.MatchMain)
        {
            return;
        }

        if (shouldRespawnContact)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnContactPickupAfterDelay(slotId));
            return;
        }

        if (shouldRespawnEquipment)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnEquipmentPickupAfterDelay(slotId));
        }
    }

    private void StartBoxDespawnTimer(int slotId)
    {
        // Start or reset the timed despawn for a destructible box.
        if (!IsServer)
        {
            return;
        }

        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        if (!slot.Active)
        {
            return;
        }

        StopBoxDespawnTimer(slotId, syncBlink: true);
        slot.DespawnRoutine = StartCoroutine(DespawnBoxAfterLifetime(slotId, GetRandomDespawnLifetime()));
    }

    private void StopBoxDespawnTimer(int slotId, bool syncBlink)
    {
        // Cancel a box despawn timer and optionally stop client blink visuals.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        if (slot.DespawnRoutine != null)
        {
            StopCoroutine(slot.DespawnRoutine);
            slot.DespawnRoutine = null;
        }

        if (syncBlink)
        {
            SetBoxBlinkClientRpc(slotId, false);
        }
    }

    private IEnumerator DespawnBoxAfterLifetime(int slotId, float lifetime)
    {
        // Blink shortly before a box disappears, then respawn it without dropping loot.
        float blinkDelay = Mathf.Max(0f, lifetime - Mathf.Max(0f, despawnBlinkLeadTime));
        if (blinkDelay > 0f)
        {
            yield return new WaitForSeconds(blinkDelay);
        }

        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        if (!slot.Active)
        {
            slot.DespawnRoutine = null;
            yield break;
        }

        SetBoxBlinkClientRpc(slotId, true);
        float remainingLifetime = Mathf.Max(0f, lifetime - blinkDelay);
        if (remainingLifetime > 0f)
        {
            yield return new WaitForSeconds(remainingLifetime);
        }

        slot = GetOrCreateBoxSlot(slotId);
        slot.DespawnRoutine = null;
        if (!slot.Active)
        {
            SetBoxBlinkClientRpc(slotId, false);
            yield break;
        }

        SetBoxBlinkClientRpc(slotId, false);
        DeactivateBoxItem(slotId, stopDespawnTimer: false);
        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnBoxItemAfterDelay(slotId));
        }
    }

    private float GetRandomDespawnLifetime()
    {
        // Pick a random lifetime in the inspector-defined despawn range.
        float minLifetime = Mathf.Max(0.1f, Mathf.Min(despawnLifetimeRange.x, despawnLifetimeRange.y));
        float maxLifetime = Mathf.Max(minLifetime, Mathf.Max(despawnLifetimeRange.x, despawnLifetimeRange.y));
        return Random.Range(minLifetime, maxLifetime);
    }

    private bool IsDamageableEquipmentSlot(PickupSlot slot)
    {
        // A field equipment drop can be damaged only while it is active, unhooked, and still has durability.
        return slot != null &&
            slot.Active &&
            !slot.Hooked &&
            slot.Kind == PickupKind.Equipment &&
            (slot.EquipmentCurrentHealth > 0f || slot.EquipmentHealthPercent > 0f) &&
            EquipmentCatalog.TryGet(slot.EquipmentId, out _);
    }

    private void EnsureEquipmentDropHealth(PickupSlot slot)
    {
        // Lazily restore missing drop health data from the equipment definition for older or reset slots.
        if (slot == null || !EquipmentCatalog.TryGet(slot.EquipmentId, out EquipmentDefinition equipment))
        {
            return;
        }

        if (slot.EquipmentMaxHealth <= 0f)
        {
            slot.EquipmentMaxHealth = ResolveEquipmentDropMaxHealth(equipment);
        }

        if (slot.EquipmentCurrentHealth <= 0f && slot.EquipmentHealthPercent > 0f)
        {
            slot.EquipmentCurrentHealth = slot.EquipmentMaxHealth * Mathf.Clamp01(slot.EquipmentHealthPercent);
        }

        slot.EquipmentHealthPercent = ResolveEquipmentHealthPercent(slot);
    }

    private float ResolveEquipmentDropMaxHealth(EquipmentDefinition equipment)
    {
        // Field equipment health uses base player health with zero collected Health stacks and equipment modifiers only.
        float baseHealth = Mathf.Max(1f, equipmentDropBaseHealth);
        return equipment != null
            ? Mathf.Max(1f, equipment.ModifyStat(PlayerStatType.Health, baseHealth))
            : baseHealth;
    }

    private float ResolveEquipmentHealthPercent(PickupSlot slot)
    {
        // Convert field equipment durability back into the ratio used by equip and drop transitions.
        if (slot == null || slot.EquipmentMaxHealth <= 0f)
        {
            return 1f;
        }

        return Mathf.Clamp01(slot.EquipmentCurrentHealth / slot.EquipmentMaxHealth);
    }

    private void DestroyEquipmentPickup(int slotId, ulong attackerClientId)
    {
        // Remove a destroyed field equipment drop and let the normal equipment respawn flow replace it.
        PickupSlot slot = GetOrCreateSlot(slotId);
        string equipmentId = slot.EquipmentId;
        DeactivatePickup(slotId);

        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnEquipmentPickupAfterDelay(slotId));
        }

        Debug.Log($"[GameplayPickupManager] Equipment destroyed slot={slotId} attacker={attackerClientId} equipment={equipmentId}");
    }

    private void StartPickupPhysics(int slotId, Vector3 initialVelocity)
    {
        // Start server-side lightweight physics so pickup visuals and collection checks share one position.
        if (!IsServer || !enablePickupGravity)
        {
            return;
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        StopPickupPhysics(slot);
        if (!slot.Active || slot.Hooked)
        {
            return;
        }

        slot.PhysicsVelocity = initialVelocity;
        slot.PhysicsRoutine = StartCoroutine(SimulatePickupPhysics(slotId));
    }

    private void StopPickupPhysics(PickupSlot slot)
    {
        // Stop a pickup physics routine when the slot is hidden, hooked, or reset.
        if (slot == null)
        {
            return;
        }

        if (slot.PhysicsRoutine != null)
        {
            StopCoroutine(slot.PhysicsRoutine);
            slot.PhysicsRoutine = null;
        }

        slot.PhysicsVelocity = Vector3.zero;
    }

    private IEnumerator SimulatePickupPhysics(int slotId)
    {
        // Move one pickup under gravity, bounce it lightly on the ground, and sync the server slot position.
        PickupSlot slot = GetOrCreateSlot(slotId);
        float syncTimer = 0f;
        float stopSpeedSqr = pickupStopSpeed * pickupStopSpeed;

        while (slot.Active && !slot.Hooked)
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
            {
                yield return null;
                continue;
            }

            Vector3 nextPosition = ResolveNextPickupPhysicsPosition(slot, deltaTime, out bool grounded);
            slot.Position = nextPosition;

            syncTimer += deltaTime;
            if (syncTimer >= Mathf.Max(0.01f, pickupPhysicsSyncInterval))
            {
                syncTimer = 0f;
                SyncPickupVisual(slotId, slot);
            }

            if (grounded && slot.PhysicsVelocity.sqrMagnitude <= stopSpeedSqr)
            {
                slot.PhysicsVelocity = Vector3.zero;
                SyncPickupVisual(slotId, slot);
                break;
            }

            yield return null;
        }

        slot.PhysicsRoutine = null;
    }

    private Vector3 ResolveNextPickupPhysicsPosition(PickupSlot slot, float deltaTime, out bool grounded)
    {
        // Advance a pickup by one frame and resolve a simple bounce against the detected ground height.
        float gravity = -Mathf.Abs(pickupGravity);
        float currentGroundY = ResolvePickupRestY(slot.Position);
        bool wasGrounded = slot.Position.y <= currentGroundY + 0.01f && slot.PhysicsVelocity.y <= 0f;
        if (!wasGrounded)
        {
            slot.PhysicsVelocity += Vector3.up * gravity * deltaTime;
        }

        Vector3 nextPosition = slot.Position + slot.PhysicsVelocity * deltaTime;
        float nextGroundY = ResolvePickupRestY(nextPosition);
        grounded = nextPosition.y <= nextGroundY;
        if (!grounded)
        {
            return nextPosition;
        }

        nextPosition.y = nextGroundY;
        if (slot.PhysicsVelocity.y < -pickupStopSpeed)
        {
            slot.PhysicsVelocity.y = -slot.PhysicsVelocity.y * Mathf.Clamp01(pickupBounceDamping);
        }
        else
        {
            slot.PhysicsVelocity.y = 0f;
        }

        slot.PhysicsVelocity = ApplyPickupGroundFriction(slot.PhysicsVelocity, deltaTime);
        return nextPosition;
    }

    private float ResolvePickupRestY(Vector3 position)
    {
        // Find the nearest ground below a pickup and return the desired center height above it.
        Vector3 rayOrigin = position + Vector3.up * Mathf.Max(0f, pickupGroundRaycastHeight);
        float rayDistance = Mathf.Max(0.1f, pickupGroundRaycastHeight + pickupGroundRaycastDistance);
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, rayDistance, pickupGroundMask, QueryTriggerInteraction.Ignore))
        {
            return hit.point.y + Mathf.Max(0f, pickupRestHeight);
        }

        return spawnY;
    }

    private Vector3 ApplyPickupGroundFriction(Vector3 velocity, float deltaTime)
    {
        // Slow horizontal pickup movement after ground contact so bounced loot settles quickly.
        Vector3 horizontalVelocity = new(velocity.x, 0f, velocity.z);
        horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, Vector3.zero, Mathf.Max(0f, pickupGroundFriction) * deltaTime);
        return new Vector3(horizontalVelocity.x, velocity.y, horizontalVelocity.z);
    }

    private void SyncPickupVisual(int slotId, PickupSlot slot)
    {
        // Broadcast the current server pickup slot state to client-side temporary visuals.
        SetPickupVisualClientRpc(slotId, slot.Active, slot.Position, slot.StatType, slot.Kind, new FixedString64Bytes(slot.EquipmentId ?? string.Empty), slot.FunctionalType);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestPickupServerRpc(int slotId, Vector3 reportedPlayerPosition, ServerRpcParams rpcParams = default)
    {
        // 클라이언트 요청은 서버가 슬롯 상태와 거리 조건을 다시 확인한 뒤 처리한다.
        PickupSlot slot = GetOrCreateSlot(slotId);
        if (!slot.Active)
        {
            return;
        }

        if (!IsContactCollectable(slot))
        {
            Debug.LogWarning($"[GameplayPickupManager] Pickup request rejected because slot is not contact-collectable clientId={rpcParams.Receive.SenderClientId} slot={slotId} kind={slot.Kind}");
            return;
        }

        if (!NetworkPlayerEquipmentState.ClientCanCollectItems(rpcParams.Receive.SenderClientId))
        {
            Debug.LogWarning($"[GameplayPickupManager] Pickup request rejected because client has no collecting equipment clientId={rpcParams.Receive.SenderClientId}");
            return;
        }

        float maxAllowedDistance = collectRadius + 0.75f;
        float sqrDistance = (reportedPlayerPosition - slot.Position).sqrMagnitude;
        if (sqrDistance > maxAllowedDistance * maxAllowedDistance)
        {
            Debug.LogWarning($"[GameplayPickupManager] Pickup request rejected clientId={rpcParams.Receive.SenderClientId} slot={slotId} sqrDistance={sqrDistance:0.00}");
            return;
        }

        CollectPickup(slotId, rpcParams.Receive.SenderClientId);
    }

    private static bool IsContactCollectable(PickupSlot slot)
    {
        // Only stat, functional, and final objective pickups can be collected by touching them.
        return slot != null &&
            (slot.Kind == PickupKind.Stat ||
                slot.Kind == PickupKind.Functional ||
                slot.Kind == PickupKind.FinalObjective);
    }

    private static float DistanceFromRaySegment(Vector3 origin, Vector3 direction, Vector3 point, float maxDistance, out float alongRay)
    {
        // Measure the shortest distance from a point to the finite hook ray.
        alongRay = Mathf.Clamp(Vector3.Dot(point - origin, direction), 0f, maxDistance);
        Vector3 closestPoint = origin + direction * alongRay;
        return Vector3.Distance(point, closestPoint);
    }

    private static bool LocalPlayerCanCollectItems()
    {
        // Let local clients skip pickup requests when their current equipment cannot collect.
        PlayerEquipment equipment = FindFirstObjectByType<PlayerEquipment>();
        return equipment != null && equipment.CanCollectItems;
    }

    private static bool TryGetLocalCollectionPosition(out Vector3 position)
    {
        // 현재 테스트 씬에서는 실제 조작 캐릭터인 ThirdPersonController 위치를 우선 사용한다.
        ThirdPersonController controller = FindFirstObjectByType<ThirdPersonController>();
        if (controller != null)
        {
            position = controller.transform.position;
            return true;
        }

        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.SpawnManager != null &&
            NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject() != null)
        {
            position = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject().transform.position;
            return true;
        }

        position = default;
        return false;
    }

    private IEnumerator RespawnContactPickupAfterDelay(int slotId)
    {
        // Respawn a contact pickup as either a stat item or a functional item after a short delay.
        yield return new WaitForSeconds(statRespawnDelay);

        if (!IsServer || matchStateController == null || matchStateController.State.Value != NetworkMatchState.MatchMain)
        {
            yield break;
        }

        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.RespawnRoutine = null;
        ActivateRandomContactPickup(slotId, GetRandomSpawnPosition());
    }

    private void OnClientConnected(ulong clientId)
    {
        // 새 클라이언트가 접속하면 현재 활성 아이템 상태를 뒤늦게 동기화.
        StartCoroutine(SyncPickupsAfterClientJoin());
    }

    private IEnumerator SyncPickupsAfterClientJoin()
    {
        // 새 클라이언트가 씬 오브젝트를 스폰한 뒤 현재 아이템 상태를 다시 전파한다.
        yield return null;

        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            PickupSlot slot = pair.Value;
            SetPickupVisualClientRpc(pair.Key, slot.Active, slot.Position, slot.StatType, slot.Kind, new FixedString64Bytes(slot.EquipmentId ?? string.Empty), slot.FunctionalType);
            if (slot.Active && slot.Kind == PickupKind.Equipment)
            {
                SetEquipmentHealthVisualClientRpc(pair.Key, ResolveEquipmentHealthPercent(slot));
            }
        }

        foreach (KeyValuePair<int, BoxSlot> pair in boxSlots)
        {
            BoxSlot slot = pair.Value;
            float healthPercent = slot.MaxHealth > 0f ? Mathf.Clamp01(slot.CurrentHealth / slot.MaxHealth) : 0f;
            SetBoxVisualClientRpc(pair.Key, slot.Active, slot.Position, healthPercent);
        }
    }

    [ClientRpc]
    private void SpawnEquipmentHookVisualClientRpc(int hookVisualId, Vector3 origin, Vector3 hookTargetPoint, Vector3 playerPosition, float speed)
    {
        // Play a temporary hook tether visual on every client until a real hook asset is available.
        SimpleHookVisual.Spawn(hookVisualId, origin, hookTargetPoint, playerPosition + Vector3.up * 0.6f, speed);
    }

    [ClientRpc]
    private void LatchEquipmentHookVisualClientRpc(int hookVisualId, Vector3 latchPosition, Vector3 playerPosition, float speed)
    {
        // Stop the matching temporary hook visual at the equipment and start its return animation.
        SimpleHookVisual.Latch(hookVisualId, latchPosition, playerPosition + Vector3.up * 0.6f, speed);
    }

    [ClientRpc]
    private void SetPickupVisualClientRpc(int slotId, bool active, Vector3 position, PlayerStatType statType, PickupKind kind, FixedString64Bytes equipmentId, FunctionalPickupType functionalType)
    {
        // 서버 슬롯 상태를 받아 로컬 임시 비주얼의 표시/위치/색상을 갱신.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = active;
        slot.Position = position;
        slot.StatType = statType;
        slot.Kind = kind;
        slot.EquipmentId = equipmentId.ToString();
        slot.FunctionalType = functionalType;

        EnsureVisual(slotId, slot);
        slot.Visual.SetActive(active);
        if (!active)
        {
            SetPickupBlink(slot, false);
            SetEquipmentLowHealthSpark(slot, false);
            return;
        }

        if (!slot.Blinking)
        {
            SetVisualRenderersVisible(slot.Visual, true);
        }

        slot.Visual.transform.position = position;
        Renderer renderer = slot.Visual.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = GetPickupColor(statType, kind, slot.EquipmentId, slot.FunctionalType);
        }

        UpdateEquipmentLowHealthSpark(slot);
    }

    [ClientRpc]
    private void SetEquipmentHealthVisualClientRpc(int slotId, float healthPercent)
    {
        // Sync field equipment durability-dependent visual effects to every client.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.EquipmentHealthPercent = Mathf.Clamp01(healthPercent);
        UpdateEquipmentLowHealthSpark(slot);
    }

    [ClientRpc]
    private void SetBoxVisualClientRpc(int slotId, bool active, Vector3 position, float healthPercent)
    {
        // Sync a destructible box visual from the server-managed box slot state.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        slot.Active = active;
        slot.Position = position;

        EnsureBoxVisual(slotId, slot);
        slot.Visual.SetActive(active);
        if (!active)
        {
            SetBoxBlink(slot, false);
            return;
        }

        if (!slot.Blinking)
        {
            SetVisualRenderersVisible(slot.Visual, true);
        }

        slot.Visual.transform.position = position;
        ApplyBoxVisualHealthColor(slot.Visual, healthPercent);
    }

    [ClientRpc]
    private void SetPickupBlinkClientRpc(int slotId, bool blinking)
    {
        // Toggle local blinking for a pickup that is close to timed despawn.
        PickupSlot slot = GetOrCreateSlot(slotId);
        SetPickupBlink(slot, blinking);
    }

    [ClientRpc]
    private void SetBoxBlinkClientRpc(int slotId, bool blinking)
    {
        // Toggle local blinking for a box that is close to timed despawn.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        SetBoxBlink(slot, blinking);
    }

    private void CreateLocalVisualSlots()
    {
        // 클라이언트별 임시 Primitive 비주얼을 미리 만들어 RPC 표시 요청에 대비.
        for (int i = 0; i < statPickupCount; i++)
        {
            EnsureVisual(i, GetOrCreateSlot(i));
        }

        PickupSlot finalSlot = GetOrCreateSlot(finalObjectiveSlotIndex);
        finalSlot.Kind = PickupKind.FinalObjective;
        EnsureVisual(finalObjectiveSlotIndex, finalSlot);

        for (int i = 0; i < equipmentPickupCount; i++)
        {
            PickupSlot equipmentSlot = GetOrCreateSlot(equipmentSlotIdBase + i);
            equipmentSlot.Kind = PickupKind.Equipment;
            EnsureVisual(equipmentSlotIdBase + i, equipmentSlot);
        }
    }

    private void EnsureVisual(int slotId, PickupSlot slot)
    {
        // 슬롯에 아직 비주얼이 없으면 임시 Sphere/Capsule 오브젝트를 생성.
        string visualKey = GetPickupVisualKey(slot);
        if (slot.Visual != null && slot.VisualKind == slot.Kind && slot.VisualEquipmentId == visualKey)
        {
            return;
        }

        if (slot.Visual != null)
        {
            SetEquipmentLowHealthSpark(slot, false);
            slot.EquipmentLowHealthSparkEffect = null;
            Destroy(slot.Visual);
            slot.Visual = null;
        }

        GameObject visual = CreatePickupVisual(slot);
        visual.name = $"PickupVisual_{slotId}";
        RemoveVisualColliders(visual);

        visual.SetActive(false);
        slot.Visual = visual;
        slot.VisualKind = slot.Kind;
        slot.VisualEquipmentId = visualKey;
    }

    private static string GetPickupVisualKey(PickupSlot slot)
    {
        // Keep temporary visuals keyed by subtype so future functional items can swap visuals cleanly.
        return slot.Kind switch
        {
            PickupKind.Equipment => slot.EquipmentId ?? string.Empty,
            PickupKind.Functional => slot.FunctionalType.ToString(),
            _ => string.Empty
        };
    }

    private GameObject CreatePickupVisual(PickupSlot slot)
    {
        // Instantiate a real equipment prefab when assigned, otherwise create a clear temporary primitive.
        if (slot.Kind == PickupKind.Equipment)
        {
            EquipmentDefinition equipment = EquipmentCatalog.Get(slot.EquipmentId);
            if (equipment != null && equipment.VisualPrefab != null)
            {
                return Instantiate(equipment.VisualPrefab);
            }
        }

        PrimitiveType primitiveType = slot.Kind switch
        {
            PickupKind.FinalObjective => PrimitiveType.Capsule,
            PickupKind.Equipment => PrimitiveType.Cube,
            PickupKind.Functional => PrimitiveType.Cylinder,
            _ => PrimitiveType.Sphere
        };

        GameObject visual = GameObject.CreatePrimitive(primitiveType);
        visual.transform.localScale = slot.Kind switch
        {
            PickupKind.FinalObjective => new Vector3(1.2f, 1.8f, 1.2f),
            PickupKind.Equipment => new Vector3(1.1f, 0.45f, 1.1f),
            PickupKind.Functional => new Vector3(0.9f, 0.2f, 0.9f),
            _ => Vector3.one
        };

        return visual;
    }

    private void EnsureBoxVisual(int slotId, BoxSlot slot)
    {
        // Create the temporary box visual from Bangae_Statue or a cube fallback.
        if (slot.Visual != null)
        {
            return;
        }

        GameObject visual = CreateBoxVisual();
        visual.name = $"BoxItemVisual_{slotId}";
        RemoveVisualColliders(visual);
        visual.SetActive(false);
        slot.Visual = visual;
    }

    private void SetPickupBlink(PickupSlot slot, bool blinking)
    {
        // Start or stop a local renderer blink coroutine for a pickup visual.
        if (slot.BlinkRoutine != null)
        {
            StopCoroutine(slot.BlinkRoutine);
            slot.BlinkRoutine = null;
        }

        slot.Blinking = blinking;
        if (!blinking)
        {
            SetVisualRenderersVisible(slot.Visual, true);
            return;
        }

        slot.BlinkRoutine = StartCoroutine(BlinkPickupVisual(slot));
    }

    private void SetBoxBlink(BoxSlot slot, bool blinking)
    {
        // Start or stop a local renderer blink coroutine for a box visual.
        if (slot.BlinkRoutine != null)
        {
            StopCoroutine(slot.BlinkRoutine);
            slot.BlinkRoutine = null;
        }

        slot.Blinking = blinking;
        if (!blinking)
        {
            SetVisualRenderersVisible(slot.Visual, true);
            return;
        }

        slot.BlinkRoutine = StartCoroutine(BlinkBoxVisual(slot));
    }

    private IEnumerator BlinkPickupVisual(PickupSlot slot)
    {
        // Blink pickup renderers until the server stops the warning state or hides the slot.
        bool visible = true;
        while (slot.Blinking && slot.Active && slot.Visual != null)
        {
            visible = !visible;
            SetVisualRenderersVisible(slot.Visual, visible);
            yield return new WaitForSeconds(Mathf.Max(0.03f, despawnBlinkInterval));
        }

        slot.BlinkRoutine = null;
        SetVisualRenderersVisible(slot.Visual, true);
    }

    private IEnumerator BlinkBoxVisual(BoxSlot slot)
    {
        // Blink box renderers until the server stops the warning state or hides the slot.
        bool visible = true;
        while (slot.Blinking && slot.Active && slot.Visual != null)
        {
            visible = !visible;
            SetVisualRenderersVisible(slot.Visual, visible);
            yield return new WaitForSeconds(Mathf.Max(0.03f, despawnBlinkInterval));
        }

        slot.BlinkRoutine = null;
        SetVisualRenderersVisible(slot.Visual, true);
    }

    private void UpdateEquipmentLowHealthSpark(PickupSlot slot)
    {
        // Refresh the field equipment low-health spark based on the latest durability percent.
        bool shouldShow = ShouldShowEquipmentLowHealthSpark(slot);
        SetEquipmentLowHealthSpark(slot, shouldShow);
    }

    private bool ShouldShowEquipmentLowHealthSpark(PickupSlot slot)
    {
        // Show sparks only for visible field equipment that is close to being destroyed.
        return slot != null &&
            slot.Active &&
            slot.Kind == PickupKind.Equipment &&
            slot.Visual != null &&
            slot.Visual.activeInHierarchy &&
            slot.EquipmentHealthPercent > 0f &&
            slot.EquipmentHealthPercent <= Mathf.Clamp01(equipmentLowHealthSparkThreshold);
    }

    private void SetEquipmentLowHealthSpark(PickupSlot slot, bool visible)
    {
        // Start or stop the field equipment low-health spark effect.
        if (slot == null)
        {
            return;
        }

        if (!visible)
        {
            if (slot.EquipmentLowHealthSparkEffect != null && slot.EquipmentLowHealthSparkEffect.isPlaying)
            {
                slot.EquipmentLowHealthSparkEffect.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            return;
        }

        EnsureEquipmentLowHealthSparkEffect(slot);
        if (slot.EquipmentLowHealthSparkEffect != null && !slot.EquipmentLowHealthSparkEffect.isPlaying)
        {
            slot.EquipmentLowHealthSparkEffect.Play();
        }
    }

    private void EnsureEquipmentLowHealthSparkEffect(PickupSlot slot)
    {
        // Create or attach the low-health spark effect under the current equipment visual.
        if (slot == null || slot.Visual == null || slot.EquipmentLowHealthSparkEffect != null)
        {
            return;
        }

        if (equipmentLowHealthSparkPrefab != null)
        {
            slot.EquipmentLowHealthSparkEffect = Instantiate(equipmentLowHealthSparkPrefab, slot.Visual.transform);
            slot.EquipmentLowHealthSparkEffect.transform.localPosition = equipmentLowHealthSparkLocalOffset;
            slot.EquipmentLowHealthSparkEffect.transform.localRotation = Quaternion.identity;
            return;
        }

        GameObject sparkObject = new("EquipmentLowHealthRedSparkEffect");
        sparkObject.transform.SetParent(slot.Visual.transform, false);
        sparkObject.transform.localPosition = equipmentLowHealthSparkLocalOffset;

        slot.EquipmentLowHealthSparkEffect = sparkObject.AddComponent<ParticleSystem>();
        ConfigureEquipmentLowHealthSparkEffect(slot.EquipmentLowHealthSparkEffect);
    }

    private void ConfigureEquipmentLowHealthSparkEffect(ParticleSystem sparkEffect)
    {
        // Configure a temporary red spark until a dedicated field-equipment VFX prefab is assigned.
        ParticleSystem.MainModule main = sparkEffect.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.45f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3.8f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.09f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0f, 0f, 1f), new Color(1f, 0.35f, 0.08f, 1f));

        ParticleSystem.EmissionModule emission = sparkEffect.emission;
        emission.rateOverTime = Mathf.Max(0f, equipmentLowHealthSparkRate);

        ParticleSystem.ShapeModule shape = sparkEffect.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.45f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = sparkEffect.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient fadeGradient = new();
        fadeGradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0f, 0f), 0f),
                new GradientColorKey(new Color(1f, 0.35f, 0.08f), 1f)
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

    private GameObject CreateBoxVisual()
    {
        // Load the current basic box model from Resources so no network prefab is required yet.
        GameObject prefab = Resources.Load<GameObject>(basicBoxVisualResourcePath);
        GameObject visual = prefab != null ? Instantiate(prefab) : GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.transform.localScale = basicBoxVisualScale;
        return visual;
    }

    private static void RemoveVisualColliders(GameObject visual)
    {
        // Keep client-side temporary visuals from interfering with movement or server hit tests.
        Collider[] visualColliders = visual.GetComponentsInChildren<Collider>(includeInactive: true);
        for (int i = 0; i < visualColliders.Length; i++)
        {
            visualColliders[i].enabled = false;
            Destroy(visualColliders[i]);
        }
    }

    private static void SetVisualRenderersVisible(GameObject visual, bool visible)
    {
        // Toggle all renderers on a temporary visual without changing gameplay state.
        if (visual == null)
        {
            return;
        }

        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
            {
                renderers[i].enabled = visible;
            }
        }
    }

    private static void ApplyBoxVisualHealthColor(GameObject visual, float healthPercent)
    {
        // Tint the temporary box visual slightly toward red as it takes damage.
        Color color = Color.Lerp(new Color(1f, 0.35f, 0.25f), Color.white, Mathf.Clamp01(healthPercent));
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
            {
                Material material = renderers[i].material;
                if (material.HasProperty("_BaseColor"))
                {
                    material.SetColor("_BaseColor", color);
                }
                else if (material.HasProperty("_Color"))
                {
                    material.SetColor("_Color", color);
                }
            }
        }
    }

    private PickupSlot GetOrCreateSlot(int slotId)
    {
        // 슬롯 딕셔너리에 없는 아이템 슬롯은 새로 생성해 반환.
        if (slots.TryGetValue(slotId, out PickupSlot slot))
        {
            return slot;
        }

        slot = new PickupSlot
        {
            EquipmentHealthPercent = 1f
        };
        slots.Add(slotId, slot);
        return slot;
    }

    private BoxSlot GetOrCreateBoxSlot(int slotId)
    {
        // Return an existing box slot or create a new server/client visual state holder.
        if (boxSlots.TryGetValue(slotId, out BoxSlot slot))
        {
            return slot;
        }

        slot = new BoxSlot
        {
            BoxId = "basic_box",
            MaxHealth = Mathf.Max(1f, basicBoxMaxHealth),
            CurrentHealth = Mathf.Max(1f, basicBoxMaxHealth),
            LootStats = System.Array.Empty<PlayerStatType>()
        };
        boxSlots.Add(slotId, slot);
        return slot;
    }

    private Vector3 GetRandomSpawnPosition()
    {
        // Inspector 범위 안에서 아이템 스폰 위치를 무작위로 생성.
        return new Vector3(
            Random.Range(xRange.x, xRange.y),
            spawnY,
            Random.Range(zRange.x, zRange.y));
    }

    private static PlayerStatType GetRandomStatType()
    {
        // 정의된 PlayerStatType 중 하나를 무작위로 선택.
        int typeCount = System.Enum.GetValues(typeof(PlayerStatType)).Length;
        return (PlayerStatType)Random.Range(0, typeCount);
    }

    private bool ShouldSpawnFunctionalPickup()
    {
        // Decide whether a contact pickup slot should become a functional item instead of a stat item.
        return functionalPickupChance > 0f && Random.value < Mathf.Clamp01(functionalPickupChance);
    }

    private static FunctionalPickupType GetRandomFunctionalPickupType()
    {
        // Return the only functional pickup for now; this becomes the expansion point for weighted utility items.
        return FunctionalPickupType.BasicHeal;
    }

    private static Color GetPickupColor(PlayerStatType statType, PickupKind kind, string equipmentId, FunctionalPickupType functionalType)
    {
        // 임시 Primitive 아이템에 적용할 스탯별 식별 색상을 반환.
        if (kind == PickupKind.FinalObjective)
        {
            return new Color(1f, 0.85f, 0.15f);
        }

        if (kind == PickupKind.Equipment)
        {
            if (equipmentId == "balanced")
            {
                return new Color(0.25f, 1f, 0.55f);
            }

            return new Color(0.95f, 0.95f, 1f);
        }

        if (kind == PickupKind.Functional)
        {
            return functionalType switch
            {
                FunctionalPickupType.BasicHeal => new Color(1f, 0.15f, 0.4f),
                _ => Color.white
            };
        }

        return statType switch
        {
            PlayerStatType.MoveSpeed => Color.cyan,
            PlayerStatType.JumpForce => Color.green,
            PlayerStatType.Weight => Color.gray,
            PlayerStatType.Health => Color.red,
            PlayerStatType.Defense => Color.blue,
            PlayerStatType.AttackPower => new Color(1f, 0.35f, 0.1f),
            PlayerStatType.FireRate => Color.magenta,
            _ => Color.white
        };
    }
}
