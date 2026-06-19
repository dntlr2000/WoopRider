using UnityEngine;

public enum SuddenEventType : byte
{
    None = 0,
    EndlessAutoFire = 1,
    MixedStatueLoot = 2
}

[CreateAssetMenu(fileName = "SuddenEventDefinition", menuName = "WoopRider/Sudden Event Definition")]
public class SuddenEventDefinition : ScriptableObject
{
    [SerializeField] private string eventId = "sudden_event";
    [SerializeField] private SuddenEventType eventType = SuddenEventType.None;
    [SerializeField] private string title = "Sudden event!";
    [SerializeField] private bool enabledInPool = true;
    [Min(0f)]
    [SerializeField] private float weight = 1f;
    [Min(0f)]
    [SerializeField] private float durationOverride;

    public string EventId => eventId;
    public SuddenEventType EventType => eventType;
    public string Title => title;
    public bool EnabledInPool => enabledInPool;
    public float Weight => weight;

    public float ResolveDuration(float defaultDuration)
    {
        // Use an event-specific duration when assigned, otherwise use the match manager default.
        return durationOverride > 0f ? durationOverride : defaultDuration;
    }
}
