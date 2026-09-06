using System.Collections.Generic;

// Supplies only final-rule world actions and replication through the existing network owner.
internal interface IFinalMatchRuleActions
{
    bool IsServer { get; }
    bool HasMatch { get; }
    bool HasNetworkManager { get; }
    IEnumerable<ulong> ConnectedClientIds { get; }

    // Spawn the existing first-contact objective using the unchanged slot policy.
    void SpawnFirstObjective(FinalMatchRuleDefinition definition);
    // Spawn the existing score statues without exposing their private slot representation.
    void SpawnStatues(FinalMatchRuleDefinition definition);
    // Apply the existing score, deactivation and respawn sequence for one broken statue.
    void BreakStatueAndScheduleRespawn(int slotId, ulong attackerClientId, FinalMatchRuleDefinition definition);
    // Clear the existing network score list at its original reset boundary.
    void ClearReplicatedScores();
    // Publish one score using the unchanged NetworkList update policy.
    void PublishScore(ulong clientId, int score);
    // Let the existing match controller finalize a timed score objective.
    void CompleteScoreObjective(IReadOnlyDictionary<ulong, int> scores, string context);
}
