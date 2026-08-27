using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class GameplayPickupManager : NetworkBehaviour
{
    private const string DefaultEquipmentSparkResourcePath = "Effects/CustomEffects/SmokeLeak_RedSparks";
    private const string DefaultStatBuffEffectResourcePath = "Effects/CustomEffects/Buff_OneShot";
    private const string DefaultHealingPickupEffectResourcePath = "Effects/CustomEffects/Healing_OneShot";
    private const string DefaultAttackUpEffectResourcePath = "Effects/CustomEffects/AttackUp";
    private const string DefaultDefenceUpEffectResourcePath = "Effects/CustomEffects/DefenceUp";
    private const string DefaultBasicHealVisualResourcePath = "ImageSource/Items/icon_medicine_bottle";
    private const string DefaultAttackPowerBuffVisualResourcePath = "ImageSource/Items/potion_atk";
    private const string DefaultDamageReductionBuffVisualResourcePath = "ImageSource/Items/potion_def";
    private const string DefaultMoveSpeedBuffVisualResourcePath = "ImageSource/Items/potion_spd";
    private const string DefaultEquipmentHitEffectResourcePath = "Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Green hit";
    private const string DefaultBoxHitEffectResourcePath = "Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Stones hit";
    private const string DefaultPenguinHitEffectResourcePath = "Effects/Hovl Studio/Magic effects pack/Prefabs/Hits and explosions/Green hit";
    private const string DefaultPenguinDisappearEffectResourcePath = "Effects/CustomEffects/Penguin_Disappear Variant";
    private const string DefaultBombBoxExplosionEffectResourcePath = "Effects/CustomEffects/Item_ExplosionA Variant";
    private const string DefaultPenguinVisualResourcePath = "Prefabs/Enemys/Penguin_enemy";
    private const string DefaultFinalMatchRuleResourcesPath = "FinalMatchRules";
    private const string DefaultFinalMatchRuleId = "break_statues";
    private const int DefaultFinalObjectiveSlotIndex = 1000;
    private const int DefaultFinalStatueBoxCount = 6;
    private const int DefaultFinalStatueBoxSlotIdBase = 9000;
    private const float DefaultFinalStatueRespawnDelay = 2f;
    private const float DefaultFinalStatueMaxHealth = 100f;
    private const string DefaultFinalStatueBoxId = "basic_stat_box";
    private static readonly PlayerStatType[] OrderedStatPickupTypes =
    {
        PlayerStatType.AttackPower,
        PlayerStatType.Defense,
        PlayerStatType.Health,
        PlayerStatType.JumpForce,
        PlayerStatType.FireRate,
        PlayerStatType.MoveSpeed,
        PlayerStatType.Weight
    };

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
        BasicHeal = 1,
        AttackPowerBuff = 2,
        DamageReductionBuff = 3,
        MoveSpeedBuff = 4,
        AutoFireBuff = 5
    }

    public enum BoxLootKind : byte
    {
        Stat = 0,
        Functional = 1,
        Equipment = 2,
        Bomb = 3
    }

    [System.Serializable]
    private class BoxVariantDefinition
    {
        public string BoxId = "basic_stat_box";
        public string DisplayName = "Basic Stat Box";
        public BoxLootKind LootKind = BoxLootKind.Stat;
        [Min(0)]
        public int LootCount = 3;
        [Min(0.01f)]
        public float MaxHealth = 100f;
        [Min(0f)]
        public float SpawnWeight = 1f;
        public Color TintColor = Color.white;
    }

    private enum PickupEffectKind : byte
    {
        StatBuff = 0,
        Healing = 1,
        AttackUp = 2,
        DefenceUp = 3
    }

    private class PickupSlot
    {
        public bool Active;
        public bool Hooked;
        public bool RespawnOnCollect;
        public bool RespawnEquipmentOnDespawn;
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
        public bool FinalScoreBox;
        public string BoxId;
        public BoxLootKind LootKind;
        public float CurrentHealth;
        public float MaxHealth;
        public PlayerStatType[] LootStats;
        public FunctionalPickupType[] LootFunctionalTypes;
        public string[] LootEquipmentIds;
        public Vector3 Position;
        public GameObject Visual;
        public Coroutine RespawnRoutine;
        public Coroutine DespawnRoutine;
        public Coroutine BlinkRoutine;
        public bool Blinking;
    }

    private class PenguinSlot
    {
        public bool Visible;
        public bool Alive;
        public float CurrentHealth;
        public float MaxHealth;
        public Vector3 Position;
        public Vector3 Forward = Vector3.forward;
        public Vector3 MoveDirection;
        public float MoveSpeed;
        public float NextDecisionTime;
        public Coroutine DeathRoutine;
        public GameObject Visual;
        public PenguinEnemyVisual VisualController;
    }

    private readonly struct SuddenEventSelection
    {
        public readonly SuddenEventType EventType;
        public readonly SuddenEventDefinition Definition;

        public SuddenEventSelection(SuddenEventType eventType, SuddenEventDefinition definition)
        {
            // Carry the selected event type and optional data asset through activation.
            EventType = eventType;
            Definition = definition;
        }
    }

    private enum HookContactKind
    {
        None,
        EquipmentDrop,
        PlayerEquipment
    }

    private readonly struct HookContact
    {
        public readonly HookContactKind Kind;
        public readonly int SlotId;
        public readonly ulong TargetClientId;
        public readonly Vector3 Point;

        public HookContact(HookContactKind kind, int slotId, ulong targetClientId, Vector3 point)
        {
            // Store the resolved hook contact so travel code can dispatch to the correct interaction.
            Kind = kind;
            SlotId = slotId;
            TargetClientId = targetClientId;
            Point = point;
        }
    }

    private interface IFinalMatchRuleHandler
    {
        FinalMatchRuleType RuleType { get; }
        void Start(FinalMatchRuleDefinition definition);
        void ResolveOnTimer();
        bool TryHandleBoxBroken(int slotId, ulong attackerClientId, BoxSlot slot);
        void Stop();
    }

    private sealed class FirstObjectivePickupFinalRuleHandler : IFinalMatchRuleHandler
    {
        private readonly GameplayPickupManager manager;

        public FirstObjectivePickupFinalRuleHandler(GameplayPickupManager manager)
        {
            this.manager = manager;
        }

        public FinalMatchRuleType RuleType => FinalMatchRuleType.FirstObjectivePickup;

        public void Start(FinalMatchRuleDefinition definition)
        {
            manager.SpawnFinalObjective(manager.ResolveFinalObjectiveSlotIndex(definition));
        }

        public void ResolveOnTimer()
        {
        }

        public bool TryHandleBoxBroken(int slotId, ulong attackerClientId, BoxSlot slot)
        {
            return false;
        }

        public void Stop()
        {
        }
    }

    private sealed class BreakStatuesFinalRuleHandler : IFinalMatchRuleHandler
    {
        private readonly GameplayPickupManager manager;
        private FinalMatchRuleDefinition activeDefinition;

        public BreakStatuesFinalRuleHandler(GameplayPickupManager manager)
        {
            this.manager = manager;
        }

        public FinalMatchRuleType RuleType => FinalMatchRuleType.BreakStatues;

        public void Start(FinalMatchRuleDefinition definition)
        {
            activeDefinition = definition;
            manager.ResetFinalStatueBreakScores();
            manager.SpawnFinalStatueBreakBoxes(activeDefinition);
        }

        public void ResolveOnTimer()
        {
            manager.CompleteFinalStatueBreakObjective(activeDefinition);
        }

        public bool TryHandleBoxBroken(int slotId, ulong attackerClientId, BoxSlot slot)
        {
            if (slot == null || !slot.FinalScoreBox)
            {
                return false;
            }

            manager.BreakFinalStatueBox(slotId, attackerClientId, activeDefinition);
            return true;
        }

        public void Stop()
        {
            activeDefinition = null;
        }
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
    [SerializeField] private float attackPowerBuffDuration = 10f;
    [SerializeField] private float attackPowerBuffMultiplier = 2f;
    [SerializeField] private float damageReductionBuffDuration = 10f;
    [Range(0.01f, 1f)]
    [SerializeField] private float damageReductionTakenMultiplier = 0.5f;
    [SerializeField] private float moveSpeedBuffDuration = 10f;
    [SerializeField] private float moveSpeedBuffMultiplier = 2f;
    [SerializeField] private float autoFireBuffDuration = 10f;
    [SerializeField] private FunctionalPickupType[] functionalPickupPool =
    {
        FunctionalPickupType.BasicHeal,
        FunctionalPickupType.AttackPowerBuff,
        FunctionalPickupType.DamageReductionBuff,
        FunctionalPickupType.MoveSpeedBuff,
        FunctionalPickupType.AutoFireBuff
    };

    [Header("Functional Pickup Visual")]
    [SerializeField] private Sprite basicHealVisualSprite;
    [SerializeField] private string basicHealVisualResourcePath = DefaultBasicHealVisualResourcePath;
    [Min(0.01f)]
    [SerializeField] private float basicHealVisualWorldHeight = 1.1f;
    [SerializeField] private Vector3 basicHealVisualLocalOffset = new(0f, 0.05f, 0f);
    [SerializeField] private Sprite attackPowerBuffVisualSprite;
    [SerializeField] private string attackPowerBuffVisualResourcePath = DefaultAttackPowerBuffVisualResourcePath;
    [SerializeField] private Sprite damageReductionBuffVisualSprite;
    [SerializeField] private string damageReductionBuffVisualResourcePath = DefaultDamageReductionBuffVisualResourcePath;
    [SerializeField] private Sprite moveSpeedBuffVisualSprite;
    [SerializeField] private string moveSpeedBuffVisualResourcePath = DefaultMoveSpeedBuffVisualResourcePath;
    [Min(0.01f)]
    [SerializeField] private float buffPotionVisualWorldHeight = 1.1f;
    [SerializeField] private Vector3 buffPotionVisualLocalOffset = new(0f, 0.05f, 0f);
    [SerializeField] private int functionalPickupSpriteSortingOrder;

    [Header("Sudden Events")]
    [SerializeField] private bool suddenEventsEnabled = true;
    [SerializeField] private float suddenEventStartDelay = 10f;
    [SerializeField] private float suddenEventDuration = 20f;
    [SerializeField] private bool loadSuddenEventDefinitionsFromResources = true;
    [SerializeField] private SuddenEventDefinition[] suddenEventDefinitions;
    [SerializeField] private SuddenEventType[] fallbackSuddenEventTypes =
    {
        SuddenEventType.EndlessAutoFire,
        SuddenEventType.MixedStatueLoot,
        SuddenEventType.PenguinFeast
    };

    [Header("Penguin Sudden Event")]
    [SerializeField] private string penguinVisualResourcePath = DefaultPenguinVisualResourcePath;
    [SerializeField] private int penguinSlotIdBase = 7000;
    [SerializeField] private int fallbackPenguinSpawnCount = 5;
    [SerializeField] private float fallbackPenguinStatueHealthMultiplier = 1.5f;
    [SerializeField] private int fallbackPenguinStatLootCount = 8;
    [SerializeField] private Vector2 fallbackPenguinMoveSpeedRange = new(1.2f, 2.2f);
    [SerializeField] private Vector2 fallbackPenguinDecisionDurationRange = new(1.2f, 3.5f);
    [Range(0f, 1f)]
    [SerializeField] private float fallbackPenguinIdleChance = 0.35f;
    [SerializeField] private float penguinHitRadius = 0.7f;
    [SerializeField] private float penguinTargetHeight = 0.7f;
    [SerializeField] private float penguinBoundsPadding = 1f;
    [SerializeField] private LayerMask penguinGroundMask = ~0;
    [SerializeField] private float penguinGroundRaycastHeight = 3f;
    [SerializeField] private float penguinGroundRaycastDistance = 6f;
    [SerializeField] private float penguinGroundOffset;
    [SerializeField] private float penguinTurnSpeed = 360f;
    [SerializeField] private float penguinNetworkSyncInterval = 0.1f;
    [SerializeField] private float penguinVisualPositionSharpness = 14f;
    [SerializeField] private Vector3 penguinVisualScale = new(0.25f, 0.25f, 0.25f);
    [SerializeField] private float penguinDeathDuration = 2f;

    [Header("Penguin Hit Effect")]
    [SerializeField] private GameObject penguinHitEffectPrefab;
    [SerializeField] private Vector3 penguinHitEffectEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float penguinHitEffectScale = 1f;
    [SerializeField] private float penguinHitEffectLifetime = 2f;

    [Header("Penguin Disappear Effect")]
    [SerializeField] private GameObject penguinDisappearEffectPrefab;
    [SerializeField] private Vector3 penguinDisappearEffectOffset = new(0f, 0.7f, 0f);
    [SerializeField] private Vector3 penguinDisappearEffectEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float penguinDisappearEffectScale = 1f;
    [SerializeField] private float penguinDisappearEffectLifetime = 3f;

    [Header("Stat Pickup Visual")]
    [SerializeField] private string statPickupVisualResourcePath = "fbx/Stat_Item/Stat_Item";
    [SerializeField] private string statPickupTextureResourceRoot = "fbx/Stat_Item";
    [SerializeField] private string statPickupTextureMaterialName = "Image";
    [SerializeField] private Vector3 statPickupVisualScale = Vector3.one;
    [SerializeField] private bool normalizeStatPickupVisualBounds = true;
    [SerializeField] private float statPickupVisualTargetHeight = 1f;
    [SerializeField] private bool statPickupTextureAlsoEmission = true;
    [SerializeField] private Color statPickupEmissionColor = Color.white;
    [SerializeField] private bool ensureAllStatTypesOnMainSpawn = true;

    [Header("Pickup Effects")]
    [SerializeField] private GameObject statBuffEffectPrefab;
    [SerializeField] private GameObject healingPickupEffectPrefab;
    [SerializeField] private GameObject attackUpEffectPrefab;
    [SerializeField] private GameObject defenceUpEffectPrefab;
    [SerializeField] private Vector3 pickupEffectWorldOffset = new(0f, 1f, 0f);
    [SerializeField] private Vector3 pickupEffectEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float pickupEffectScale = 1f;
    [SerializeField] private float pickupEffectLifetime = 2f;

    [Header("Box Items")]
    [SerializeField] private int boxItemCount = 3;
    [SerializeField] private int boxSlotIdBase = 4000;
    [SerializeField] private float boxRespawnDelay = 15f;
    [SerializeField] private float boxHitRadius = 2.4f;
    [SerializeField] private float boxTargetHeight = 2.4f;
    [SerializeField] private float boxLootScatterRadius = 1.4f;
    [SerializeField] private string basicBoxVisualResourcePath = "fbx/Bangae_Statue";
    [SerializeField] private Vector3 basicBoxVisualScale = new(2f, 2f, 2f);

    [Header("Box Collision")]
    [SerializeField] private bool boxBlocksPlayers = true;
    [SerializeField] private Vector3 basicBoxColliderSizeMultiplier = Vector3.one;
    [SerializeField] private Vector3 basicBoxColliderCenterOffset;

    [SerializeField] private string basicBoxCleanTextureResourcePath = "fbx/BgStatue_Clean";
    [SerializeField] private string basicBoxHalfBreakTextureResourcePath = "fbx/BgStatue_Half-break";
    [SerializeField] private string basicBoxFullBreakTextureResourcePath = "fbx/BgStatue_Full-break";
    [SerializeField] private string basicBoxTextureExcludedMaterialName = "Rock_Eye";
    [Range(0f, 1f)]
    [SerializeField] private float basicBoxHalfBreakThreshold = 0.66f;
    [Range(0f, 1f)]
    [SerializeField] private float basicBoxFullBreakThreshold = 0.33f;
    [SerializeField] private float basicBoxMaxHealth = 100f;
    [SerializeField] private int basicBoxLootCount = 3;
    [SerializeField] private LayerMask boxGroundMask = ~0;
    [SerializeField] private float boxGroundOffset = 0f;
    [SerializeField] private float boxGroundRaycastHeight = 4f;
    [SerializeField] private float boxGroundRaycastDistance = 10f;
    [SerializeField] private BoxVariantDefinition[] boxVariants =
    {
        new()
        {
            BoxId = "basic_stat_box",
            DisplayName = "Basic Stat Box",
            LootKind = BoxLootKind.Stat,
            LootCount = 3,
            MaxHealth = 100f,
            SpawnWeight = 1f,
            TintColor = Color.white
        },
        new()
        {
            BoxId = "heal_box",
            DisplayName = "Heal Box",
            LootKind = BoxLootKind.Functional,
            LootCount = 2,
            MaxHealth = 80f,
            SpawnWeight = 0.45f,
            TintColor = new Color(1f, 0.35f, 0.55f)
        },
        new()
        {
            BoxId = "equipment_box",
            DisplayName = "Equipment Box",
            LootKind = BoxLootKind.Equipment,
            LootCount = 1,
            MaxHealth = 140f,
            SpawnWeight = 0.25f,
            TintColor = new Color(0.25f, 0.8f, 1f)
        },
        new()
        {
            BoxId = "bomb_box",
            DisplayName = "Bomb Box",
            LootKind = BoxLootKind.Bomb,
            LootCount = 0,
            MaxHealth = 50f,
            SpawnWeight = 0.25f,
            TintColor = new Color(0.04f, 0.04f, 0.04f)
        }
    };

    [Header("Box Hit Effect")]
    [SerializeField] private GameObject boxHitEffectPrefab;
    [SerializeField] private Vector3 boxHitEffectEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float boxHitEffectScale = 1f;
    [SerializeField] private float boxHitEffectLifetime = 2f;

    [Header("Box Sound Effects")]
    [SerializeField] private AudioClip boxHitSfxClip;
    [SerializeField] private AudioClip boxBreakSfxClip;
    [Range(0f, 1f)]
    [SerializeField] private float boxHitSfxVolumeScale = 1f;
    [Range(0f, 1f)]
    [SerializeField] private float boxBreakSfxVolumeScale = 1f;

    [Header("Bomb Box")]
    [SerializeField] private float bombBoxExplosionRadius = 2f;
    [SerializeField] private float bombBoxExplosionDamage = 100f;
    [Range(0f, 1f)]
    [SerializeField] private float bombBoxExplosionMinimumDamageMultiplier = 0.4f;
    [Range(0f, 1f)]
    [SerializeField] private float bombBoxExplosionSelfDamageMultiplier = 1f;
    [SerializeField] private GameObject bombBoxExplosionEffectPrefab;
    [SerializeField] private string bombBoxExplosionEffectResourcePath = DefaultBombBoxExplosionEffectResourcePath;
    [SerializeField] private Vector3 bombBoxExplosionEffectEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float bombBoxExplosionEffectScale = 1f;
    [SerializeField] private float bombBoxExplosionEffectLifetime = 3f;

    [Header("Final Match Objective")]
    [SerializeField] private FinalMatchRuleDefinition finalMatchRuleDefinition;
    [SerializeField] private string selectedFinalMatchRuleId = DefaultFinalMatchRuleId;
    [SerializeField] private bool loadFinalMatchRuleDefinitionsFromResources = true;
    [SerializeField] private string finalMatchRuleResourcesPath = DefaultFinalMatchRuleResourcesPath;

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

    [Header("Field Equipment Hit Effect")]
    [SerializeField] private GameObject equipmentHitEffectPrefab;
    [SerializeField] private Vector3 equipmentHitEffectEulerOffset;
    [Min(0.01f)]
    [SerializeField] private float equipmentHitEffectScale = 1f;
    [SerializeField] private float equipmentHitEffectLifetime = 2f;

    [Header("Equipment Low Health Effect")]
    [SerializeField] private ParticleSystem equipmentLowHealthSparkPrefab;
    [Range(0f, 1f)]
    [SerializeField] private float equipmentLowHealthSparkThreshold = 0.2f;
    [SerializeField] private Vector3 equipmentLowHealthSparkLocalOffset = new(0f, 0.35f, 0f);
    [SerializeField] private Vector3 equipmentLowHealthSparkLocalEulerAngles = new(-20f, 180f, 0f);
    [Min(0.01f)]
    [SerializeField] private float equipmentLowHealthSparkScale = 2f;
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
    [SerializeField] private float hookRange = 30f;
    [SerializeField] private float hookSelectRadius = 0.75f;
    [Min(1f)]
    [Tooltip("Controls both the temporary hook visual speed and the equipment pull speed.")]
    [SerializeField] private float hookPullSpeed = 70f;
    [SerializeField] private float hookEquipRadius = 1.1f;
    [SerializeField] private float hookServerCooldown = 0.5f;
    [SerializeField] private float hookOriginTolerance = 4f;
    [SerializeField] private float hookPlayerStealRadius = 1.2f;
    [SerializeField] private float hookPlayerStealTargetHeight = 1f;
    [Range(0f, 1f)]
    [SerializeField] private float hookStealHealPercent = 0.25f;

    private readonly Dictionary<int, PickupSlot> slots = new();
    private readonly Dictionary<int, BoxSlot> boxSlots = new();
    private readonly Dictionary<int, PenguinSlot> penguinSlots = new();
    private readonly Dictionary<ulong, int> finalStatueBreakScores = new();
    private readonly Dictionary<ulong, float> nextHookRequestTimes = new();
    private PlayerStatsState statsState;
    private MatchStateController matchStateController;
    private float nextScanTime;
    private float nextLocalRequestTime;
    private int nextHookVisualId;
    private int nextLootPickupSlotId;
    private Coroutine suddenEventRoutine;
    private SuddenEventType activeSuddenEvent = SuddenEventType.None;
    private PenguinSuddenEventDefinition activePenguinEventDefinition;
    private FinalMatchRuleDefinition activeFinalMatchRuleDefinition;
    private IFinalMatchRuleHandler activeFinalMatchRuleHandler;
    private float activeSuddenEventEndTime;
    private float nextPenguinNetworkSyncTime;
    private ParticleSystem resolvedDefaultEquipmentSparkPrefab;
    private GameObject resolvedDefaultStatBuffEffectPrefab;
    private GameObject resolvedDefaultHealingPickupEffectPrefab;
    private GameObject resolvedDefaultAttackUpEffectPrefab;
    private GameObject resolvedDefaultDefenceUpEffectPrefab;
    private GameObject resolvedDefaultEquipmentHitEffectPrefab;
    private GameObject resolvedDefaultBoxHitEffectPrefab;
    private GameObject resolvedDefaultPenguinHitEffectPrefab;
    private GameObject resolvedDefaultPenguinDisappearEffectPrefab;
    private GameObject resolvedDefaultBombBoxExplosionEffectPrefab;
    private GameObject resolvedPenguinVisualPrefab;
    private Sprite resolvedBasicHealVisualSprite;
    private Sprite resolvedAttackPowerBuffVisualSprite;
    private Sprite resolvedDamageReductionBuffVisualSprite;
    private Sprite resolvedMoveSpeedBuffVisualSprite;
    private Texture2D resolvedBasicBoxCleanTexture;
    private Texture2D resolvedBasicBoxHalfBreakTexture;
    private Texture2D resolvedBasicBoxFullBreakTexture;
    private GameObject resolvedStatPickupVisualPrefab;
    private readonly Dictionary<PlayerStatType, Texture2D> resolvedStatPickupTextures = new();
    private readonly HashSet<PlayerStatType> triedLoadStatPickupTextures = new();
    private bool triedLoadDefaultEquipmentSparkPrefab;
    private bool triedLoadDefaultStatBuffEffectPrefab;
    private bool triedLoadDefaultHealingPickupEffectPrefab;
    private bool triedLoadDefaultAttackUpEffectPrefab;
    private bool triedLoadDefaultDefenceUpEffectPrefab;
    private bool triedLoadDefaultEquipmentHitEffectPrefab;
    private bool triedLoadDefaultBoxHitEffectPrefab;
    private bool triedLoadDefaultPenguinHitEffectPrefab;
    private bool triedLoadDefaultPenguinDisappearEffectPrefab;
    private bool triedLoadDefaultBombBoxExplosionEffectPrefab;
    private bool triedLoadPenguinVisualPrefab;
    private bool triedLoadBasicHealVisualSprite;
    private bool triedLoadAttackPowerBuffVisualSprite;
    private bool triedLoadDamageReductionBuffVisualSprite;
    private bool triedLoadMoveSpeedBuffVisualSprite;
    private bool triedLoadBasicBoxTextures;
    private bool triedLoadStatPickupVisualPrefab;
    private bool warnedMissingStatPickupMaterial;

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
        StopSuddenEventSchedule();

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
        if (IsServer)
        {
            UpdateActiveSuddenEvent();
            UpdatePenguinEvent(Time.deltaTime);
        }

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
                StopSuddenEventSchedule();
                StopFinalMatchObjective();
                ClearAllPickups();
                ClearAllBoxItems();
                ResetFinalStatueBreakScores();
                statsState?.ResetStats();
                break;
            case NetworkMatchState.MatchMain:
                StopSuddenEventSchedule();
                StopFinalMatchObjective();
                ClearAllPickups();
                ClearAllBoxItems();
                ResetFinalStatueBreakScores();
                NetworkPlayerEquipmentState.EquipDefaultForAll();
                NetworkPlayerCombatState.ResetForMatchStartForAll();
                statsState?.ResetStats();
                SpawnMainMatchPickups();
                SpawnEquipmentPickups();
                SpawnBoxItems();
                StartSuddenEventSchedule();
                break;
            case NetworkMatchState.FinalTransition:
                StopSuddenEventSchedule();
                StopFinalMatchObjective();
                ClearAllPickups();
                ClearAllBoxItems();
                break;
            case NetworkMatchState.FinalMatch:
                StopSuddenEventSchedule();
                StopFinalMatchObjective();
                ClearAllPickups();
                ClearAllBoxItems();
                NetworkPlayerEquipmentState.EquipDefaultForUnequippedAll();
                NetworkPlayerCombatState.ResetForMatchStartForAll();
                StartFinalMatchObjective();
                break;
            case NetworkMatchState.Result:
                StopSuddenEventSchedule();
                StopFinalMatchObjective();
                ClearAllPickups();
                ClearAllBoxItems();
                break;
        }
    }

    private void StartSuddenEventSchedule()
    {
        // Start the one-shot sudden event timer for the current main match.
        StopSuddenEventSchedule();
        if (!suddenEventsEnabled)
        {
            return;
        }

        suddenEventRoutine = StartCoroutine(TriggerSuddenEventAfterDelay(Mathf.Max(0f, suddenEventStartDelay)));
    }

    private void StopSuddenEventSchedule()
    {
        // Stop pending or active sudden event state when the main match ends or the manager despawns.
        if (suddenEventRoutine != null)
        {
            StopCoroutine(suddenEventRoutine);
            suddenEventRoutine = null;
        }

        bool shouldLetMatchStateFadeAudio = matchStateController != null &&
            matchStateController.State.Value == NetworkMatchState.FinalTransition;
        if (IsServer && activeSuddenEvent != SuddenEventType.None && !shouldLetMatchStateFadeAudio)
        {
            StopSuddenEventBgmClientRpc(revealBaseBgm: false);
        }

        StopPenguinEvent();
        activeSuddenEvent = SuddenEventType.None;
        activeSuddenEventEndTime = 0f;
    }

    private IEnumerator TriggerSuddenEventAfterDelay(float delay)
    {
        // Wait from main-match start, then activate one random enabled sudden event.
        if (delay > 0f)
        {
            yield return new WaitForSeconds(delay);
        }

        suddenEventRoutine = null;
        if (!ShouldTriggerSuddenEvent())
        {
            yield break;
        }

        ActivateSuddenEvent(ChooseRandomSuddenEvent());
    }

    private bool ShouldTriggerSuddenEvent()
    {
        // Confirm sudden events are still enabled and the game is still in the main match.
        return IsServer &&
            suddenEventsEnabled &&
            matchStateController != null &&
            matchStateController.State.Value == NetworkMatchState.MatchMain;
    }

    private SuddenEventSelection ChooseRandomSuddenEvent()
    {
        // Choose a weighted event definition first, then fall back to simple event ids.
        List<SuddenEventDefinition> definitions = ResolveSuddenEventDefinitions();
        if (HasAnySuddenEventDefinition(definitions))
        {
            return TryChooseDefinitionSuddenEvent(definitions, out SuddenEventSelection definitionSelection)
                ? definitionSelection
                : new SuddenEventSelection(SuddenEventType.None, null);
        }

        return ChooseFallbackSuddenEvent();
    }

    private bool TryChooseDefinitionSuddenEvent(List<SuddenEventDefinition> definitions, out SuddenEventSelection selection)
    {
        // Choose from ScriptableObject definitions so larger event lists can be edited as data assets.
        selection = default;
        float totalWeight = 0f;
        for (int i = 0; i < definitions.Count; i++)
        {
            SuddenEventDefinition definition = definitions[i];
            if (IsUsableSuddenEventDefinition(definition))
            {
                totalWeight += Mathf.Max(0f, definition.Weight);
            }
        }

        if (totalWeight <= 0f)
        {
            return false;
        }

        float roll = Random.Range(0f, totalWeight);
        for (int i = 0; i < definitions.Count; i++)
        {
            SuddenEventDefinition definition = definitions[i];
            if (!IsUsableSuddenEventDefinition(definition))
            {
                continue;
            }

            roll -= Mathf.Max(0f, definition.Weight);
            if (roll <= 0f)
            {
                selection = new SuddenEventSelection(definition.EventType, definition);
                return true;
            }
        }

        return false;
    }

    private static bool HasAnySuddenEventDefinition(List<SuddenEventDefinition> definitions)
    {
        // Treat assigned or resource-loaded definitions as the authoritative event list.
        if (definitions == null)
        {
            return false;
        }

        for (int i = 0; i < definitions.Count; i++)
        {
            if (definitions[i] != null)
            {
                return true;
            }
        }

        return false;
    }

    private List<SuddenEventDefinition> ResolveSuddenEventDefinitions()
    {
        // Combine inspector-assigned definitions and optional Resources/SuddenEvents assets.
        List<SuddenEventDefinition> definitions = new();
        if (suddenEventDefinitions != null)
        {
            definitions.AddRange(suddenEventDefinitions);
        }

        if (loadSuddenEventDefinitionsFromResources)
        {
            definitions.AddRange(Resources.LoadAll<SuddenEventDefinition>("SuddenEvents"));
        }

        return definitions;
    }

    private static bool IsUsableSuddenEventDefinition(SuddenEventDefinition definition)
    {
        // Accept enabled definitions with a known event type and positive random weight.
        return definition != null &&
            definition.EnabledInPool &&
            definition.EventType != SuddenEventType.None &&
            definition.Weight > 0f;
    }

    private SuddenEventSelection ChooseFallbackSuddenEvent()
    {
        // Preserve current behavior when no ScriptableObject event definitions exist yet.
        if (fallbackSuddenEventTypes == null || fallbackSuddenEventTypes.Length == 0)
        {
            return new SuddenEventSelection(SuddenEventType.EndlessAutoFire, null);
        }

        for (int attempts = 0; attempts < fallbackSuddenEventTypes.Length; attempts++)
        {
            SuddenEventType candidate = fallbackSuddenEventTypes[Random.Range(0, fallbackSuddenEventTypes.Length)];
            if (candidate != SuddenEventType.None)
            {
                return new SuddenEventSelection(candidate, null);
            }
        }

        return new SuddenEventSelection(SuddenEventType.EndlessAutoFire, null);
    }

    private void ActivateSuddenEvent(SuddenEventSelection selection)
    {
        // Activate the selected event for the configured duration and notify all players.
        SuddenEventType eventKind = selection.EventType;
        if (eventKind == SuddenEventType.None)
        {
            return;
        }

        float duration = ResolveSuddenEventDuration(selection.Definition);
        if (duration <= 0f)
        {
            return;
        }

        activeSuddenEvent = eventKind;
        activeSuddenEventEndTime = Time.time + duration;

        if (eventKind == SuddenEventType.PenguinFeast)
        {
            StartPenguinEvent(selection.Definition as PenguinSuddenEventDefinition);
        }

        string title = ResolveSuddenEventTitle(selection);
        matchStateController?.ShowNoticeToAll(title, 4f);
        PlaySuddenEventWarningClientRpc();
        PlaySuddenEventBgmClientRpc(eventKind);
        Debug.Log($"[GameplayPickupManager] Sudden event started kind={eventKind} duration={duration:0.0}s title={title}");
        UpdateActiveSuddenEvent();
    }

    private float ResolveSuddenEventDuration(SuddenEventDefinition definition)
    {
        // Keep sudden events inside the remaining main-match window.
        float configuredDuration = Mathf.Max(0f, definition != null
            ? definition.ResolveDuration(suddenEventDuration)
            : suddenEventDuration);
        if (matchStateController == null)
        {
            return configuredDuration;
        }

        float remainingMainTime = Mathf.Max(0f, matchStateController.RemainingTime.Value);
        return remainingMainTime > 0f ? Mathf.Min(configuredDuration, remainingMainTime) : configuredDuration;
    }

    private static string ResolveSuddenEventTitle(SuddenEventSelection selection)
    {
        // Convert event ids into temporary player-facing notice titles.
        if (selection.Definition != null && !string.IsNullOrWhiteSpace(selection.Definition.Title))
        {
            return selection.Definition.Title;
        }

        return selection.EventType switch
        {
            SuddenEventType.EndlessAutoFire => "자동 발사가 멈춰지지 않는다!",
            SuddenEventType.MixedStatueLoot => "석상의 내용물이 제각각이다!",
            SuddenEventType.PenguinFeast => "펭귄들이 아이템을 먹고 뚱뚱해졌다!",
            _ => "돌발 이벤트 발생!"
        };
    }

    private void UpdateActiveSuddenEvent()
    {
        // Tick active sudden events and apply any repeating effects they need.
        if (activeSuddenEvent == SuddenEventType.None)
        {
            return;
        }

        if (!IsSuddenEventActive())
        {
            ClearActiveSuddenEvent();
            return;
        }

        if (activeSuddenEvent == SuddenEventType.EndlessAutoFire)
        {
            NetworkPlayerCombatState.ApplyAutoFireBuffUntilForAll(activeSuddenEventEndTime, "sudden-event");
        }
    }

    private bool IsSuddenEventActive()
    {
        // Check whether the current sudden event should still affect main-match gameplay.
        return IsServer &&
            suddenEventsEnabled &&
            matchStateController != null &&
            matchStateController.State.Value == NetworkMatchState.MatchMain &&
            Time.time < activeSuddenEventEndTime;
    }

    private bool IsMixedBoxLootSuddenEventActive()
    {
        // Mixed statue loot only affects boxes while that sudden event is active.
        return activeSuddenEvent == SuddenEventType.MixedStatueLoot && IsSuddenEventActive();
    }

    private void ClearActiveSuddenEvent()
    {
        // Clear local event state once the event expires or the main match ends.
        if (activeSuddenEvent != SuddenEventType.None)
        {
            Debug.Log($"[GameplayPickupManager] Sudden event ended kind={activeSuddenEvent}");
            bool revealBaseBgm = matchStateController != null &&
                matchStateController.State.Value == NetworkMatchState.MatchMain;
            StopSuddenEventBgmClientRpc(revealBaseBgm);
        }

        StopPenguinEvent();
        activeSuddenEvent = SuddenEventType.None;
        activeSuddenEventEndTime = 0f;
    }

    private void StartPenguinEvent(PenguinSuddenEventDefinition definition)
    {
        // Spawn one server-authoritative group using editable values from the selected event asset.
        StopPenguinEvent();
        activePenguinEventDefinition = definition;
        int spawnCount = ResolvePenguinSpawnCount();
        float maxHealth = ResolvePenguinMaxHealth();

        for (int i = 0; i < spawnCount; i++)
        {
            int slotId = penguinSlotIdBase + i;
            PenguinSlot slot = GetOrCreatePenguinSlot(slotId);
            slot.Visible = true;
            slot.Alive = true;
            slot.MaxHealth = maxHealth;
            slot.CurrentHealth = maxHealth;
            slot.Position = GetRandomPenguinSpawnPosition();
            slot.Forward = GetRandomHorizontalDirection();
            ChooseNextPenguinMovement(slot);
            SendPenguinVisualState(slotId, slot, snap: true, playDeath: false);
        }

        nextPenguinNetworkSyncTime = Time.time + Mathf.Max(0.02f, penguinNetworkSyncInterval);
        Debug.Log($"[GameplayPickupManager] Penguin event spawned count={spawnCount} maxHealth={maxHealth:0.0} lootEach={ResolvePenguinStatLootCount()}");
    }

    private void StopPenguinEvent()
    {
        // Cancel death timers and hide every pooled Penguin when its event or the main match ends.
        activePenguinEventDefinition = null;
        nextPenguinNetworkSyncTime = 0f;
        foreach (KeyValuePair<int, PenguinSlot> pair in penguinSlots)
        {
            PenguinSlot slot = pair.Value;
            if (slot.DeathRoutine != null)
            {
                StopCoroutine(slot.DeathRoutine);
                slot.DeathRoutine = null;
            }

            slot.Visible = false;
            slot.Alive = false;
            slot.MoveDirection = Vector3.zero;
            slot.MoveSpeed = 0f;
            slot.VisualController?.Hide();
            if (IsServer && IsSpawned)
            {
                SendPenguinVisualState(pair.Key, slot, snap: true, playDeath: false);
            }
        }
    }

    private void UpdatePenguinEvent(float deltaTime)
    {
        // Advance simple random roaming on the server and periodically replicate compact transform samples.
        if (activeSuddenEvent != SuddenEventType.PenguinFeast || !IsSuddenEventActive())
        {
            return;
        }

        bool shouldSync = Time.time >= nextPenguinNetworkSyncTime;
        foreach (KeyValuePair<int, PenguinSlot> pair in penguinSlots)
        {
            PenguinSlot slot = pair.Value;
            if (!slot.Visible || !slot.Alive)
            {
                continue;
            }

            UpdatePenguinMovement(slot, Mathf.Max(0f, deltaTime));
            if (shouldSync)
            {
                SendPenguinVisualState(pair.Key, slot, snap: false, playDeath: false);
            }
        }

        if (shouldSync)
        {
            nextPenguinNetworkSyncTime = Time.time + Mathf.Max(0.02f, penguinNetworkSyncInterval);
        }
    }

    private void UpdatePenguinMovement(PenguinSlot slot, float deltaTime)
    {
        // Move one Penguin without pathfinding while rejecting steps outside the arena or above missing ground.
        if (slot == null || !slot.Alive)
        {
            return;
        }

        if (Time.time >= slot.NextDecisionTime)
        {
            ChooseNextPenguinMovement(slot);
        }

        if (slot.MoveDirection.sqrMagnitude <= 0.0001f || slot.MoveSpeed <= 0f || deltaTime <= 0f)
        {
            return;
        }

        Quaternion currentRotation = Quaternion.LookRotation(slot.Forward.sqrMagnitude > 0.0001f ? slot.Forward : slot.MoveDirection, Vector3.up);
        Quaternion desiredRotation = Quaternion.LookRotation(slot.MoveDirection, Vector3.up);
        Quaternion nextRotation = Quaternion.RotateTowards(currentRotation, desiredRotation, Mathf.Max(0f, penguinTurnSpeed) * deltaTime);
        Vector3 nextForward = nextRotation * Vector3.forward;
        Vector3 candidate = slot.Position + nextForward * slot.MoveSpeed * deltaTime;

        if (!IsInsidePenguinBounds(candidate) || !TryResolvePenguinGroundPosition(candidate, out Vector3 groundedPosition))
        {
            RedirectPenguinTowardArena(slot);
            return;
        }

        slot.Forward = nextForward.normalized;
        slot.Position = groundedPosition;
    }

    private void ChooseNextPenguinMovement(PenguinSlot slot)
    {
        // Randomly alternate between idle pauses and uncomplicated horizontal wandering.
        if (slot == null)
        {
            return;
        }

        Vector2 durationRange = ResolvePenguinDecisionDurationRange();
        slot.NextDecisionTime = Time.time + Random.Range(durationRange.x, durationRange.y);
        if (Random.value < ResolvePenguinIdleChance())
        {
            slot.MoveDirection = Vector3.zero;
            slot.MoveSpeed = 0f;
            return;
        }

        slot.MoveDirection = GetRandomHorizontalDirection();
        Vector2 speedRange = ResolvePenguinMoveSpeedRange();
        slot.MoveSpeed = Random.Range(speedRange.x, speedRange.y);
    }

    private void RedirectPenguinTowardArena(PenguinSlot slot)
    {
        // Turn an unsafe wander step back toward the configured spawn-area center.
        Vector3 center = new((xRange.x + xRange.y) * 0.5f, slot.Position.y, (zRange.x + zRange.y) * 0.5f);
        Vector3 inward = center - slot.Position;
        inward.y = 0f;
        slot.MoveDirection = inward.sqrMagnitude > 0.0001f ? inward.normalized : -slot.Forward;
        slot.NextDecisionTime = Time.time + 0.5f;
    }

    private Vector3 GetRandomPenguinSpawnPosition()
    {
        // Find a random in-bounds point with ground beneath it, retaining a static fallback for sparse test scenes.
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Vector3 candidate = GetRandomSpawnPosition();
            if (TryResolvePenguinGroundPosition(candidate, out Vector3 groundedPosition))
            {
                return groundedPosition;
            }
        }

        return ResolveBoxGroundedPosition(GetRandomSpawnPosition());
    }

    private bool TryResolvePenguinGroundPosition(Vector3 position, out Vector3 groundedPosition)
    {
        // Raycast downward before accepting a step so Penguins never walk over unsupported arena edges.
        groundedPosition = position;
        if (!IsInsidePenguinBounds(position))
        {
            return false;
        }

        Vector3 rayOrigin = position + Vector3.up * Mathf.Max(0f, penguinGroundRaycastHeight);
        float rayDistance = Mathf.Max(0.1f, penguinGroundRaycastHeight + penguinGroundRaycastDistance);
        if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, rayDistance, penguinGroundMask, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        groundedPosition.y = hit.point.y + penguinGroundOffset;
        return true;
    }

    private bool IsInsidePenguinBounds(Vector3 position)
    {
        // Keep wandering inside a padded version of the same rectangle used by field-item spawns.
        ResolvePenguinBounds(out float minX, out float maxX, out float minZ, out float maxZ);
        return position.x >= minX && position.x <= maxX && position.z >= minZ && position.z <= maxZ;
    }

    private void ResolvePenguinBounds(out float minX, out float maxX, out float minZ, out float maxZ)
    {
        // Normalize serialized ranges and cap padding so even very small test arenas remain usable.
        float rawMinX = Mathf.Min(xRange.x, xRange.y);
        float rawMaxX = Mathf.Max(xRange.x, xRange.y);
        float rawMinZ = Mathf.Min(zRange.x, zRange.y);
        float rawMaxZ = Mathf.Max(zRange.x, zRange.y);
        float xPadding = Mathf.Min(Mathf.Max(0f, penguinBoundsPadding), (rawMaxX - rawMinX) * 0.45f);
        float zPadding = Mathf.Min(Mathf.Max(0f, penguinBoundsPadding), (rawMaxZ - rawMinZ) * 0.45f);
        minX = rawMinX + xPadding;
        maxX = rawMaxX - xPadding;
        minZ = rawMinZ + zPadding;
        maxZ = rawMaxZ - zPadding;
    }

    private static Vector3 GetRandomHorizontalDirection()
    {
        // Generate one uniformly distributed horizontal travel direction.
        float angle = Random.Range(0f, Mathf.PI * 2f);
        return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
    }

    private int ResolvePenguinSpawnCount()
    {
        // Prefer event-asset spawn tuning and retain a safe manager fallback.
        return activePenguinEventDefinition != null
            ? activePenguinEventDefinition.SpawnCount
            : Mathf.Max(1, fallbackPenguinSpawnCount);
    }

    private float ResolvePenguinMaxHealth()
    {
        // Define Penguin durability as the basic statue's health multiplied by the event setting.
        float statueHealth = Mathf.Max(1f, ResolveBoxVariant("basic_stat_box").MaxHealth);
        float multiplier = activePenguinEventDefinition != null
            ? activePenguinEventDefinition.StatueHealthMultiplier
            : Mathf.Max(0.1f, fallbackPenguinStatueHealthMultiplier);
        return statueHealth * multiplier;
    }

    private int ResolvePenguinStatLootCount()
    {
        // Resolve how many random stat pickups one defeated Penguin spills.
        return activePenguinEventDefinition != null
            ? activePenguinEventDefinition.StatLootCount
            : Mathf.Max(0, fallbackPenguinStatLootCount);
    }

    private Vector2 ResolvePenguinMoveSpeedRange()
    {
        // Resolve and normalize the active event's roaming-speed range.
        Vector2 range = activePenguinEventDefinition != null
            ? activePenguinEventDefinition.MoveSpeedRange
            : fallbackPenguinMoveSpeedRange;
        float min = Mathf.Max(0f, Mathf.Min(range.x, range.y));
        return new Vector2(min, Mathf.Max(min, Mathf.Max(range.x, range.y)));
    }

    private Vector2 ResolvePenguinDecisionDurationRange()
    {
        // Resolve and normalize how long each random idle or movement decision lasts.
        Vector2 range = activePenguinEventDefinition != null
            ? activePenguinEventDefinition.DecisionDurationRange
            : fallbackPenguinDecisionDurationRange;
        float min = Mathf.Max(0.1f, Mathf.Min(range.x, range.y));
        return new Vector2(min, Mathf.Max(min, Mathf.Max(range.x, range.y)));
    }

    private float ResolvePenguinIdleChance()
    {
        // Resolve the probability that a Penguin pauses at its next movement decision.
        return activePenguinEventDefinition != null
            ? activePenguinEventDefinition.IdleChance
            : Mathf.Clamp01(fallbackPenguinIdleChance);
    }

    private void SpawnMainMatchPickups()
    {
        // 메인 경기용 스탯 아이템 슬롯을 랜덤 위치와 랜덤 스탯으로 활성화.
        for (int i = 0; i < statPickupCount; i++)
        {
            ActivateMainMatchContactPickup(i, GetRandomSpawnPosition());
        }

        Debug.Log($"[GameplayPickupManager] Main match pickups spawned count={statPickupCount}");
    }

    private void ActivateMainMatchContactPickup(int slotId, Vector3 position)
    {
        // Guarantee one visible sample of each stat pickup type during tests, then use the normal random pool.
        if (ensureAllStatTypesOnMainSpawn && slotId < OrderedStatPickupTypes.Length)
        {
            ActivateStatPickup(slotId, OrderedStatPickupTypes[slotId], position);
            return;
        }

        ActivateRandomContactPickup(slotId, position);
    }

    private void SpawnFinalObjective(int slotId)
    {
        // 최종전 승리 조건인 단일 목표 아이템을 필드에 배치.
        ActivateFinalObjective(slotId, GetRandomSpawnPosition());
        Debug.Log("[GameplayPickupManager] Final objective spawned.");
    }

    private void StartFinalMatchObjective()
    {
        // Resolve data from ScriptableObject, then let the matching handler own the runtime behavior.
        activeFinalMatchRuleDefinition = ResolveFinalMatchRuleDefinition();
        activeFinalMatchRuleHandler = CreateFinalMatchRuleHandler(activeFinalMatchRuleDefinition);
        activeFinalMatchRuleHandler.Start(activeFinalMatchRuleDefinition);

        string ruleId = activeFinalMatchRuleDefinition != null ? activeFinalMatchRuleDefinition.RuleId : "fallback";
        Debug.Log($"[GameplayPickupManager] Final match objective started rule={activeFinalMatchRuleHandler.RuleType} ruleId={ruleId}");
    }

    public void ResolveFinalMatchOnTimer()
    {
        // Let timed final objectives finish their own scoring immediately before the Result state begins.
        if (!IsServer || matchStateController == null || matchStateController.State.Value != NetworkMatchState.FinalMatch)
        {
            return;
        }

        activeFinalMatchRuleHandler?.ResolveOnTimer();
    }

    private void StopFinalMatchObjective()
    {
        // Drop references to the active final rule runner when leaving final-match flow.
        activeFinalMatchRuleHandler?.Stop();
        activeFinalMatchRuleHandler = null;
        activeFinalMatchRuleDefinition = null;
    }

    public float ResolveFinalMatchDuration(float fallbackDuration)
    {
        // MatchStateController asks this before entering final match so rule assets can tune duration.
        FinalMatchRuleDefinition definition = ResolveFinalMatchRuleDefinition();
        return definition != null ? definition.ResolveDuration(fallbackDuration) : Mathf.Max(0f, fallbackDuration);
    }

    private void SpawnFinalStatueBreakBoxes(FinalMatchRuleDefinition definition)
    {
        // Spawn score-only statue boxes. They use the normal box visuals but never drop loot.
        int count = ResolveFinalStatueBoxCount(definition);
        int slotIdBase = ResolveFinalStatueBoxSlotIdBase(definition);
        for (int i = 0; i < count; i++)
        {
            ActivateFinalStatueBreakBox(slotIdBase + i, GetRandomSpawnPosition(), definition);
        }

        Debug.Log($"[GameplayPickupManager] Final statue boxes spawned count={count}");
    }

    private void ActivateFinalStatueBreakBox(int slotId, Vector3 position, FinalMatchRuleDefinition definition)
    {
        // Score boxes share the basic box presentation and health tuning, but are marked as no-loot objectives.
        ActivateBoxItem(slotId, position, CreateFinalStatueBoxVariant(definition), finalScoreBox: true, startTimedDespawn: false);
    }

    private BoxVariantDefinition CreateFinalStatueBoxVariant(FinalMatchRuleDefinition definition)
    {
        // Keep the final objective statue visually identical to the existing basic box by reusing the same id.
        return new BoxVariantDefinition
        {
            BoxId = ResolveFinalStatueBoxId(definition),
            DisplayName = "Final Statue Box",
            LootKind = BoxLootKind.Stat,
            LootCount = 0,
            MaxHealth = ResolveFinalStatueMaxHealth(definition),
            SpawnWeight = 1f,
            TintColor = ResolveFinalStatueTintColor(definition)
        };
    }

    private FinalMatchRuleDefinition ResolveFinalMatchRuleDefinition()
    {
        // Prefer an explicitly assigned asset, otherwise load the selected rule id from Resources.
        if (finalMatchRuleDefinition != null)
        {
            return finalMatchRuleDefinition;
        }

        List<FinalMatchRuleDefinition> definitions = ResolveFinalMatchRuleDefinitions();
        if (definitions.Count == 0)
        {
            return null;
        }

        string selectedRuleId = string.IsNullOrWhiteSpace(selectedFinalMatchRuleId)
            ? DefaultFinalMatchRuleId
            : selectedFinalMatchRuleId;
        for (int i = 0; i < definitions.Count; i++)
        {
            FinalMatchRuleDefinition definition = definitions[i];
            if (definition != null && definition.RuleId == selectedRuleId)
            {
                return definition;
            }
        }

        return ChooseWeightedFinalMatchRuleDefinition(definitions);
    }

    private List<FinalMatchRuleDefinition> ResolveFinalMatchRuleDefinitions()
    {
        // Load final rule definitions from Resources so adding rules does not require scene edits.
        List<FinalMatchRuleDefinition> definitions = new();
        if (loadFinalMatchRuleDefinitionsFromResources)
        {
            string resourcesPath = string.IsNullOrWhiteSpace(finalMatchRuleResourcesPath)
                ? DefaultFinalMatchRuleResourcesPath
                : finalMatchRuleResourcesPath;
            definitions.AddRange(Resources.LoadAll<FinalMatchRuleDefinition>(resourcesPath));
        }

        return definitions;
    }

    private static FinalMatchRuleDefinition ChooseWeightedFinalMatchRuleDefinition(List<FinalMatchRuleDefinition> definitions)
    {
        // Fallback selection keeps future random final-rule rotation data-driven.
        float totalWeight = 0f;
        for (int i = 0; i < definitions.Count; i++)
        {
            FinalMatchRuleDefinition definition = definitions[i];
            if (IsUsableFinalMatchRuleDefinition(definition))
            {
                totalWeight += Mathf.Max(0f, definition.SelectionWeight);
            }
        }

        if (totalWeight <= 0f)
        {
            return null;
        }

        float roll = Random.Range(0f, totalWeight);
        for (int i = 0; i < definitions.Count; i++)
        {
            FinalMatchRuleDefinition definition = definitions[i];
            if (!IsUsableFinalMatchRuleDefinition(definition))
            {
                continue;
            }

            roll -= Mathf.Max(0f, definition.SelectionWeight);
            if (roll <= 0f)
            {
                return definition;
            }
        }

        return null;
    }

    private static bool IsUsableFinalMatchRuleDefinition(FinalMatchRuleDefinition definition)
    {
        // Resource-loaded definitions can be disabled without deleting the asset.
        return definition != null &&
            definition.EnabledInPool &&
            definition.SelectionWeight > 0f;
    }

    private IFinalMatchRuleHandler CreateFinalMatchRuleHandler(FinalMatchRuleDefinition definition)
    {
        // Keep runtime logic in handlers and data in ScriptableObjects.
        FinalMatchRuleType ruleType = definition != null
            ? definition.RuleType
            : FinalMatchRuleType.FirstObjectivePickup;
        return ruleType switch
        {
            FinalMatchRuleType.BreakStatues => new BreakStatuesFinalRuleHandler(this),
            _ => new FirstObjectivePickupFinalRuleHandler(this)
        };
    }

    private int ResolveFinalObjectiveSlotIndex(FinalMatchRuleDefinition definition)
    {
        return definition != null ? definition.ObjectiveSlotIndex : DefaultFinalObjectiveSlotIndex;
    }

    private int ResolveFinalStatueBoxCount(FinalMatchRuleDefinition definition)
    {
        return definition != null ? definition.StatueBoxCount : DefaultFinalStatueBoxCount;
    }

    private int ResolveFinalStatueBoxSlotIdBase(FinalMatchRuleDefinition definition)
    {
        return definition != null ? definition.StatueBoxSlotIdBase : DefaultFinalStatueBoxSlotIdBase;
    }

    private float ResolveFinalStatueRespawnDelay(FinalMatchRuleDefinition definition)
    {
        return definition != null ? definition.StatueRespawnDelay : DefaultFinalStatueRespawnDelay;
    }

    private float ResolveFinalStatueMaxHealth(FinalMatchRuleDefinition definition)
    {
        return definition != null ? definition.StatueMaxHealth : Mathf.Max(1f, DefaultFinalStatueMaxHealth);
    }

    private string ResolveFinalStatueBoxId(FinalMatchRuleDefinition definition)
    {
        return definition != null ? definition.StatueBoxId : DefaultFinalStatueBoxId;
    }

    private Color ResolveFinalStatueTintColor(FinalMatchRuleDefinition definition)
    {
        return definition != null ? definition.StatueTintColor : Color.white;
    }

    private string ResolveFinalRuleContext(FinalMatchRuleDefinition definition)
    {
        return definition != null && !string.IsNullOrWhiteSpace(definition.RuleId)
            ? definition.RuleId
            : "final-rule";
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
        slot.RespawnEquipmentOnDespawn = false;
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
        // Activate a functional pickup with normal gravity and no launch impulse.
        ActivateFunctionalPickup(slotId, functionalType, position, respawnOnCollect, Vector3.zero);
    }

    private void ActivateFunctionalPickup(int slotId, FunctionalPickupType functionalType, Vector3 position, bool respawnOnCollect, Vector3 initialVelocity)
    {
        // Activate a contact-collected functional item such as the current basic heal pickup.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = true;
        slot.Hooked = false;
        slot.RespawnOnCollect = respawnOnCollect;
        slot.RespawnEquipmentOnDespawn = false;
        slot.Kind = PickupKind.Functional;
        slot.StatType = PlayerStatType.Health;
        slot.FunctionalType = functionalType;
        slot.EquipmentId = string.Empty;
        slot.Position = position;

        SetPickupVisualClientRpc(slotId, true, position, slot.StatType, PickupKind.Functional, default, functionalType);
        StartPickupDespawnTimer(slotId);
        StartPickupPhysics(slotId, initialVelocity);
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
        // Spawn destructible boxes that pre-roll loot according to their selected variant.
        for (int i = 0; i < boxItemCount; i++)
        {
            ActivateBoxItem(boxSlotIdBase + i, GetRandomSpawnPosition(), ChooseRandomBoxVariant());
        }

        Debug.Log($"[GameplayPickupManager] Box items spawned count={boxItemCount}");
    }

    private void ActivateBoxItem(int slotId, Vector3 position, BoxVariantDefinition variant)
    {
        // Default box activation keeps the existing main-match loot and timed despawn behavior.
        ActivateBoxItem(slotId, position, variant, finalScoreBox: false, startTimedDespawn: true);
    }

    private void ActivateBoxItem(int slotId, Vector3 position, BoxVariantDefinition variant, bool finalScoreBox, bool startTimedDespawn)
    {
        // Initialize a destructible box with variant-specific health, tint, and pre-rolled loot.
        Vector3 resolvedPosition = ResolveBoxGroundedPosition(position);
        BoxVariantDefinition resolvedVariant = ResolveBoxVariant(variant);
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        slot.Active = true;
        slot.FinalScoreBox = finalScoreBox;
        slot.BoxId = resolvedVariant.BoxId;
        slot.LootKind = resolvedVariant.LootKind;
        slot.MaxHealth = Mathf.Max(1f, resolvedVariant.MaxHealth);
        slot.CurrentHealth = slot.MaxHealth;
        PreRollBoxLoot(slot, resolvedVariant);
        if (finalScoreBox)
        {
            slot.LootStats = System.Array.Empty<PlayerStatType>();
            slot.LootFunctionalTypes = System.Array.Empty<FunctionalPickupType>();
            slot.LootEquipmentIds = System.Array.Empty<string>();
        }

        slot.Position = resolvedPosition;

        SetBoxVisualClientRpc(slotId, true, resolvedPosition, 1f, new FixedString64Bytes(slot.BoxId));
        if (startTimedDespawn)
        {
            StartBoxDespawnTimer(slotId);
        }
    }

    private Vector3 ResolveBoxGroundedPosition(Vector3 position)
    {
        // Snap static box spawns to the detected ground so shared pickup spawn height does not make boxes float.
        Vector3 rayOrigin = position + Vector3.up * Mathf.Max(0f, boxGroundRaycastHeight);
        float rayDistance = Mathf.Max(0.1f, boxGroundRaycastHeight + boxGroundRaycastDistance);
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, rayDistance, boxGroundMask, QueryTriggerInteraction.Ignore))
        {
            position.y = hit.point.y + boxGroundOffset;
        }

        return position;
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
        SetBoxVisualClientRpc(slotId, false, Vector3.zero, 0f, new FixedString64Bytes(slot.BoxId ?? string.Empty));
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
            SetBoxVisualClientRpc(pair.Key, false, Vector3.zero, 0f, new FixedString64Bytes(slot.BoxId ?? string.Empty));
        }
    }

    private void ActivateFinalObjective(int slotId, Vector3 position)
    {
        // Activate the final objective pickup that still uses contact collection.
        PickupSlot slot = GetOrCreateSlot(slotId);
        slot.Active = true;
        slot.Hooked = false;
        slot.RespawnOnCollect = false;
        slot.RespawnEquipmentOnDespawn = false;
        slot.Kind = PickupKind.FinalObjective;
        slot.StatType = PlayerStatType.MoveSpeed;
        slot.FunctionalType = FunctionalPickupType.None;
        slot.EquipmentId = string.Empty;
        slot.Position = position;

        SetPickupVisualClientRpc(slotId, true, position, slot.StatType, PickupKind.FinalObjective, default, FunctionalPickupType.None);
        StartPickupPhysics(slotId, Vector3.zero);
    }

    private void ActivateEquipmentPickup(int slotId, EquipmentDefinition equipment, Vector3 position, float healthPercent = 1f, bool respawnOnDespawn = true)
    {
        // Activate a hook-only equipment pickup with normal gravity and no launch impulse.
        ActivateEquipmentPickup(slotId, equipment, position, healthPercent, respawnOnDespawn, Vector3.zero);
    }

    private void ActivateEquipmentPickup(int slotId, EquipmentDefinition equipment, Vector3 position, float healthPercent, bool respawnOnDespawn, Vector3 initialVelocity)
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
        slot.RespawnEquipmentOnDespawn = respawnOnDespawn;
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
        StartPickupPhysics(slotId, initialVelocity);
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
        slot.RespawnEquipmentOnDespawn = false;
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
            slot.RespawnEquipmentOnDespawn = false;
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
            PlayPickupSfxForClient(clientId, PickupSfxKind.FinalObjective);
            DeactivatePickup(slotId);
            matchStateController?.CompleteFinalObjectiveByClient(clientId);
            Debug.Log($"[GameplayPickupManager] Final objective collected clientId={clientId}");
            return;
        }

        if (slot.Kind == PickupKind.Functional)
        {
            FunctionalPickupType collectedFunctionalType = slot.FunctionalType;
            if (!ApplyFunctionalPickup(slot, clientId))
            {
                return;
            }

            PlayFunctionalPickupEffect(clientId, collectedFunctionalType);
            PlayPickupSfxForClient(clientId, ResolveFunctionalPickupSfxKind(collectedFunctionalType));
            DeactivatePickup(slotId);
            ScheduleContactPickupRespawn(slotId, slot);
            Debug.Log($"[GameplayPickupManager] Functional pickup collected clientId={clientId} type={slot.FunctionalType}");
            return;
        }

        float previousMaxHealth = slot.StatType == PlayerStatType.Health
            ? NetworkPlayerCombatState.GetMaxHealthForClient(clientId)
            : 0f;
        int previousStackCount = statsState != null ? statsState.GetStackCount(clientId, slot.StatType) : 0;
        statsState?.AddStat(clientId, slot.StatType, 1);
        int currentStackCount = statsState != null ? statsState.GetStackCount(clientId, slot.StatType) : previousStackCount;
        if (slot.StatType == PlayerStatType.Health)
        {
            NetworkPlayerCombatState.AddCurrentHealthForMaxHealthGain(clientId, previousMaxHealth);
        }

        if (currentStackCount > previousStackCount)
        {
            PlayPickupEffectForClient(clientId, PickupEffectKind.StatBuff);
        }

        PlayPickupSfxForClient(clientId, PickupSfxKind.Stat);
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
            FunctionalPickupType.AttackPowerBuff => NetworkPlayerCombatState.TryApplyAttackBuff(clientId, Mathf.Max(0f, attackPowerBuffDuration), Mathf.Max(0.01f, attackPowerBuffMultiplier)),
            FunctionalPickupType.DamageReductionBuff => NetworkPlayerCombatState.TryApplyDamageReductionBuff(clientId, Mathf.Max(0f, damageReductionBuffDuration), Mathf.Clamp(damageReductionTakenMultiplier, 0.01f, 1f)),
            FunctionalPickupType.MoveSpeedBuff => NetworkPlayerCombatState.TryApplyMoveSpeedBuff(clientId, Mathf.Max(0f, moveSpeedBuffDuration), Mathf.Max(0.01f, moveSpeedBuffMultiplier)),
            FunctionalPickupType.AutoFireBuff => NetworkPlayerCombatState.TryApplyAutoFireBuff(clientId, Mathf.Max(0f, autoFireBuffDuration)),
            _ => false
        };
    }

    private void PlayFunctionalPickupEffect(ulong clientId, FunctionalPickupType functionalType)
    {
        // Route functional pickup visuals by subtype so future utility items can use different effects.
        if (functionalType == FunctionalPickupType.BasicHeal)
        {
            PlayPickupEffectForClient(clientId, PickupEffectKind.Healing);
            return;
        }

        // Attack, defense, movement, and auto-fire buff state is driven by NetworkPlayerCombatState.
    }

    private static PickupSfxKind ResolveFunctionalPickupSfxKind(FunctionalPickupType functionalType)
    {
        // Keep each functional pickup on an independent sound category for later audio replacement.
        return functionalType switch
        {
            FunctionalPickupType.BasicHeal => PickupSfxKind.Healing,
            FunctionalPickupType.AttackPowerBuff => PickupSfxKind.AttackBuff,
            FunctionalPickupType.DamageReductionBuff => PickupSfxKind.DefenceBuff,
            FunctionalPickupType.MoveSpeedBuff => PickupSfxKind.MoveSpeedBuff,
            FunctionalPickupType.AutoFireBuff => PickupSfxKind.AutoFireBuff,
            _ => PickupSfxKind.Stat
        };
    }

    private void PlayPickupSfxForClient(ulong clientId, PickupSfxKind kind)
    {
        // Send pickup audio only to the client whose server-authoritative collection succeeded.
        if (!IsServer || NetworkManager.Singleton == null ||
            !NetworkManager.Singleton.ConnectedClients.ContainsKey(clientId))
        {
            return;
        }

        ClientRpcParams rpcParams = new()
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        };
        PlayPickupSfxClientRpc(kind, rpcParams);
    }

    private void PlayPickupEffectForClient(ulong clientId, PickupEffectKind effectKind)
    {
        // Resolve the server-authoritative player position and replicate a short pickup VFX to all clients.
        if (!IsServer || !TryGetClientPickupEffectPosition(clientId, out Vector3 position))
        {
            return;
        }

        PlayPickupEffectClientRpc(effectKind, position);
    }

    private bool TryGetClientPickupEffectPosition(ulong clientId, out Vector3 position)
    {
        // Find the current player object so pickup effects appear on the collector instead of the field slot.
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client) &&
            client.PlayerObject != null)
        {
            position = client.PlayerObject.transform.position + pickupEffectWorldOffset;
            return true;
        }

        position = default;
        return false;
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
        PlayEquipmentHookAnimationClientRpc(clientId);
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

            if (TryFindHookContact(clientId, previousTip, currentTip, out HookContact contact))
            {
                ResolveHookContact(contact, clientId, hookVisualId);
                yield break;
            }

            yield return null;
        }

        if (TryFindHookContact(clientId, currentTip, targetPoint, out HookContact finalContact))
        {
            ResolveHookContact(finalContact, clientId, hookVisualId);
            yield break;
        }

        Debug.Log($"[GameplayPickupManager] Equipment hook missed clientId={clientId}");
    }

    private bool TryFindHookContact(ulong requesterClientId, Vector3 segmentStart, Vector3 segmentEnd, out HookContact contact)
    {
        // Find the nearest hookable equipment drop or stealable low-health player along this hook segment.
        contact = default;
        Vector3 segment = segmentEnd - segmentStart;
        float segmentLength = segment.magnitude;
        if (segmentLength <= 0.001f)
        {
            return false;
        }

        Vector3 direction = segment / segmentLength;
        float nearestAlongSegment = float.MaxValue;
        TryFindHookEquipmentDropContact(segmentStart, direction, segmentLength, ref nearestAlongSegment, ref contact);
        TryFindHookPlayerStealContact(requesterClientId, segmentStart, direction, segmentLength, ref nearestAlongSegment, ref contact);
        return contact.Kind != HookContactKind.None;
    }

    private void TryFindHookEquipmentDropContact(Vector3 segmentStart, Vector3 direction, float segmentLength, ref float nearestAlongSegment, ref HookContact contact)
    {
        // Check active field equipment drops against the current hook travel segment.
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
                contact = new HookContact(HookContactKind.EquipmentDrop, pair.Key, 0, segmentStart + direction * alongSegment);
            }
        }
    }

    private void TryFindHookPlayerStealContact(ulong requesterClientId, Vector3 segmentStart, Vector3 direction, float segmentLength, ref float nearestAlongSegment, ref HookContact contact)
    {
        // Check other players whose equipment is already sparking and therefore can be stolen by hook.
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        float stealRadius = Mathf.Max(0.1f, hookPlayerStealRadius);
        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            if (clientId == requesterClientId ||
                !NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out NetworkClient client) ||
                client.PlayerObject == null ||
                !client.PlayerObject.TryGetComponent(out NetworkPlayerCombatState targetCombatState) ||
                !targetCombatState.CanBeHookStolen)
            {
                continue;
            }

            Vector3 targetPoint = client.PlayerObject.transform.position + Vector3.up * Mathf.Max(0f, hookPlayerStealTargetHeight);
            float distanceToRay = DistanceFromRaySegment(segmentStart, direction, targetPoint, segmentLength, out float alongSegment);
            if (distanceToRay <= stealRadius && alongSegment < nearestAlongSegment)
            {
                nearestAlongSegment = alongSegment;
                contact = new HookContact(HookContactKind.PlayerEquipment, -1, clientId, segmentStart + direction * alongSegment);
            }
        }
    }

    private void ResolveHookContact(HookContact contact, ulong clientId, int hookVisualId)
    {
        // Dispatch the resolved hook contact to either field-equipment pulling or player-equipment stealing.
        if (contact.Kind == HookContactKind.EquipmentDrop)
        {
            BeginPullEquipmentToClient(contact.SlotId, clientId, hookVisualId);
            return;
        }

        if (contact.Kind == HookContactKind.PlayerEquipment)
        {
            StealPlayerEquipmentWithHook(contact.TargetClientId, clientId, contact.Point, hookVisualId);
        }
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
        bool shouldRespawnSlot = slot.RespawnEquipmentOnDespawn;
        EquipmentDefinition incomingEquipment = EquipmentCatalog.Get(slot.EquipmentId);
        float incomingHealthPercent = ResolveEquipmentHealthPercent(slot);
        NetworkPlayerEquipmentState.TryGetClientEquipment(clientId, out EquipmentDefinition previousEquipment);
        float previousHealthPercent = previousEquipment != null ?
            NetworkPlayerCombatState.GetEquipmentHealthPercent(clientId) :
            0f;

        if (incomingEquipment != null && NetworkPlayerEquipmentState.TryEquipClient(clientId, incomingEquipment))
        {
            NetworkPlayerCombatState.ResetClientForEquippedHealthPercent(clientId, incomingHealthPercent);
            PlayPickupSfxForClient(clientId, PickupSfxKind.Equipment);
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
        if (shouldRespawnSlot && matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnEquipmentPickupAfterDelay(slotId));
        }
    }

    private void StealPlayerEquipmentWithHook(ulong victimClientId, ulong stealerClientId, Vector3 contactPoint, int hookVisualId)
    {
        // Transfer low-health equipment from a hooked player, drop the stealer's old equipment, and heal the stealer.
        if (!NetworkPlayerEquipmentState.ClientHasEquipmentState(stealerClientId) ||
            !TryGetPlayerObject(stealerClientId, out NetworkObject stealerObject) ||
            !TryGetPlayerObject(victimClientId, out NetworkObject victimObject) ||
            !victimObject.TryGetComponent(out NetworkPlayerCombatState victimCombatState))
        {
            return;
        }

        if (stealerObject != null)
        {
            LatchEquipmentHookVisualClientRpc(hookVisualId, contactPoint, stealerObject.transform.position, hookPullSpeed);
        }

        NetworkPlayerEquipmentState.TryGetClientEquipment(stealerClientId, out EquipmentDefinition previousEquipment);
        float previousHealthPercent = previousEquipment != null ?
            NetworkPlayerCombatState.GetEquipmentHealthPercent(stealerClientId) :
            0f;

        if (!victimCombatState.TryStealEquipmentByHook(stealerClientId, out EquipmentDefinition stolenEquipment, out float stolenHealthPercent))
        {
            return;
        }

        if (!NetworkPlayerEquipmentState.TryEquipClient(stealerClientId, stolenEquipment))
        {
            Debug.LogWarning($"[GameplayPickupManager] Hook steal failed after victim unequip stealer={stealerClientId} victim={victimClientId}");
            return;
        }

        NetworkPlayerCombatState.ResetClientForEquippedHealthPercent(stealerClientId, stolenHealthPercent);
        NetworkPlayerCombatState.TryHealPercent(stealerClientId, Mathf.Clamp01(hookStealHealPercent), "hook-steal");
        PlayPickupSfxForClient(stealerClientId, PickupSfxKind.Equipment);
        DropPreviousEquipmentFromHookSteal(stealerObject, stealerClientId, previousEquipment, previousHealthPercent);
        SendHookStealNotices(victimClientId, stealerClientId, stolenEquipment);
        Debug.Log($"[GameplayPickupManager] Player equipment stolen stealer={stealerClientId} victim={victimClientId} equipment={stolenEquipment.EquipmentId} stolenHealthPercent={stolenHealthPercent:0.00}");
    }

    private void SendHookStealNotices(ulong victimClientId, ulong stealerClientId, EquipmentDefinition stolenEquipment)
    {
        // Notify both clients after a hook steal successfully transfers equipment ownership.
        if (!IsServer || stolenEquipment == null)
        {
            return;
        }

        MatchStateController controller = MatchStateController.Instance;
        if (controller == null || !controller.IsSpawned)
        {
            return;
        }

        string equipmentName = FormatEquipmentName(stolenEquipment);
        controller.ShowNoticeToClient(victimClientId, $"{equipmentName}을 강탈당하였습니다!", 4f);
        controller.ShowNoticeToClient(stealerClientId, $"{FormatClientId(victimClientId)}로부터 {equipmentName}을 강탈하였습니다!", 4f);
    }

    private static string FormatEquipmentName(EquipmentDefinition equipment)
    {
        // Prefer player-facing equipment display names, falling back to the stable id when needed.
        if (equipment == null)
        {
            return "장비";
        }

        return string.IsNullOrWhiteSpace(equipment.DisplayName) ? equipment.EquipmentId : equipment.DisplayName;
    }

    private static string FormatClientId(ulong clientId)
    {
        // Format a temporary player label until user-facing player names are introduced.
        return $"플레이어 {clientId}";
    }

    private void DropPreviousEquipmentFromHookSteal(NetworkObject stealerObject, ulong stealerClientId, EquipmentDefinition previousEquipment, float previousHealthPercent)
    {
        // Drop the stealer's old equipment as a temporary field item that does not expand the normal spawn pool.
        if (previousEquipment == null || stealerObject == null)
        {
            return;
        }

        int slotId = GetNextLootPickupSlotId();
        ActivateEquipmentPickup(slotId, previousEquipment, ResolveEquipmentDropPosition(stealerObject), previousHealthPercent, respawnOnDespawn: false);
        Debug.Log($"[GameplayPickupManager] Previous equipment dropped after steal clientId={stealerClientId} slot={slotId} equipment={previousEquipment.EquipmentId} healthPercent={previousHealthPercent:0.00}");
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

    public bool TryApplyBoxDamage(int slotId, float damage, ulong attackerClientId, Vector3 hitPoint)
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
        bool destroyed = slot.CurrentHealth <= 0f;
        SetBoxVisualClientRpc(slotId, true, slot.Position, healthPercent, new FixedString64Bytes(slot.BoxId ?? string.Empty));
        PlayBoxHitEffectClientRpc(ResolveBoxHitEffectPoint(slot, hitPoint), destroyed);
        Debug.Log($"[GameplayPickupManager] Box damaged slot={slotId} attacker={attackerClientId} damage={damage:0.0} health={slot.CurrentHealth:0.0}");

        if (destroyed)
        {
            BreakBoxItem(slotId, attackerClientId);
            return true;
        }

        if (!slot.FinalScoreBox)
        {
            StartBoxDespawnTimer(slotId);
        }

        return true;
    }

    private Vector3 ResolveBoxHitEffectPoint(BoxSlot slot, Vector3 hitPoint)
    {
        // Prefer the projectile impact point and fall back to the visible upper body of the box.
        if (IsFinite(hitPoint))
        {
            return hitPoint;
        }

        return slot != null
            ? slot.Position + Vector3.up * Mathf.Max(0f, boxTargetHeight)
            : Vector3.zero;
    }

    public bool TryFindDamageablePenguin(Vector3 origin, Vector3 direction, float maxDistance, float projectileRadius, out int slotId, out float hitDistance, out Vector3 hitPoint)
    {
        // Find the nearest living event Penguin touched by a server-approved projectile path.
        slotId = -1;
        hitDistance = float.MaxValue;
        hitPoint = default;
        if (!IsServer || direction.sqrMagnitude <= 0.0001f || maxDistance <= 0f)
        {
            return false;
        }

        Vector3 normalizedDirection = direction.normalized;
        float combinedRadius = Mathf.Max(0f, projectileRadius) + Mathf.Max(0.1f, penguinHitRadius);
        foreach (KeyValuePair<int, PenguinSlot> pair in penguinSlots)
        {
            PenguinSlot slot = pair.Value;
            if (slot == null || !slot.Visible || !slot.Alive || slot.CurrentHealth <= 0f)
            {
                continue;
            }

            Vector3 targetPoint = slot.Position + Vector3.up * Mathf.Max(0f, penguinTargetHeight);
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

    public bool TryApplyPenguinDamage(int slotId, float damage, ulong attackerClientId, Vector3 hitPoint)
    {
        // Preserve the existing damage API with a stable fallback effect direction.
        return TryApplyPenguinDamage(slotId, damage, attackerClientId, hitPoint, Vector3.forward);
    }

    public bool TryApplyPenguinDamage(int slotId, float damage, ulong attackerClientId, Vector3 hitPoint, Vector3 hitDirection)
    {
        // Apply server-authoritative damage, replicate hit feedback, and trigger one death sequence at zero health.
        if (!IsServer || damage <= 0f || !penguinSlots.TryGetValue(slotId, out PenguinSlot slot) ||
            slot == null || !slot.Visible || !slot.Alive || slot.CurrentHealth <= 0f)
        {
            return false;
        }

        slot.CurrentHealth = Mathf.Max(0f, slot.CurrentHealth - damage);
        PlayPenguinHitEffectClientRpc(ResolvePenguinHitEffectPoint(slot, hitPoint), ResolvePenguinHitEffectDirection(hitDirection));
        Debug.Log($"[GameplayPickupManager] Penguin damaged slot={slotId} attacker={attackerClientId} damage={damage:0.0} health={slot.CurrentHealth:0.0}/{slot.MaxHealth:0.0} point={hitPoint}");
        if (slot.CurrentHealth <= 0f)
        {
            DefeatPenguin(slotId, attackerClientId, slot);
        }

        return true;
    }

    private void DefeatPenguin(int slotId, ulong attackerClientId, PenguinSlot slot)
    {
        // Stop the defeated Penguin, spill random stats, and keep its visual alive long enough for death playback.
        if (slot == null || !slot.Alive)
        {
            return;
        }

        slot.Alive = false;
        slot.MoveDirection = Vector3.zero;
        slot.MoveSpeed = 0f;
        int lootCount = DropStatBoxLoot(slot.Position, GenerateStatLoot(ResolvePenguinStatLootCount()));
        SendPenguinVisualState(slotId, slot, snap: true, playDeath: true);

        if (slot.DeathRoutine != null)
        {
            StopCoroutine(slot.DeathRoutine);
        }

        slot.DeathRoutine = StartCoroutine(HideDefeatedPenguinAfterDelay(slotId));
        Debug.Log($"[GameplayPickupManager] Penguin defeated slot={slotId} attacker={attackerClientId} lootCount={lootCount}");
    }

    private IEnumerator HideDefeatedPenguinAfterDelay(int slotId)
    {
        // Wait for the death animation, then return the visual to its inactive pooled state without respawning it.
        yield return new WaitForSeconds(Mathf.Max(0f, penguinDeathDuration));
        if (!penguinSlots.TryGetValue(slotId, out PenguinSlot slot) || slot == null)
        {
            yield break;
        }

        slot.DeathRoutine = null;
        if (slot.Alive)
        {
            yield break;
        }

        PlayPenguinDisappearEffectClientRpc(ResolvePenguinDisappearEffectPoint(slot));
        slot.Visible = false;
        SendPenguinVisualState(slotId, slot, snap: true, playDeath: false);
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
        // Preserve callers without impact data by placing feedback at the field equipment target point.
        PickupSlot slot = GetOrCreateSlot(slotId);
        Vector3 fallbackHitPoint = slot.Position + Vector3.up * Mathf.Max(0f, equipmentTargetHeight);
        return TryApplyEquipmentDamage(slotId, damage, attackerClientId, fallbackHitPoint, Vector3.forward);
    }

    public bool TryApplyEquipmentDamage(int slotId, float damage, ulong attackerClientId, Vector3 hitPoint, Vector3 hitDirection)
    {
        // Apply server-authoritative damage and replicate player-style impact feedback at the approved hit point.
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
        PlayFieldEquipmentHitEffectClientRpc(
            ResolveEquipmentHitEffectPoint(slot, hitPoint),
            ResolveEquipmentHitEffectDirection(hitDirection));
        Debug.Log($"[GameplayPickupManager] Equipment damaged slot={slotId} attacker={attackerClientId} equipment={slot.EquipmentId} damage={damage:0.0} health={slot.EquipmentCurrentHealth:0.0}/{slot.EquipmentMaxHealth:0.0}");

        if (slot.EquipmentCurrentHealth <= 0f)
        {
            DestroyEquipmentPickup(slotId, attackerClientId);
        }

        return true;
    }

    public int ApplySplashDamage(Vector3 center, float radius, float baseDamage, ulong attackerClientId, float minimumMultiplier)
    {
        // Preserve existing callers that only need the total number of damaged pickup targets.
        return ApplySplashDamage(center, radius, baseDamage, attackerClientId, minimumMultiplier, out _);
    }

    public int ApplySplashDamage(
        Vector3 center,
        float radius,
        float baseDamage,
        ulong attackerClientId,
        float minimumMultiplier,
        out int penguinHitCount)
    {
        // Apply splash damage and separately report Penguin hits for one attacker confirmation sound.
        penguinHitCount = 0;
        if (!IsServer || radius <= 0f || baseDamage <= 0f)
        {
            return 0;
        }

        int hitCount = 0;
        float resolvedRadius = Mathf.Max(0.01f, radius);
        float resolvedMinimumMultiplier = Mathf.Clamp01(minimumMultiplier);

        List<int> boxTargets = new();
        foreach (KeyValuePair<int, BoxSlot> pair in boxSlots)
        {
            BoxSlot slot = pair.Value;
            if (slot != null && slot.Active && slot.CurrentHealth > 0f)
            {
                boxTargets.Add(pair.Key);
            }
        }

        List<int> equipmentTargets = new();
        foreach (KeyValuePair<int, PickupSlot> pair in slots)
        {
            if (IsDamageableEquipmentSlot(pair.Value))
            {
                equipmentTargets.Add(pair.Key);
            }
        }

        List<int> penguinTargets = new();
        foreach (KeyValuePair<int, PenguinSlot> pair in penguinSlots)
        {
            PenguinSlot slot = pair.Value;
            if (slot != null && slot.Visible && slot.Alive && slot.CurrentHealth > 0f)
            {
                penguinTargets.Add(pair.Key);
            }
        }

        for (int i = 0; i < boxTargets.Count; i++)
        {
            int slotId = boxTargets[i];
            BoxSlot slot = GetOrCreateBoxSlot(slotId);
            if (TryResolveBoxSplashDamage(slot, center, resolvedRadius, baseDamage, resolvedMinimumMultiplier, out float damage, out Vector3 hitPoint) &&
                TryApplyBoxDamage(slotId, damage, attackerClientId, hitPoint))
            {
                hitCount++;
            }
        }

        for (int i = 0; i < equipmentTargets.Count; i++)
        {
            int slotId = equipmentTargets[i];
            PickupSlot slot = GetOrCreateSlot(slotId);
            if (TryResolveEquipmentSplashDamage(slot, center, resolvedRadius, baseDamage, resolvedMinimumMultiplier, out float damage, out Vector3 hitPoint) &&
                TryApplyEquipmentDamage(slotId, damage, attackerClientId, hitPoint, hitPoint - center))
            {
                hitCount++;
            }
        }

        for (int i = 0; i < penguinTargets.Count; i++)
        {
            int slotId = penguinTargets[i];
            PenguinSlot slot = GetOrCreatePenguinSlot(slotId);
            if (TryResolvePenguinSplashDamage(slot, center, resolvedRadius, baseDamage, resolvedMinimumMultiplier, out float damage, out Vector3 hitPoint) &&
                TryApplyPenguinDamage(slotId, damage, attackerClientId, hitPoint, ResolvePenguinHitEffectDirection(hitPoint - center)))
            {
                hitCount++;
                penguinHitCount++;
            }
        }

        return hitCount;
    }

    private bool TryResolveBoxSplashDamage(BoxSlot slot, Vector3 center, float radius, float baseDamage, float minimumMultiplier, out float damage, out Vector3 hitPoint)
    {
        // Calculate one box's explosion falloff using its visible target point.
        damage = 0f;
        hitPoint = default;
        if (slot == null || !slot.Active || slot.CurrentHealth <= 0f)
        {
            return false;
        }

        Vector3 targetPoint = slot.Position + Vector3.up * Mathf.Max(0f, boxTargetHeight);
        float targetRadius = Mathf.Max(0.1f, boxHitRadius);
        hitPoint = ResolveSplashSurfacePoint(center, targetPoint, targetRadius);
        return TryResolveSplashDamage(center, radius, baseDamage, minimumMultiplier, targetPoint, targetRadius, out damage);
    }

    private bool TryResolveEquipmentSplashDamage(PickupSlot slot, Vector3 center, float radius, float baseDamage, float minimumMultiplier, out float damage, out Vector3 hitPoint)
    {
        // Calculate one field equipment drop's explosion falloff and visible surface impact point.
        damage = 0f;
        hitPoint = default;
        if (!IsDamageableEquipmentSlot(slot))
        {
            return false;
        }

        Vector3 targetPoint = slot.Position + Vector3.up * Mathf.Max(0f, equipmentTargetHeight);
        float targetRadius = Mathf.Max(0.1f, equipmentHitRadius);
        hitPoint = ResolveSplashSurfacePoint(center, targetPoint, targetRadius);
        return TryResolveSplashDamage(center, radius, baseDamage, minimumMultiplier, targetPoint, targetRadius, out damage);
    }

    private bool TryResolvePenguinSplashDamage(PenguinSlot slot, Vector3 center, float radius, float baseDamage, float minimumMultiplier, out float damage, out Vector3 hitPoint)
    {
        // Calculate explosion falloff against a living Penguin's visible body volume.
        damage = 0f;
        hitPoint = default;
        if (slot == null || !slot.Visible || !slot.Alive || slot.CurrentHealth <= 0f)
        {
            return false;
        }

        Vector3 targetPoint = slot.Position + Vector3.up * Mathf.Max(0f, penguinTargetHeight);
        float targetRadius = Mathf.Max(0.1f, penguinHitRadius);
        hitPoint = ResolveSplashSurfacePoint(center, targetPoint, targetRadius);
        return TryResolveSplashDamage(center, radius, baseDamage, minimumMultiplier, targetPoint, targetRadius, out damage);
    }

    private static bool TryResolveSplashDamage(Vector3 center, float radius, float baseDamage, float minimumMultiplier, Vector3 targetPoint, float targetRadius, out float damage)
    {
        // Convert distance to the target surface into a 100%-to-minimum splash damage value.
        damage = 0f;
        float distance = Mathf.Max(0f, Vector3.Distance(center, targetPoint) - Mathf.Max(0f, targetRadius));
        if (distance > radius)
        {
            return false;
        }

        float normalizedDistance = Mathf.Clamp01(distance / Mathf.Max(0.01f, radius));
        damage = baseDamage * Mathf.Lerp(1f, Mathf.Clamp01(minimumMultiplier), normalizedDistance);
        return damage > 0f;
    }

    private static Vector3 ResolveSplashSurfacePoint(Vector3 center, Vector3 targetPoint, float targetRadius)
    {
        // Place box hit feedback near the surface closest to the explosion center.
        Vector3 direction = center - targetPoint;
        float distance = direction.magnitude;
        if (distance <= 0.0001f)
        {
            return targetPoint;
        }

        return targetPoint + direction.normalized * Mathf.Min(Mathf.Max(0f, targetRadius), distance);
    }

    private void BreakBoxItem(int slotId, ulong attackerClientId)
    {
        // Convert a destroyed box into its pre-selected variant loot and schedule a replacement box.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        if (activeFinalMatchRuleHandler != null &&
            activeFinalMatchRuleHandler.TryHandleBoxBroken(slotId, attackerClientId, slot))
        {
            return;
        }

        if (slot.LootKind == BoxLootKind.Bomb)
        {
            BreakBombBoxItem(slotId, attackerClientId);
            return;
        }

        Vector3 dropCenter = slot.Position;
        BoxLootKind lootKind = slot.LootKind;
        PlayerStatType[] lootStats = slot.LootStats ?? System.Array.Empty<PlayerStatType>();
        FunctionalPickupType[] lootFunctionalTypes = slot.LootFunctionalTypes ?? System.Array.Empty<FunctionalPickupType>();
        string[] lootEquipmentIds = slot.LootEquipmentIds ?? System.Array.Empty<string>();

        DeactivateBoxItem(slotId);
        bool useMixedLoot = IsMixedBoxLootSuddenEventActive();
        int lootCount = useMixedLoot
            ? DropMixedBoxLoot(dropCenter, ResolveBoxLootCount(slot))
            : DropBoxLoot(dropCenter, lootKind, lootStats, lootFunctionalTypes, lootEquipmentIds);

        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnBoxItemAfterDelay(slotId));
        }

        Debug.Log($"[GameplayPickupManager] Box broken slot={slotId} attacker={attackerClientId} boxId={slot.BoxId} lootKind={lootKind} mixedLoot={useMixedLoot} lootCount={lootCount}");
    }

    private void BreakBombBoxItem(int slotId, ulong attackerClientId)
    {
        // Replace normal loot spilling with a server-authoritative player splash explosion.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        Vector3 explosionCenter = ResolveBombBoxExplosionCenter(slot);
        string boxId = slot.BoxId;

        DeactivateBoxItem(slotId);
        int playerHits = NetworkPlayerCombatState.ApplySplashDamage(
            explosionCenter,
            Mathf.Max(0.01f, bombBoxExplosionRadius),
            Mathf.Max(0f, bombBoxExplosionDamage),
            attackerClientId,
            Mathf.Clamp01(bombBoxExplosionMinimumDamageMultiplier),
            Mathf.Clamp01(bombBoxExplosionSelfDamageMultiplier));
        PlayBombBoxExplosionEffectClientRpc(explosionCenter);

        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnBoxItemAfterDelay(slotId));
        }

        Debug.Log($"[GameplayPickupManager] Bomb box exploded slot={slotId} attacker={attackerClientId} boxId={boxId} radius={bombBoxExplosionRadius:0.0} damage={bombBoxExplosionDamage:0.0} playerHits={playerHits}");
    }

    private Vector3 ResolveBombBoxExplosionCenter(BoxSlot slot)
    {
        // Place the explosion around the visible center of the statue rather than its ground point.
        return slot != null
            ? slot.Position + Vector3.up * Mathf.Max(0f, boxTargetHeight * 0.5f)
            : Vector3.zero;
    }

    private void BreakFinalStatueBox(int slotId, ulong attackerClientId, FinalMatchRuleDefinition definition)
    {
        // Final objective statues award one point to the breaker and never drop field loot.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        RegisterFinalStatueBreakScore(attackerClientId);
        DeactivateBoxItem(slotId);

        if (matchStateController != null && matchStateController.State.Value == NetworkMatchState.FinalMatch)
        {
            slot.RespawnRoutine = StartCoroutine(RespawnFinalStatueBoxAfterDelay(slotId, definition));
        }

        int score = finalStatueBreakScores.TryGetValue(attackerClientId, out int currentScore) ? currentScore : 0;
        Debug.Log($"[GameplayPickupManager] Final statue broken slot={slotId} attacker={attackerClientId} score={score}");
    }

    private IEnumerator RespawnFinalStatueBoxAfterDelay(int slotId, FinalMatchRuleDefinition definition)
    {
        // Keep the timed statue-breaking objective supplied with targets until the final timer ends.
        yield return new WaitForSeconds(ResolveFinalStatueRespawnDelay(definition));

        if (!IsServer ||
            matchStateController == null ||
            matchStateController.State.Value != NetworkMatchState.FinalMatch ||
            activeFinalMatchRuleHandler == null ||
            activeFinalMatchRuleHandler.RuleType != FinalMatchRuleType.BreakStatues)
        {
            yield break;
        }

        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        slot.RespawnRoutine = null;
        ActivateFinalStatueBreakBox(slotId, GetRandomSpawnPosition(), definition);
    }

    private void ResetFinalStatueBreakScores()
    {
        // Clear score state for the timed statue-breaking final objective.
        finalStatueBreakScores.Clear();
        if (!IsServer || NetworkManager.Singleton == null)
        {
            return;
        }

        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            EnsureFinalStatueScoreEntry(clientId);
        }
    }

    private void RegisterFinalStatueBreakScore(ulong clientId)
    {
        // Award one objective point to the player who destroyed a final statue.
        EnsureFinalStatueScoreEntry(clientId);
        finalStatueBreakScores[clientId]++;
    }

    private void EnsureFinalStatueScoreEntry(ulong clientId)
    {
        // Include zero-score players so timeout resolution can detect ties cleanly.
        if (!finalStatueBreakScores.ContainsKey(clientId))
        {
            finalStatueBreakScores.Add(clientId, 0);
        }
    }

    private void CompleteFinalStatueBreakObjective(FinalMatchRuleDefinition definition)
    {
        // Resolve the winner from final statue scores when the final timer reaches zero.
        if (!IsServer || matchStateController == null)
        {
            return;
        }

        if (NetworkManager.Singleton != null)
        {
            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                EnsureFinalStatueScoreEntry(clientId);
            }
        }

        matchStateController.CompleteFinalScoreObjective(finalStatueBreakScores, ResolveFinalRuleContext(definition));
    }

    private IEnumerator RespawnBoxItemAfterDelay(int slotId)
    {
        // Respawn a destructible box after a short delay during the main match.
        yield return new WaitForSeconds(boxRespawnDelay);

        if (!IsServer || matchStateController == null || matchStateController.State.Value != NetworkMatchState.MatchMain)
        {
            yield break;
        }

        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        slot.RespawnRoutine = null;
        ActivateBoxItem(slotId, GetRandomSpawnPosition(), ChooseRandomBoxVariant());
    }

    private int DropBoxLoot(Vector3 dropCenter, BoxLootKind lootKind, PlayerStatType[] lootStats, FunctionalPickupType[] lootFunctionalTypes, string[] lootEquipmentIds)
    {
        // Dispatch a broken box's pre-rolled loot into the matching pickup activation path.
        return lootKind switch
        {
            BoxLootKind.Functional => DropFunctionalBoxLoot(dropCenter, lootFunctionalTypes),
            BoxLootKind.Equipment => DropEquipmentBoxLoot(dropCenter, lootEquipmentIds),
            BoxLootKind.Bomb => 0,
            _ => DropStatBoxLoot(dropCenter, lootStats)
        };
    }

    private int DropStatBoxLoot(Vector3 dropCenter, PlayerStatType[] lootStats)
    {
        // Spawn stat loot with an outward launch impulse from the broken box.
        int lootCount = lootStats != null ? lootStats.Length : 0;
        for (int i = 0; i < lootCount; i++)
        {
            ActivateStatPickup(
                GetNextLootPickupSlotId(),
                lootStats[i],
                ResolveBoxLootLaunchPosition(dropCenter),
                respawnOnCollect: false,
                initialVelocity: ResolveBoxLootLaunchVelocity(i, lootCount));
        }

        return lootCount;
    }

    private int DropFunctionalBoxLoot(Vector3 dropCenter, FunctionalPickupType[] lootFunctionalTypes)
    {
        // Spawn functional loot with the same outward launch impulse as stat loot.
        int lootCount = lootFunctionalTypes != null ? lootFunctionalTypes.Length : 0;
        for (int i = 0; i < lootCount; i++)
        {
            ActivateFunctionalPickup(
                GetNextLootPickupSlotId(),
                lootFunctionalTypes[i],
                ResolveBoxLootLaunchPosition(dropCenter),
                respawnOnCollect: false,
                initialVelocity: ResolveBoxLootLaunchVelocity(i, lootCount));
        }

        return lootCount;
    }

    private int DropEquipmentBoxLoot(Vector3 dropCenter, string[] lootEquipmentIds)
    {
        // Spawn equipment loot with preserved full durability and no automatic respawn from the loot slot.
        int spawnedCount = 0;
        int lootCount = lootEquipmentIds != null ? lootEquipmentIds.Length : 0;
        for (int i = 0; i < lootCount; i++)
        {
            if (!EquipmentCatalog.TryGet(lootEquipmentIds[i], out EquipmentDefinition equipment))
            {
                continue;
            }

            ActivateEquipmentPickup(
                GetNextLootPickupSlotId(),
                equipment,
                ResolveBoxLootLaunchPosition(dropCenter),
                healthPercent: 1f,
                respawnOnDespawn: false,
                initialVelocity: ResolveBoxLootLaunchVelocity(i, lootCount));
            spawnedCount++;
        }

        return spawnedCount;
    }

    private int DropMixedBoxLoot(Vector3 dropCenter, int lootCount)
    {
        // Spawn each box loot slot from stat, equipment, or functional categories with equal probability.
        int resolvedCount = Mathf.Max(0, lootCount);
        int spawnedCount = 0;
        for (int i = 0; i < resolvedCount; i++)
        {
            if (DropSingleMixedBoxLoot(dropCenter, i, resolvedCount))
            {
                spawnedCount++;
            }
        }

        return spawnedCount;
    }

    private bool DropSingleMixedBoxLoot(Vector3 dropCenter, int index, int count)
    {
        // Pick one mixed-loot category and spawn it with the normal box spill impulse.
        Vector3 position = ResolveBoxLootLaunchPosition(dropCenter);
        Vector3 velocity = ResolveBoxLootLaunchVelocity(index, count);
        int category = Random.Range(0, 3);
        if (category == 1)
        {
            EquipmentDefinition equipment = EquipmentCatalog.GetRandom();
            if (equipment != null)
            {
                ActivateEquipmentPickup(
                    GetNextLootPickupSlotId(),
                    equipment,
                    position,
                    healthPercent: 1f,
                    respawnOnDespawn: false,
                    initialVelocity: velocity);
                return true;
            }
        }

        if (category == 2)
        {
            ActivateFunctionalPickup(
                GetNextLootPickupSlotId(),
                GetRandomFunctionalPickupType(),
                position,
                respawnOnCollect: false,
                initialVelocity: velocity);
            return true;
        }

        ActivateStatPickup(
            GetNextLootPickupSlotId(),
            GetRandomStatType(),
            position,
            respawnOnCollect: false,
            initialVelocity: velocity);
        return true;
    }

    private int ResolveBoxLootCount(BoxSlot slot)
    {
        // Preserve the box variant's intended loot count when sudden events override the loot category.
        if (slot == null)
        {
            return Mathf.Clamp(basicBoxLootCount, 0, 3);
        }

        int statCount = slot.LootStats != null ? slot.LootStats.Length : 0;
        int functionalCount = slot.LootFunctionalTypes != null ? slot.LootFunctionalTypes.Length : 0;
        int equipmentCount = slot.LootEquipmentIds != null ? slot.LootEquipmentIds.Length : 0;
        int resolvedCount = Mathf.Max(statCount, functionalCount, equipmentCount);
        return resolvedCount > 0 ? resolvedCount : Mathf.Clamp(basicBoxLootCount, 0, 3);
    }

    private BoxVariantDefinition ChooseRandomBoxVariant()
    {
        // Choose a box variant by inspector-configured spawn weight, falling back to the basic stat box.
        if (boxVariants == null || boxVariants.Length == 0)
        {
            return CreateFallbackBoxVariant();
        }

        float totalWeight = 0f;
        for (int i = 0; i < boxVariants.Length; i++)
        {
            BoxVariantDefinition variant = boxVariants[i];
            if (IsUsableBoxVariant(variant))
            {
                totalWeight += Mathf.Max(0f, variant.SpawnWeight);
            }
        }

        if (totalWeight <= 0f)
        {
            return CreateFallbackBoxVariant();
        }

        float roll = Random.Range(0f, totalWeight);
        for (int i = 0; i < boxVariants.Length; i++)
        {
            BoxVariantDefinition variant = boxVariants[i];
            if (!IsUsableBoxVariant(variant))
            {
                continue;
            }

            roll -= Mathf.Max(0f, variant.SpawnWeight);
            if (roll <= 0f)
            {
                return variant;
            }
        }

        return CreateFallbackBoxVariant();
    }

    private BoxVariantDefinition ResolveBoxVariant(BoxVariantDefinition variant)
    {
        // Return a usable variant object so box activation always has valid health, loot, and tint data.
        return IsUsableBoxVariant(variant) ? variant : CreateFallbackBoxVariant();
    }

    private BoxVariantDefinition ResolveBoxVariant(string boxId)
    {
        // Resolve a variant by id on clients so tint stays deterministic from server-synced box state.
        if (!string.IsNullOrWhiteSpace(boxId) && boxVariants != null)
        {
            for (int i = 0; i < boxVariants.Length; i++)
            {
                BoxVariantDefinition variant = boxVariants[i];
                if (variant != null && variant.BoxId == boxId)
                {
                    return ResolveBoxVariant(variant);
                }
            }
        }

        return CreateFallbackBoxVariant();
    }

    private bool IsUsableBoxVariant(BoxVariantDefinition variant)
    {
        // Accept only variants that have a stable id and a positive chance to spawn.
        return variant != null &&
            !string.IsNullOrWhiteSpace(variant.BoxId) &&
            variant.SpawnWeight > 0f;
    }

    private BoxVariantDefinition CreateFallbackBoxVariant()
    {
        // Preserve the original basic stat box behavior if the editable variant list is empty or invalid.
        return new BoxVariantDefinition
        {
            BoxId = "basic_stat_box",
            DisplayName = "Basic Stat Box",
            LootKind = BoxLootKind.Stat,
            LootCount = Mathf.Clamp(basicBoxLootCount, 0, 3),
            MaxHealth = Mathf.Max(1f, basicBoxMaxHealth),
            SpawnWeight = 1f,
            TintColor = Color.white
        };
    }

    private void PreRollBoxLoot(BoxSlot slot, BoxVariantDefinition variant)
    {
        // Pre-roll all loot data at spawn time so destroyed boxes already know what they will drop.
        int lootCount = Mathf.Max(0, variant.LootCount);
        slot.LootStats = System.Array.Empty<PlayerStatType>();
        slot.LootFunctionalTypes = System.Array.Empty<FunctionalPickupType>();
        slot.LootEquipmentIds = System.Array.Empty<string>();

        switch (variant.LootKind)
        {
            case BoxLootKind.Functional:
                slot.LootFunctionalTypes = GenerateFunctionalLoot(lootCount);
                break;
            case BoxLootKind.Equipment:
                slot.LootEquipmentIds = GenerateEquipmentLoot(lootCount);
                break;
            case BoxLootKind.Bomb:
                break;
            default:
                slot.LootStats = GenerateStatLoot(lootCount);
                break;
        }
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

    private FunctionalPickupType[] GenerateFunctionalLoot(int lootCount)
    {
        // Pre-roll functional pickups such as the current basic heal item.
        int resolvedCount = Mathf.Max(0, lootCount);
        FunctionalPickupType[] lootTypes = new FunctionalPickupType[resolvedCount];
        for (int i = 0; i < resolvedCount; i++)
        {
            lootTypes[i] = GetRandomFunctionalPickupType();
        }

        return lootTypes;
    }

    private string[] GenerateEquipmentLoot(int lootCount)
    {
        // Pre-roll equipment ids from the Resources equipment catalog for equipment box drops.
        int resolvedCount = Mathf.Max(0, lootCount);
        List<string> equipmentIds = new(resolvedCount);
        for (int i = 0; i < resolvedCount; i++)
        {
            EquipmentDefinition equipment = EquipmentCatalog.GetRandom();
            if (equipment != null && !string.IsNullOrWhiteSpace(equipment.EquipmentId))
            {
                equipmentIds.Add(equipment.EquipmentId);
            }
        }

        return equipmentIds.ToArray();
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
        bool shouldRespawnEquipment = despawnedKind == PickupKind.Equipment && slot.RespawnEquipmentOnDespawn;

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
        Vector3 breakPosition = slot.Position;
        bool shouldRespawn = slot.RespawnEquipmentOnDespawn;
        DeactivatePickup(slotId);
        PlayFieldEquipmentBreakSfxClientRpc(
            breakPosition,
            new FixedString64Bytes(equipmentId ?? string.Empty));

        if (shouldRespawn && matchStateController != null && matchStateController.State.Value == NetworkMatchState.MatchMain)
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
        PlayerEquipment equipment = ResolveLocalPlayerEquipment();
        return equipment != null && equipment.CanCollectItems;
    }

    private static bool TryGetLocalCollectionPosition(out Vector3 position)
    {
        // Prefer the owned Network PlayerObject, falling back to offline test controllers.
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.SpawnManager != null &&
            NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject() != null)
        {
            position = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject().transform.position;
            return true;
        }

        ThirdPersonController controller = FindLocalController();
        if (controller != null)
        {
            position = controller.transform.position;
            return true;
        }

        position = default;
        return false;
    }

    private static PlayerEquipment ResolveLocalPlayerEquipment()
    {
        // Resolve equipment from the owned Network PlayerObject before using offline fallbacks.
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.SpawnManager != null &&
            NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject() != null &&
            NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject().TryGetComponent(out PlayerEquipment equipment))
        {
            return equipment;
        }

        ThirdPersonController controller = FindLocalController();
        return controller != null ? controller.GetComponent<PlayerEquipment>() : null;
    }

    private static ThirdPersonController FindLocalController()
    {
        // Find the locally controlled player controller for offline and transitional scenes.
        ThirdPersonController[] controllers = FindObjectsByType<ThirdPersonController>(FindObjectsSortMode.None);
        for (int i = 0; i < controllers.Length; i++)
        {
            if (controllers[i] != null && controllers[i].HasLocalControl)
            {
                return controllers[i];
            }
        }

        return null;
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
            SetBoxVisualClientRpc(pair.Key, slot.Active, slot.Position, healthPercent, new FixedString64Bytes(slot.BoxId ?? string.Empty));
        }

        foreach (KeyValuePair<int, PenguinSlot> pair in penguinSlots)
        {
            PenguinSlot slot = pair.Value;
            SendPenguinVisualState(pair.Key, slot, snap: false, playDeath: slot.Visible && !slot.Alive);
        }

        if (activeSuddenEvent != SuddenEventType.None && IsSuddenEventActive())
        {
            PlaySuddenEventBgmClientRpc(activeSuddenEvent);
        }
    }

    [ClientRpc]
    private void PlaySuddenEventWarningClientRpc()
    {
        // Play the event-start alarm once for every client present when the event activates.
        SoundManager.Instance?.PlaySuddenEventWarningSfx();
    }

    [ClientRpc]
    private void PlaySuddenEventBgmClientRpc(SuddenEventType eventType)
    {
        // Tell every client to play the BGM override associated with the active sudden event.
        SoundManager.Instance?.PlaySuddenEventBgm(eventType);
    }

    [ClientRpc]
    private void StopSuddenEventBgmClientRpc(bool revealBaseBgm)
    {
        // Tell every client whether to reveal or keep hiding the underlying match BGM after an event ends.
        SoundManager.Instance?.StopSuddenEventBgm(revealBaseBgm);
    }

    private void SendPenguinVisualState(int slotId, PenguinSlot slot, bool snap, bool playDeath)
    {
        // Broadcast one authoritative Penguin pose while keeping the server free of presentation-only objects.
        if (!IsServer || !IsSpawned || slot == null)
        {
            return;
        }

        SetPenguinVisualClientRpc(
            slotId,
            slot.Visible,
            slot.Alive,
            slot.Position,
            slot.Forward,
            slot.MoveSpeed,
            snap,
            playDeath);
    }

    [ClientRpc]
    private void SetPenguinVisualClientRpc(int slotId, bool visible, bool alive, Vector3 position, Vector3 forward, float moveSpeed, bool snap, bool playDeath)
    {
        // Create or reuse a local Penguin visual and apply the latest server movement or death state.
        PenguinSlot slot = GetOrCreatePenguinSlot(slotId);
        slot.Visible = visible;
        slot.Alive = alive;
        slot.Position = position;
        slot.Forward = forward;
        slot.MoveSpeed = moveSpeed;

        if (!visible)
        {
            slot.VisualController?.Hide();
            return;
        }

        EnsurePenguinVisual(slotId, slot);
        if (slot.VisualController == null)
        {
            return;
        }

        if (playDeath || !alive)
        {
            slot.VisualController.PlayDeath(position, forward);
            return;
        }

        if (slot.Visual == null || !slot.Visual.activeSelf)
        {
            slot.VisualController.ShowAlive(position, forward);
            return;
        }

        slot.VisualController.SetNetworkState(position, forward, moveSpeed, snap);
    }

    [ClientRpc]
    private void SpawnEquipmentHookVisualClientRpc(int hookVisualId, Vector3 origin, Vector3 hookTargetPoint, Vector3 playerPosition, float speed)
    {
        // Play a temporary hook tether visual on every client until a real hook asset is available.
        SimpleHookVisual.Spawn(hookVisualId, origin, hookTargetPoint, playerPosition + Vector3.up * 0.6f, speed);
    }

    [ClientRpc]
    private void PlayEquipmentHookAnimationClientRpc(ulong clientId)
    {
        // Play the hook action on the network avatar that owns this hook request.
        NetworkPlayerAvatarRelay.TryPlayHookAnimationForClient(clientId);
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
        if (kind == PickupKind.Stat && ApplyStatPickupVisualTexture(slot.Visual, statType))
        {
            UpdateEquipmentLowHealthSpark(slot);
            return;
        }

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
    private void SetBoxVisualClientRpc(int slotId, bool active, Vector3 position, float healthPercent, FixedString64Bytes boxId)
    {
        // Sync a destructible box visual from the server-managed box slot state.
        BoxSlot slot = GetOrCreateBoxSlot(slotId);
        slot.Active = active;
        slot.BoxId = boxId.ToString();
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
        ApplyBoxVisualHealthTexture(slot.Visual, healthPercent, slot.BoxId);
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

    [ClientRpc]
    private void PlayPickupEffectClientRpc(PickupEffectKind effectKind, Vector3 position)
    {
        // Spawn stat/heal pickup feedback on every client from the server-approved pickup result.
        PlayPickupOneShotEffect(ResolvePickupEffectPrefab(effectKind), position, effectKind.ToString());
    }

    [ClientRpc]
    private void PlayPickupSfxClientRpc(PickupSfxKind kind, ClientRpcParams rpcParams = default)
    {
        // Forward server-approved pickup feedback to the collecting client's shared 2D SFX channel.
        SoundManager.Instance?.PlayPickupSfx(kind);
    }

    [ClientRpc]
    private void PlayBoxHitEffectClientRpc(Vector3 position, bool destroyed)
    {
        // Spawn stone feedback and play either the damage or destruction sound on every client.
        PlayBoxHitOneShotEffect(ResolveBoxHitEffectPrefab(), position);
        PlayBoxDamageSound(destroyed, position);
    }

    [ClientRpc]
    private void PlayFieldEquipmentHitEffectClientRpc(Vector3 position, Vector3 hitDirection)
    {
        // Spawn the same directional Green hit feedback used by players on every observing client.
        PlayDirectionalHitOneShotEffect(
            ResolveEquipmentHitEffectPrefab(),
            position,
            hitDirection,
            equipmentHitEffectEulerOffset,
            equipmentHitEffectScale,
            equipmentHitEffectLifetime,
            "FieldEquipmentGreenHitEffect");
    }

    [ClientRpc]
    private void PlayFieldEquipmentBreakSfxClientRpc(Vector3 position, FixedString64Bytes equipmentId)
    {
        // Play a positional field-equipment break cue with an optional per-equipment audio override.
        EquipmentDefinition equipment = EquipmentCatalog.Get(equipmentId.ToString());
        SoundManager.Instance?.PlayWorldEquipmentBreakSfx(
            position,
            equipment != null ? equipment.BreakSfxClip : null);
    }

    private void PlayBoxDamageSound(bool destroyed, Vector3 position)
    {
        // Route synchronized statue damage sounds through the pooled 3D client SFX channel.
        SoundManager soundManager = SoundManager.Instance;
        if (soundManager == null)
        {
            return;
        }

        AudioClip clip = destroyed ? boxBreakSfxClip : boxHitSfxClip;
        float volumeScale = destroyed ? boxBreakSfxVolumeScale : boxHitSfxVolumeScale;
        soundManager.PlayWorldSfx(clip, position, volumeScale);
    }

    [ClientRpc]
    private void PlayPenguinHitEffectClientRpc(Vector3 position, Vector3 hitDirection)
    {
        // Spawn the same green directional hit VFX used by players for every damaged Penguin.
        PlayDirectionalHitOneShotEffect(
            ResolvePenguinHitEffectPrefab(),
            position,
            hitDirection,
            penguinHitEffectEulerOffset,
            penguinHitEffectScale,
            penguinHitEffectLifetime,
            "PenguinGreenHitEffect");
    }

    [ClientRpc]
    private void PlayPenguinDisappearEffectClientRpc(Vector3 position)
    {
        // Spawn the configured disappearance VFX when a defeated Penguin leaves the field.
        PlayPenguinDisappearOneShotEffect(ResolvePenguinDisappearEffectPrefab(), position);
    }

    [ClientRpc]
    private void PlayBombBoxExplosionEffectClientRpc(Vector3 position)
    {
        // Spawn the bomb box explosion VFX on every client after the server resolves damage.
        PlayBombBoxExplosionOneShotEffect(ResolveBombBoxExplosionEffectPrefab(), position);
    }

    private void PlayPickupOneShotEffect(GameObject effectPrefab, Vector3 position, string effectName)
    {
        // Instantiate a temporary pickup effect, force particle systems to play once, and clean it up.
        if (effectPrefab == null)
        {
            return;
        }

        GameObject effectObject = Instantiate(effectPrefab, position, Quaternion.Euler(pickupEffectEulerOffset));
        effectObject.name = $"Pickup{effectName}Effect";
        effectObject.transform.localScale *= Mathf.Max(0.01f, pickupEffectScale);
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
            main.loop = false;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }

        Destroy(effectObject, Mathf.Max(0.1f, pickupEffectLifetime));
    }

    private void PlayBoxHitOneShotEffect(GameObject effectPrefab, Vector3 position)
    {
        // Instantiate the box hit effect, force all child particles to play once, and clean it up.
        if (effectPrefab == null)
        {
            return;
        }

        GameObject effectObject = Instantiate(effectPrefab, position, Quaternion.Euler(boxHitEffectEulerOffset));
        effectObject.name = "BoxStoneHitEffect";
        effectObject.transform.localScale *= Mathf.Max(0.01f, boxHitEffectScale);
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
            main.loop = false;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }

        Destroy(effectObject, Mathf.Max(0.1f, boxHitEffectLifetime));
    }

    private void PlayDirectionalHitOneShotEffect(
        GameObject effectPrefab,
        Vector3 position,
        Vector3 hitDirection,
        Vector3 eulerOffset,
        float scale,
        float lifetime,
        string effectName)
    {
        // Instantiate, orient, play, and clean up a shared directional impact effect.
        if (effectPrefab == null)
        {
            return;
        }

        Vector3 resolvedDirection = ResolveDirectionalHitEffectDirection(hitDirection);
        Quaternion rotation = Quaternion.LookRotation(resolvedDirection, Vector3.up) * Quaternion.Euler(eulerOffset);
        GameObject effectObject = Instantiate(effectPrefab, position, rotation);
        effectObject.name = effectName;
        effectObject.transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);
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
            main.loop = false;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }

        Destroy(effectObject, Mathf.Max(0.1f, lifetime));
    }

    private void PlayPenguinDisappearOneShotEffect(GameObject effectPrefab, Vector3 position)
    {
        // Instantiate, play, and clean up one Penguin disappearance effect.
        if (effectPrefab == null)
        {
            return;
        }

        GameObject effectObject = Instantiate(effectPrefab, position, Quaternion.Euler(penguinDisappearEffectEulerOffset));
        effectObject.name = "PenguinDisappearEffect";
        effectObject.transform.localScale = Vector3.one * Mathf.Max(0.01f, penguinDisappearEffectScale);
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
            main.loop = false;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }

        Destroy(effectObject, Mathf.Max(0.1f, penguinDisappearEffectLifetime));
    }

    private void PlayBombBoxExplosionOneShotEffect(GameObject effectPrefab, Vector3 position)
    {
        // Instantiate the bomb explosion effect, force child particles to play once, and clean it up.
        if (effectPrefab == null)
        {
            return;
        }

        GameObject effectObject = Instantiate(effectPrefab, position, Quaternion.Euler(bombBoxExplosionEffectEulerOffset));
        effectObject.name = "BombBoxExplosionEffect";
        effectObject.transform.localScale *= Mathf.Max(0.01f, bombBoxExplosionEffectScale);
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
            main.loop = false;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }

        Destroy(effectObject, Mathf.Max(0.1f, bombBoxExplosionEffectLifetime));
    }

    private GameObject ResolvePickupEffectPrefab(PickupEffectKind effectKind)
    {
        // Select the correct pickup VFX prefab while keeping each subtype independently replaceable.
        return effectKind switch
        {
            PickupEffectKind.Healing => ResolveHealingPickupEffectPrefab(),
            PickupEffectKind.AttackUp => ResolveAttackUpEffectPrefab(),
            PickupEffectKind.DefenceUp => ResolveDefenceUpEffectPrefab(),
            _ => ResolveStatBuffEffectPrefab()
        };
    }

    private GameObject ResolveStatBuffEffectPrefab()
    {
        // Use the inspector-assigned buff VFX first, then fall back to Resources/Effects/CustomEffects.
        if (statBuffEffectPrefab != null)
        {
            return statBuffEffectPrefab;
        }

        if (!triedLoadDefaultStatBuffEffectPrefab)
        {
            triedLoadDefaultStatBuffEffectPrefab = true;
            resolvedDefaultStatBuffEffectPrefab = Resources.Load<GameObject>(DefaultStatBuffEffectResourcePath);
        }

        return resolvedDefaultStatBuffEffectPrefab;
    }

    private GameObject ResolveHealingPickupEffectPrefab()
    {
        // Use the inspector-assigned healing VFX first, then fall back to Resources/Effects/CustomEffects.
        if (healingPickupEffectPrefab != null)
        {
            return healingPickupEffectPrefab;
        }

        if (!triedLoadDefaultHealingPickupEffectPrefab)
        {
            triedLoadDefaultHealingPickupEffectPrefab = true;
            resolvedDefaultHealingPickupEffectPrefab = Resources.Load<GameObject>(DefaultHealingPickupEffectResourcePath);
        }

        return resolvedDefaultHealingPickupEffectPrefab;
    }

    private GameObject ResolveAttackUpEffectPrefab()
    {
        // Use the inspector-assigned attack buff VFX first, then fall back to CustomEffects/AttackUp.
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
        // Use the inspector-assigned defense buff VFX first, then fall back to CustomEffects/DefenceUp.
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

    private GameObject ResolveBoxHitEffectPrefab()
    {
        // Use the inspector-assigned box hit VFX first, then fall back to the shared stone hit prefab.
        if (boxHitEffectPrefab != null)
        {
            return boxHitEffectPrefab;
        }

        if (!triedLoadDefaultBoxHitEffectPrefab)
        {
            triedLoadDefaultBoxHitEffectPrefab = true;
            resolvedDefaultBoxHitEffectPrefab = Resources.Load<GameObject>(DefaultBoxHitEffectResourcePath);
        }

        return resolvedDefaultBoxHitEffectPrefab;
    }

    private GameObject ResolveEquipmentHitEffectPrefab()
    {
        // Use the field-equipment override first, then load the same Green hit resource used by players.
        if (equipmentHitEffectPrefab != null)
        {
            return equipmentHitEffectPrefab;
        }

        if (!triedLoadDefaultEquipmentHitEffectPrefab)
        {
            triedLoadDefaultEquipmentHitEffectPrefab = true;
            resolvedDefaultEquipmentHitEffectPrefab = Resources.Load<GameObject>(DefaultEquipmentHitEffectResourcePath);
        }

        return resolvedDefaultEquipmentHitEffectPrefab;
    }

    private GameObject ResolvePenguinHitEffectPrefab()
    {
        // Use an inspector override first, then load the same Green hit resource used by players.
        if (penguinHitEffectPrefab != null)
        {
            return penguinHitEffectPrefab;
        }

        if (!triedLoadDefaultPenguinHitEffectPrefab)
        {
            triedLoadDefaultPenguinHitEffectPrefab = true;
            resolvedDefaultPenguinHitEffectPrefab = Resources.Load<GameObject>(DefaultPenguinHitEffectResourcePath);
        }

        return resolvedDefaultPenguinHitEffectPrefab;
    }

    private GameObject ResolvePenguinDisappearEffectPrefab()
    {
        // Use an inspector override first, then load Penguin_Disappear Variant from Resources.
        if (penguinDisappearEffectPrefab != null)
        {
            return penguinDisappearEffectPrefab;
        }

        if (!triedLoadDefaultPenguinDisappearEffectPrefab)
        {
            triedLoadDefaultPenguinDisappearEffectPrefab = true;
            resolvedDefaultPenguinDisappearEffectPrefab = Resources.Load<GameObject>(DefaultPenguinDisappearEffectResourcePath);
        }

        return resolvedDefaultPenguinDisappearEffectPrefab;
    }

    private Vector3 ResolvePenguinDisappearEffectPoint(PenguinSlot slot)
    {
        // Place the disappearance effect around the visible center while keeping an editable world offset.
        return slot != null ? slot.Position + penguinDisappearEffectOffset : penguinDisappearEffectOffset;
    }

    private Vector3 ResolvePenguinHitEffectPoint(PenguinSlot slot, Vector3 requestedPoint)
    {
        // Use the server impact point, falling back to the visible center of the Penguin body.
        if (IsFinite(requestedPoint))
        {
            return requestedPoint;
        }

        return slot != null
            ? slot.Position + Vector3.up * Mathf.Max(0f, penguinTargetHeight)
            : Vector3.zero;
    }

    private static Vector3 ResolvePenguinHitEffectDirection(Vector3 requestedDirection)
    {
        // Normalize the incoming attack direction and provide a stable orientation when it is unavailable.
        return ResolveDirectionalHitEffectDirection(requestedDirection);
    }

    private Vector3 ResolveEquipmentHitEffectPoint(PickupSlot slot, Vector3 requestedPoint)
    {
        // Use the approved impact point and fall back to the field equipment's damage target height.
        if (IsFinite(requestedPoint))
        {
            return requestedPoint;
        }

        return slot != null
            ? slot.Position + Vector3.up * Mathf.Max(0f, equipmentTargetHeight)
            : Vector3.zero;
    }

    private static Vector3 ResolveEquipmentHitEffectDirection(Vector3 requestedDirection)
    {
        // Normalize field-equipment impact direction with the same fallback used by player hit effects.
        return ResolveDirectionalHitEffectDirection(requestedDirection);
    }

    private static Vector3 ResolveDirectionalHitEffectDirection(Vector3 requestedDirection)
    {
        // Normalize directional impact data and provide a stable forward orientation when it is unavailable.
        if (IsFinite(requestedDirection) && requestedDirection.sqrMagnitude > 0.0001f)
        {
            return requestedDirection.normalized;
        }

        return Vector3.forward;
    }

    private GameObject ResolveBombBoxExplosionEffectPrefab()
    {
        // Use an assigned explosion VFX first, then fall back to the CustomEffects resource prefab.
        if (bombBoxExplosionEffectPrefab != null)
        {
            return bombBoxExplosionEffectPrefab;
        }

        if (!triedLoadDefaultBombBoxExplosionEffectPrefab)
        {
            triedLoadDefaultBombBoxExplosionEffectPrefab = true;
            string resourcePath = string.IsNullOrWhiteSpace(bombBoxExplosionEffectResourcePath)
                ? DefaultBombBoxExplosionEffectResourcePath
                : bombBoxExplosionEffectResourcePath.Trim();
            resolvedDefaultBombBoxExplosionEffectPrefab = Resources.Load<GameObject>(resourcePath);
            if (resolvedDefaultBombBoxExplosionEffectPrefab == null &&
                resourcePath != DefaultBombBoxExplosionEffectResourcePath)
            {
                resolvedDefaultBombBoxExplosionEffectPrefab = Resources.Load<GameObject>(DefaultBombBoxExplosionEffectResourcePath);
            }
        }

        return resolvedDefaultBombBoxExplosionEffectPrefab;
    }

    private void CreateLocalVisualSlots()
    {
        // 클라이언트별 임시 Primitive 비주얼을 미리 만들어 RPC 표시 요청에 대비.
        for (int i = 0; i < statPickupCount; i++)
        {
            EnsureVisual(i, GetOrCreateSlot(i));
        }

        int finalObjectiveSlotIndex = ResolveFinalObjectiveSlotIndex(ResolveFinalMatchRuleDefinition());
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
            PickupKind.Stat => slot.StatType.ToString(),
            PickupKind.Equipment => slot.EquipmentId ?? string.Empty,
            PickupKind.Functional => slot.FunctionalType.ToString(),
            _ => string.Empty
        };
    }

    private GameObject CreatePickupVisual(PickupSlot slot)
    {
        // Instantiate a real equipment prefab when assigned, otherwise create a clear temporary primitive.
        if (slot.Kind == PickupKind.Stat)
        {
            return CreateStatPickupVisual(slot.StatType);
        }

        if (slot.Kind == PickupKind.Functional)
        {
            GameObject functionalVisual = CreateFunctionalPickupVisual(slot.FunctionalType);
            if (functionalVisual != null)
            {
                return functionalVisual;
            }
        }

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

    private GameObject CreateFunctionalPickupVisual(FunctionalPickupType functionalType)
    {
        // Build a camera-facing world sprite for each configured contact buff or healing item.
        Sprite sprite = ResolveFunctionalPickupVisualSprite(functionalType);
        if (sprite == null)
        {
            return null;
        }

        bool isBasicHeal = functionalType == FunctionalPickupType.BasicHeal;
        float targetHeight = isBasicHeal ? basicHealVisualWorldHeight : buffPotionVisualWorldHeight;
        Vector3 localOffset = isBasicHeal ? basicHealVisualLocalOffset : buffPotionVisualLocalOffset;
        string visualName = ResolveFunctionalPickupVisualName(functionalType);

        GameObject visual = new($"{visualName}Root");
        GameObject spriteObject = new(visualName);
        spriteObject.transform.SetParent(visual.transform, false);
        spriteObject.transform.localPosition = localOffset;

        SpriteRenderer spriteRenderer = spriteObject.AddComponent<SpriteRenderer>();
        spriteRenderer.sprite = sprite;
        spriteRenderer.color = Color.white;
        spriteRenderer.sortingOrder = functionalPickupSpriteSortingOrder;

        float spriteHeight = Mathf.Max(0.0001f, sprite.bounds.size.y);
        float uniformScale = Mathf.Max(0.01f, targetHeight) / spriteHeight;
        spriteObject.transform.localScale = Vector3.one * uniformScale;
        visual.AddComponent<PickupSpriteBillboard>();
        return visual;
    }

    private Sprite ResolveFunctionalPickupVisualSprite(FunctionalPickupType functionalType)
    {
        // Route each supported functional pickup type to its independently replaceable sprite asset.
        switch (functionalType)
        {
            case FunctionalPickupType.BasicHeal:
                return ResolveConfiguredFunctionalPickupSprite(
                    basicHealVisualSprite,
                    basicHealVisualResourcePath,
                    DefaultBasicHealVisualResourcePath,
                    "basic heal",
                    ref resolvedBasicHealVisualSprite,
                    ref triedLoadBasicHealVisualSprite);
            case FunctionalPickupType.AttackPowerBuff:
                return ResolveConfiguredFunctionalPickupSprite(
                    attackPowerBuffVisualSprite,
                    attackPowerBuffVisualResourcePath,
                    DefaultAttackPowerBuffVisualResourcePath,
                    "attack power buff",
                    ref resolvedAttackPowerBuffVisualSprite,
                    ref triedLoadAttackPowerBuffVisualSprite);
            case FunctionalPickupType.DamageReductionBuff:
                return ResolveConfiguredFunctionalPickupSprite(
                    damageReductionBuffVisualSprite,
                    damageReductionBuffVisualResourcePath,
                    DefaultDamageReductionBuffVisualResourcePath,
                    "damage reduction buff",
                    ref resolvedDamageReductionBuffVisualSprite,
                    ref triedLoadDamageReductionBuffVisualSprite);
            case FunctionalPickupType.MoveSpeedBuff:
                return ResolveConfiguredFunctionalPickupSprite(
                    moveSpeedBuffVisualSprite,
                    moveSpeedBuffVisualResourcePath,
                    DefaultMoveSpeedBuffVisualResourcePath,
                    "move speed buff",
                    ref resolvedMoveSpeedBuffVisualSprite,
                    ref triedLoadMoveSpeedBuffVisualSprite);
            default:
                return null;
        }
    }

    private static Sprite ResolveConfiguredFunctionalPickupSprite(
        Sprite inspectorSprite,
        string configuredResourcePath,
        string defaultResourcePath,
        string pickupLabel,
        ref Sprite cachedSprite,
        ref bool triedLoad)
    {
        // Prefer an Inspector override, then cache the configured Resources sprite with a default-path fallback.
        if (inspectorSprite != null)
        {
            return inspectorSprite;
        }

        if (triedLoad)
        {
            return cachedSprite;
        }

        triedLoad = true;
        string resourcePath = string.IsNullOrWhiteSpace(configuredResourcePath)
            ? defaultResourcePath
            : configuredResourcePath.Trim();
        cachedSprite = Resources.Load<Sprite>(resourcePath);
        if (cachedSprite == null && resourcePath != defaultResourcePath)
        {
            cachedSprite = Resources.Load<Sprite>(defaultResourcePath);
        }

        if (cachedSprite == null)
        {
            Debug.LogWarning($"[GameplayPickupManager] Functional pickup sprite not found type={pickupLabel} path={resourcePath}");
        }

        return cachedSprite;
    }

    private static string ResolveFunctionalPickupVisualName(FunctionalPickupType functionalType)
    {
        // Give generated sprite objects stable subtype names for runtime inspection and future prefab replacement.
        return functionalType switch
        {
            FunctionalPickupType.BasicHeal => "MedicineBottleSprite",
            FunctionalPickupType.AttackPowerBuff => "AttackPowerPotionSprite",
            FunctionalPickupType.DamageReductionBuff => "DefencePotionSprite",
            FunctionalPickupType.MoveSpeedBuff => "MoveSpeedPotionSprite",
            _ => "FunctionalPickupSprite"
        };
    }

    private GameObject CreateStatPickupVisual(PlayerStatType statType)
    {
        // Instantiate the shared stat item model and swap only its Image material texture by stat type.
        GameObject prefab = ResolveStatPickupVisualPrefab();
        GameObject visual = new("StatPickupVisualRoot");
        GameObject model = prefab != null ? Instantiate(prefab, visual.transform) : GameObject.CreatePrimitive(PrimitiveType.Sphere);
        model.name = "StatPickupModel";
        model.transform.SetParent(visual.transform, false);

        ApplyStatPickupVisualTexture(visual, statType);
        NormalizeStatPickupVisualBounds(visual, model.transform);
        return visual;
    }

    private GameObject ResolveStatPickupVisualPrefab()
    {
        // Load the shared stat item FBX from Resources once so every stat can reuse the same model.
        if (triedLoadStatPickupVisualPrefab)
        {
            return resolvedStatPickupVisualPrefab;
        }

        triedLoadStatPickupVisualPrefab = true;
        resolvedStatPickupVisualPrefab = Resources.Load<GameObject>(statPickupVisualResourcePath);
        if (resolvedStatPickupVisualPrefab == null)
        {
            Debug.LogWarning($"[GameplayPickupManager] Stat pickup visual prefab not found path={statPickupVisualResourcePath}");
        }

        return resolvedStatPickupVisualPrefab;
    }

    private bool ApplyStatPickupVisualTexture(GameObject visual, PlayerStatType statType)
    {
        // Apply the stat-specific icon texture to the FBX material named Image.
        if (visual == null)
        {
            return false;
        }

        Texture2D texture = ResolveStatPickupTexture(statType);
        if (texture == null)
        {
            return false;
        }

        bool applied = false;
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < renderers.Length; i++)
        {
            applied |= ApplyStatTextureToRendererMaterials(renderers[i], texture);
        }

        if (!applied && !warnedMissingStatPickupMaterial)
        {
            warnedMissingStatPickupMaterial = true;
            Debug.LogWarning($"[GameplayPickupManager] Stat pickup material '{statPickupTextureMaterialName}' was not found under {statPickupVisualResourcePath}.");
        }

        return applied;
    }

    private Texture2D ResolveStatPickupTexture(PlayerStatType statType)
    {
        // Resolve and cache the texture that represents one PlayerStatType.
        if (resolvedStatPickupTextures.TryGetValue(statType, out Texture2D texture))
        {
            return texture;
        }

        if (triedLoadStatPickupTextures.Contains(statType))
        {
            return null;
        }

        triedLoadStatPickupTextures.Add(statType);
        string texturePath = $"{statPickupTextureResourceRoot}/{GetStatPickupTextureName(statType)}";
        texture = Resources.Load<Texture2D>(texturePath);
        if (texture == null)
        {
            Debug.LogWarning($"[GameplayPickupManager] Stat pickup texture not found stat={statType} path={texturePath}");
            return null;
        }

        resolvedStatPickupTextures[statType] = texture;
        return texture;
    }

    private static string GetStatPickupTextureName(PlayerStatType statType)
    {
        // Map stat types to the texture file names provided under Resources/fbx/Stat_Item.
        return statType switch
        {
            PlayerStatType.AttackPower => "atk",
            PlayerStatType.Defense => "def",
            PlayerStatType.Health => "hp",
            PlayerStatType.JumpForce => "jmp",
            PlayerStatType.FireRate => "rof",
            PlayerStatType.MoveSpeed => "spd",
            PlayerStatType.Weight => "wgt",
            _ => "atk"
        };
    }

    private bool ApplyStatTextureToRendererMaterials(Renderer renderer, Texture texture)
    {
        // Swap only the configured material slot so the rest of the FBX material setup stays intact.
        if (renderer == null || texture == null)
        {
            return false;
        }

        bool applied = false;
        Material[] materials = renderer.materials;
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null || !ShouldApplyStatTextureMaterial(material))
            {
                continue;
            }

            ApplyStatTextureToMaterial(material, texture);
            applied = true;
        }

        return applied;
    }

    private bool ShouldApplyStatTextureMaterial(Material material)
    {
        // Match the FBX's Image material, or all materials if the filter is intentionally left blank.
        if (material == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(statPickupTextureMaterialName))
        {
            return true;
        }

        return material.name.IndexOf(statPickupTextureMaterialName, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void NormalizeStatPickupVisualBounds(GameObject visual, Transform modelRoot)
    {
        // Normalize FBX size and pivot so the pickup is visible at the same world position as old primitives.
        if (visual == null || modelRoot == null)
        {
            return;
        }

        if (!normalizeStatPickupVisualBounds)
        {
            modelRoot.localScale = Vector3.Scale(modelRoot.localScale, statPickupVisualScale);
            return;
        }

        if (!TryGetRendererBounds(visual, out Bounds bounds) || bounds.size.y <= 0.0001f)
        {
            modelRoot.localScale = Vector3.Scale(modelRoot.localScale, statPickupVisualScale);
            return;
        }

        float targetHeight = Mathf.Max(0.01f, statPickupVisualTargetHeight);
        float scaleFactor = targetHeight / Mathf.Max(0.0001f, bounds.size.y);
        modelRoot.localScale = Vector3.Scale(modelRoot.localScale * scaleFactor, statPickupVisualScale);

        if (!TryGetRendererBounds(visual, out bounds))
        {
            return;
        }

        Vector3 localOffset = new(
            -bounds.center.x,
            -Mathf.Max(0f, pickupRestHeight) - bounds.min.y,
            -bounds.center.z);
        modelRoot.localPosition += localOffset;
    }

    private static bool TryGetRendererBounds(GameObject visual, out Bounds bounds)
    {
        // Calculate combined renderer bounds for imported models that keep meshes under child objects.
        bounds = default;
        if (visual == null)
        {
            return false;
        }

        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        bool hasBounds = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private void ApplyStatTextureToMaterial(Material material, Texture texture)
    {
        // Set base and optional emission texture properties while keeping the material color neutral.
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", texture);
        }

        if (material.HasProperty("_MainTex"))
        {
            material.SetTexture("_MainTex", texture);
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", Color.white);
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", Color.white);
        }

        if (!statPickupTextureAlsoEmission)
        {
            return;
        }

        if (material.HasProperty("_EmissionMap"))
        {
            material.SetTexture("_EmissionMap", texture);
        }

        if (material.HasProperty("_EmissionColor"))
        {
            material.SetColor("_EmissionColor", statPickupEmissionColor);
        }

        material.EnableKeyword("_EMISSION");
    }

    private void EnsureBoxVisual(int slotId, BoxSlot slot)
    {
        // Create the statue visual and configure its local player-blocking collision volume.
        if (slot.Visual != null)
        {
            return;
        }

        GameObject visual = CreateBoxVisual();
        visual.name = $"BoxItemVisual_{slotId}";
        RemoveVisualColliders(visual);
        ConfigureBoxBlockingCollider(visual);
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

        ParticleSystem sparkPrefab = ResolveEquipmentLowHealthSparkPrefab();
        if (sparkPrefab != null)
        {
            slot.EquipmentLowHealthSparkEffect = Instantiate(sparkPrefab, slot.Visual.transform);
            slot.EquipmentLowHealthSparkEffect.name = "EquipmentLowHealthRedSparkEffect";
            ApplyEquipmentLowHealthSparkTransform(slot.EquipmentLowHealthSparkEffect.transform);
            return;
        }

        GameObject sparkObject = new("EquipmentLowHealthRedSparkEffect");
        sparkObject.transform.SetParent(slot.Visual.transform, false);
        ApplyEquipmentLowHealthSparkTransform(sparkObject.transform);

        slot.EquipmentLowHealthSparkEffect = sparkObject.AddComponent<ParticleSystem>();
        ConfigureEquipmentLowHealthSparkEffect(slot.EquipmentLowHealthSparkEffect);
    }

    private void ApplyEquipmentLowHealthSparkTransform(Transform sparkTransform)
    {
        // Apply drop-equipment VFX placement, direction, and size without editing the shared effect prefab.
        if (sparkTransform == null)
        {
            return;
        }

        sparkTransform.localPosition = equipmentLowHealthSparkLocalOffset;
        sparkTransform.localRotation = Quaternion.Euler(equipmentLowHealthSparkLocalEulerAngles);
        sparkTransform.localScale = Vector3.one * Mathf.Max(0.01f, equipmentLowHealthSparkScale);
    }

    private ParticleSystem ResolveEquipmentLowHealthSparkPrefab()
    {
        // Use the inspector-assigned drop-equipment VFX first, then fall back to the shared Resources spark asset.
        if (equipmentLowHealthSparkPrefab != null)
        {
            return equipmentLowHealthSparkPrefab;
        }

        if (!triedLoadDefaultEquipmentSparkPrefab)
        {
            triedLoadDefaultEquipmentSparkPrefab = true;
            GameObject sparkPrefabObject = Resources.Load<GameObject>(DefaultEquipmentSparkResourcePath);
            resolvedDefaultEquipmentSparkPrefab = sparkPrefabObject != null
                ? sparkPrefabObject.GetComponentInChildren<ParticleSystem>(true)
                : null;
        }

        return resolvedDefaultEquipmentSparkPrefab;
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

    private void ConfigureBoxBlockingCollider(GameObject visual)
    {
        // Fit one non-trigger box collider to the rendered statue so CharacterControllers cannot pass through it.
        if (!boxBlocksPlayers || visual == null)
        {
            return;
        }

        Bounds localBounds = CalculateVisualLocalBounds(visual);
        GameObject colliderObject = new("PlayerBlockingCollider");
        colliderObject.layer = visual.layer;
        colliderObject.transform.SetParent(visual.transform, false);

        BoxCollider boxCollider = colliderObject.AddComponent<BoxCollider>();
        boxCollider.center = localBounds.center + basicBoxColliderCenterOffset;
        boxCollider.size = Vector3.Scale(localBounds.size, SanitizeColliderSizeMultiplier(basicBoxColliderSizeMultiplier));
        boxCollider.isTrigger = false;
    }

    private static Bounds CalculateVisualLocalBounds(GameObject visual)
    {
        // Convert every renderer's world bounds into root-local space for a stable generated collider.
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        Bounds localBounds = new(Vector3.zero, Vector3.one);
        bool hasBounds = false;
        Transform root = visual.transform;

        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            if (renderer == null)
            {
                continue;
            }

            Bounds worldBounds = renderer.bounds;
            Vector3 worldMin = worldBounds.min;
            Vector3 worldMax = worldBounds.max;
            for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
            {
                Vector3 worldCorner = new(
                    (cornerIndex & 1) == 0 ? worldMin.x : worldMax.x,
                    (cornerIndex & 2) == 0 ? worldMin.y : worldMax.y,
                    (cornerIndex & 4) == 0 ? worldMin.z : worldMax.z);
                Vector3 localCorner = root.InverseTransformPoint(worldCorner);
                if (!hasBounds)
                {
                    localBounds = new Bounds(localCorner, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    localBounds.Encapsulate(localCorner);
                }
            }
        }

        return localBounds;
    }

    private static Vector3 SanitizeColliderSizeMultiplier(Vector3 multiplier)
    {
        // Prevent zero or negative Inspector values from disabling an intended blocking axis.
        return new Vector3(
            Mathf.Max(0.01f, Mathf.Abs(multiplier.x)),
            Mathf.Max(0.01f, Mathf.Abs(multiplier.y)),
            Mathf.Max(0.01f, Mathf.Abs(multiplier.z)));
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

    private void ApplyBoxVisualHealthTexture(GameObject visual, float healthPercent, string boxId)
    {
        // Swap the box surface texture by health and tint it according to the box variant.
        if (visual == null)
        {
            return;
        }

        Color tintColor = ResolveBoxVariant(boxId).TintColor;
        Texture2D texture = ResolveBasicBoxTextureForHealth(healthPercent);
        if (texture == null)
        {
            ApplyBoxVisualFallbackColor(visual, healthPercent, tintColor);
            return;
        }

        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < renderers.Length; i++)
        {
            ApplyTextureToRendererMaterials(renderers[i], texture, tintColor);
        }
    }

    private Texture2D ResolveBasicBoxTextureForHealth(float healthPercent)
    {
        // Choose a box damage texture using the configured health thresholds.
        EnsureBasicBoxTexturesLoaded();
        float clampedHealthPercent = Mathf.Clamp01(healthPercent);
        float fullBreakThreshold = Mathf.Clamp01(basicBoxFullBreakThreshold);
        float halfBreakThreshold = Mathf.Clamp01(Mathf.Max(basicBoxHalfBreakThreshold, fullBreakThreshold));

        if (clampedHealthPercent <= fullBreakThreshold)
        {
            return resolvedBasicBoxFullBreakTexture != null ? resolvedBasicBoxFullBreakTexture : resolvedBasicBoxHalfBreakTexture;
        }

        if (clampedHealthPercent <= halfBreakThreshold)
        {
            return resolvedBasicBoxHalfBreakTexture != null ? resolvedBasicBoxHalfBreakTexture : resolvedBasicBoxCleanTexture;
        }

        return resolvedBasicBoxCleanTexture;
    }

    private void EnsureBasicBoxTexturesLoaded()
    {
        // Load the three basic box state textures from Resources once per manager instance.
        if (triedLoadBasicBoxTextures)
        {
            return;
        }

        triedLoadBasicBoxTextures = true;
        resolvedBasicBoxCleanTexture = Resources.Load<Texture2D>(basicBoxCleanTextureResourcePath);
        resolvedBasicBoxHalfBreakTexture = Resources.Load<Texture2D>(basicBoxHalfBreakTextureResourcePath);
        resolvedBasicBoxFullBreakTexture = Resources.Load<Texture2D>(basicBoxFullBreakTextureResourcePath);

        if (resolvedBasicBoxCleanTexture == null ||
            resolvedBasicBoxHalfBreakTexture == null ||
            resolvedBasicBoxFullBreakTexture == null)
        {
            Debug.LogWarning($"[GameplayPickupManager] Basic box texture load incomplete clean={resolvedBasicBoxCleanTexture != null} half={resolvedBasicBoxHalfBreakTexture != null} full={resolvedBasicBoxFullBreakTexture != null}");
        }
    }

    private void ApplyTextureToRendererMaterials(Renderer renderer, Texture texture, Color tintColor)
    {
        // Apply one texture and variant tint to every editable material slot on a renderer.
        if (renderer == null || texture == null)
        {
            return;
        }

        Material[] materials = renderer.materials;
        for (int i = 0; i < materials.Length; i++)
        {
            Material material = materials[i];
            if (material == null)
            {
                continue;
            }

            if (ShouldPreserveBoxMaterial(material))
            {
                continue;
            }

            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }

            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", texture);
            }

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", tintColor);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", tintColor);
            }
        }
    }

    private void ApplyBoxVisualFallbackColor(GameObject visual, float healthPercent, Color tintColor)
    {
        // Keep a visible damage cue if the configured box textures are missing.
        if (visual == null)
        {
            return;
        }

        Color damageColor = Color.Lerp(new Color(1f, 0.35f, 0.25f), Color.white, Mathf.Clamp01(healthPercent));
        Color color = new(damageColor.r * tintColor.r, damageColor.g * tintColor.g, damageColor.b * tintColor.b, tintColor.a);
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
            {
                Material material = renderers[i].material;
                if (ShouldPreserveBoxMaterial(material))
                {
                    continue;
                }

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

    private bool ShouldPreserveBoxMaterial(Material material)
    {
        // Keep model-specific materials such as Rock_Eye untouched by box damage texture swaps.
        return material != null &&
            !string.IsNullOrWhiteSpace(basicBoxTextureExcludedMaterialName) &&
            material.name.Contains(basicBoxTextureExcludedMaterialName);
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

    private PenguinSlot GetOrCreatePenguinSlot(int slotId)
    {
        // Reuse stable slot records so visuals remain pooled across repeated event tests.
        if (penguinSlots.TryGetValue(slotId, out PenguinSlot slot))
        {
            return slot;
        }

        slot = new PenguinSlot();
        penguinSlots[slotId] = slot;
        return slot;
    }

    private void EnsurePenguinVisual(int slotId, PenguinSlot slot)
    {
        // Lazily create one presentation-only prefab instance for each synchronized Penguin slot.
        if (slot == null || slot.Visual != null)
        {
            return;
        }

        GameObject prefab = ResolvePenguinVisualPrefab();
        GameObject visual;
        if (prefab != null)
        {
            visual = Instantiate(prefab, transform);
        }
        else
        {
            visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.transform.SetParent(transform, true);
        }

        visual.name = $"PenguinEventVisual_{slotId}";
        PenguinEnemyVisual controller = visual.GetComponent<PenguinEnemyVisual>() ?? visual.AddComponent<PenguinEnemyVisual>();
        Vector3 resolvedScale = Vector3.Scale(visual.transform.localScale, penguinVisualScale);
        controller.Configure(resolvedScale, penguinVisualPositionSharpness, penguinTurnSpeed);
        visual.SetActive(false);
        slot.Visual = visual;
        slot.VisualController = controller;
    }

    private GameObject ResolvePenguinVisualPrefab()
    {
        // Load the editable Penguin prefab once and preserve a fallback path for renamed inspector values.
        if (triedLoadPenguinVisualPrefab)
        {
            return resolvedPenguinVisualPrefab;
        }

        triedLoadPenguinVisualPrefab = true;
        string resourcePath = string.IsNullOrWhiteSpace(penguinVisualResourcePath)
            ? DefaultPenguinVisualResourcePath
            : penguinVisualResourcePath.Trim();
        resolvedPenguinVisualPrefab = Resources.Load<GameObject>(resourcePath);
        if (resolvedPenguinVisualPrefab == null && resourcePath != DefaultPenguinVisualResourcePath)
        {
            resolvedPenguinVisualPrefab = Resources.Load<GameObject>(DefaultPenguinVisualResourcePath);
        }

        if (resolvedPenguinVisualPrefab == null)
        {
            Debug.LogWarning($"[GameplayPickupManager] Penguin prefab not found at Resources/{resourcePath}; using a capsule fallback.");
        }

        return resolvedPenguinVisualPrefab;
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
            BoxId = "basic_stat_box",
            LootKind = BoxLootKind.Stat,
            MaxHealth = Mathf.Max(1f, basicBoxMaxHealth),
            CurrentHealth = Mathf.Max(1f, basicBoxMaxHealth),
            LootStats = System.Array.Empty<PlayerStatType>(),
            LootFunctionalTypes = System.Array.Empty<FunctionalPickupType>(),
            LootEquipmentIds = System.Array.Empty<string>()
        };
        boxSlots.Add(slotId, slot);
        return slot;
    }

    private static bool IsFinite(Vector3 value)
    {
        // Validate networked vector values before using them as effect spawn points.
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        // Reject NaN and infinity values that can break transforms or particle spawning.
        return !float.IsNaN(value) && !float.IsInfinity(value);
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

    private FunctionalPickupType GetRandomFunctionalPickupType()
    {
        // Choose from the editable functional pickup pool, ignoring None entries.
        if (functionalPickupPool == null || functionalPickupPool.Length == 0)
        {
            return FunctionalPickupType.BasicHeal;
        }

        for (int attempts = 0; attempts < functionalPickupPool.Length; attempts++)
        {
            FunctionalPickupType candidate = functionalPickupPool[Random.Range(0, functionalPickupPool.Length)];
            if (candidate != FunctionalPickupType.None)
            {
                return candidate;
            }
        }

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

            if (equipmentId == "balanced_hitscan")
            {
                return new Color(0.25f, 0.85f, 1f);
            }

            if (equipmentId == "balanced_canon")
            {
                return new Color(1f, 0.55f, 0.1f);
            }

            return new Color(0.95f, 0.95f, 1f);
        }

        if (kind == PickupKind.Functional)
        {
            return functionalType switch
            {
                FunctionalPickupType.BasicHeal => new Color(1f, 0.15f, 0.4f),
                FunctionalPickupType.AttackPowerBuff => new Color(1f, 0.55f, 0.05f),
                FunctionalPickupType.DamageReductionBuff => new Color(0.2f, 0.45f, 1f),
                FunctionalPickupType.MoveSpeedBuff => new Color(0.1f, 1f, 0.55f),
                FunctionalPickupType.AutoFireBuff => new Color(1f, 0.95f, 0.1f),
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

public sealed class PickupSpriteBillboard : MonoBehaviour
{
    [SerializeField] private bool keepUpright = true;

    private Camera targetCamera;

    private void OnEnable()
    {
        // Resolve the active gameplay camera and orient the sprite immediately when it appears.
        ResolveTargetCamera();
        FaceTargetCamera();
    }

    private void LateUpdate()
    {
        // Follow camera movement after gameplay transforms finish updating for the frame.
        if (targetCamera == null || !targetCamera.isActiveAndEnabled)
        {
            ResolveTargetCamera();
        }

        FaceTargetCamera();
    }

    private void ResolveTargetCamera()
    {
        // Prefer Unity's MainCamera tag so each client faces its own active gameplay camera.
        targetCamera = Camera.main;
    }

    private void FaceTargetCamera()
    {
        // Point the sprite plane at the camera while optionally preserving a world-up orientation.
        if (targetCamera == null)
        {
            return;
        }

        Vector3 direction = targetCamera.transform.position - transform.position;
        if (keepUpright)
        {
            direction.y = 0f;
        }

        if (direction.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
    }
}
