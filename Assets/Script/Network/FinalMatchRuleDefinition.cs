using UnityEngine;

[CreateAssetMenu(fileName = "FinalMatchRuleDefinition", menuName = "WoopRider/Final Match Rule Definition")]
public class FinalMatchRuleDefinition : ScriptableObject
{
    [Header("Identity")]
    [SerializeField] private string ruleId = "break_statues";
    [SerializeField] private FinalMatchRuleType ruleType = FinalMatchRuleType.BreakStatues;
    [SerializeField] private string displayName = "Break Statues";
    [SerializeField] private string noticeTitle = "Break as many statues as possible!";
    [SerializeField] private bool enabledInPool = true;
    [Min(0f)]
    [SerializeField] private float selectionWeight = 1f;

    [Header("Timing")]
    [Min(0f)]
    [Tooltip("0 uses MatchStateController's default final match duration.")]
    [SerializeField] private float durationOverride = 0f;

    [Header("Stage")]
    [SerializeField] private GameObject stagePrefab;
    [SerializeField] private Vector3 stageWorldPosition;
    [SerializeField] private Vector3 stageWorldEulerAngles;
    [Min(0f)]
    [SerializeField] private float fallbackPlayerSpawnMinimumSpacing = 4f;

    [Header("First Objective Pickup")]
    [SerializeField] private int objectiveSlotIndex = 1000;

    [Header("Break Statues")]
    [SerializeField] private int statueBoxCount = 12;
    [SerializeField] private int statueBoxSlotIdBase = 9000;
    [SerializeField] private float statueRespawnDelay = 2f;
    [SerializeField] private float statueMaxHealth = 100f;
    [SerializeField] private string statueBoxId = "basic_stat_box";
    [SerializeField] private Color statueTintColor = Color.white;
    [Min(0f)]
    [SerializeField] private float statueMinimumSpacing = 5f;

    public string RuleId => ruleId;
    public FinalMatchRuleType RuleType => ruleType;
    public string DisplayName => displayName;
    public string NoticeTitle => noticeTitle;
    public bool EnabledInPool => enabledInPool;
    public float SelectionWeight => selectionWeight;
    public GameObject StagePrefab => stagePrefab;
    public Vector3 StageWorldPosition => stageWorldPosition;
    public Quaternion StageWorldRotation => Quaternion.Euler(stageWorldEulerAngles);
    public float FallbackPlayerSpawnMinimumSpacing => Mathf.Max(0f, fallbackPlayerSpawnMinimumSpacing);
    public int ObjectiveSlotIndex => objectiveSlotIndex;
    public int StatueBoxCount => Mathf.Max(1, statueBoxCount);
    public int StatueBoxSlotIdBase => statueBoxSlotIdBase;
    public float StatueRespawnDelay => Mathf.Max(0f, statueRespawnDelay);
    public float StatueMaxHealth => Mathf.Max(1f, statueMaxHealth);
    public string StatueBoxId => string.IsNullOrWhiteSpace(statueBoxId) ? "basic_stat_box" : statueBoxId;
    public Color StatueTintColor => statueTintColor;
    public float StatueMinimumSpacing => Mathf.Max(0f, statueMinimumSpacing);

    public float ResolveDuration(float fallbackDuration)
    {
        // A non-positive override means the room-level final match timer remains authoritative.
        return durationOverride > 0f ? durationOverride : Mathf.Max(0f, fallbackDuration);
    }
}
