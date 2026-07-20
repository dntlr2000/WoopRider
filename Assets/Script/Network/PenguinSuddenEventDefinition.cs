using UnityEngine;

[CreateAssetMenu(fileName = "PenguinFeast", menuName = "WoopRider/Sudden Events/Penguin Feast")]
public class PenguinSuddenEventDefinition : SuddenEventDefinition
{
    [Header("Penguin Spawn")]
    [Min(1)]
    [SerializeField] private int spawnCount = 5;
    [Min(0.1f)]
    [SerializeField] private float statueHealthMultiplier = 1.5f;
    [Min(0)]
    [SerializeField] private int statLootCount = 8;

    [Header("Penguin Movement")]
    [SerializeField] private Vector2 moveSpeedRange = new(1.2f, 2.2f);
    [SerializeField] private Vector2 decisionDurationRange = new(1.2f, 3.5f);
    [Range(0f, 1f)]
    [SerializeField] private float idleChance = 0.35f;

    public int SpawnCount => Mathf.Max(1, spawnCount);
    public float StatueHealthMultiplier => Mathf.Max(0.1f, statueHealthMultiplier);
    public int StatLootCount => Mathf.Max(0, statLootCount);
    public Vector2 MoveSpeedRange => ResolvePositiveRange(moveSpeedRange, 0f);
    public Vector2 DecisionDurationRange => ResolvePositiveRange(decisionDurationRange, 0.1f);
    public float IdleChance => Mathf.Clamp01(idleChance);

    private static Vector2 ResolvePositiveRange(Vector2 range, float minimum)
    {
        // Normalize editable min/max pairs so runtime random selection always receives a valid range.
        float min = Mathf.Max(minimum, Mathf.Min(range.x, range.y));
        float max = Mathf.Max(min, Mathf.Max(range.x, range.y));
        return new Vector2(min, max);
    }
}
