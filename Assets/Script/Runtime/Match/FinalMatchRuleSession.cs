using System;
using System.Collections.Generic;
using UnityEngine;

// Owns the selected final rule, its handler and score ledger without owning Netcode or world slots.
internal sealed class FinalMatchRuleSession
{
    private readonly IFinalMatchRuleActions actions;
    private readonly Func<FinalMatchRuleDefinition> resolveDefinition;
    private readonly Dictionary<ulong, int> statueBreakScores = new();
    private FinalMatchRuleDefinition selectedDefinition;
    private IFinalMatchRuleHandler activeHandler;

    internal FinalMatchRuleDefinition SelectedDefinition => selectedDefinition;
    internal bool HasActiveRule => activeHandler != null;
    internal FinalMatchRuleType ActiveRuleType => activeHandler != null
        ? activeHandler.RuleType
        : FinalMatchRuleType.FirstObjectivePickup;

    internal FinalMatchRuleSession(IFinalMatchRuleActions actions, Func<FinalMatchRuleDefinition> resolveDefinition)
    {
        // Bind world operations and the unchanged definition resolver for this manager lifetime.
        this.actions = actions;
        this.resolveDefinition = resolveDefinition;
    }

    internal void SelectForTransition()
    {
        // Select once at the original transition point after player transforms have been captured.
        selectedDefinition = resolveDefinition();
    }

    internal FinalMatchRuleDefinition EnsureSelectedDefinition()
    {
        // Preserve the existing null-only fallback, including re-resolution when no asset exists.
        if (selectedDefinition == null)
        {
            selectedDefinition = resolveDefinition();
        }

        return selectedDefinition;
    }

    internal float ResolveDuration(float fallbackDuration)
    {
        // Reuse the transition selection so the stage and the timer refer to the same rule.
        FinalMatchRuleDefinition definition = EnsureSelectedDefinition();
        return definition != null ? definition.ResolveDuration(fallbackDuration) : Mathf.Max(0f, fallbackDuration);
    }

    internal void StartSelectedRule()
    {
        // Start only after the owner has completed its existing stage-preparation sequence.
        activeHandler = CreateHandler(selectedDefinition);
        activeHandler.Start(selectedDefinition);
    }

    internal void ResolveOnTimer()
    {
        // Give the active rule its original completion callback before the Result transition.
        activeHandler?.ResolveOnTimer();
    }

    internal void Stop(bool clearPreparedRule)
    {
        // Stop the handler while retaining the selected stage data through Result when requested.
        activeHandler?.Stop();
        activeHandler = null;
        if (clearPreparedRule)
        {
            selectedDefinition = null;
        }
    }

    internal bool TryHandleBoxBroken(int slotId, ulong attackerClientId, bool isFinalScoreBox)
    {
        // Dispatch only the slot fact needed by a rule, leaving mutable world slots with the owner.
        return activeHandler != null &&
            activeHandler.TryHandleBoxBroken(slotId, attackerClientId, isFinalScoreBox);
    }

    internal void ResetScores()
    {
        // Preserve the local clear followed by server-only replicated reset and participant ordering.
        statueBreakScores.Clear();
        if (!actions.IsServer || !actions.HasNetworkManager)
        {
            return;
        }

        actions.ClearReplicatedScores();
        foreach (ulong clientId in actions.ConnectedClientIds)
        {
            EnsureScoreEntry(clientId);
        }
    }

    internal void RegisterStatueBreak(ulong clientId)
    {
        // Keep the existing entry publication before incrementing and publishing the awarded point.
        EnsureScoreEntry(clientId);
        statueBreakScores[clientId]++;
        actions.PublishScore(clientId, statueBreakScores[clientId]);
    }

    internal void EnsureScoreEntry(ulong clientId)
    {
        // Include zero-score players and preserve publication even for an already known entry.
        if (!statueBreakScores.ContainsKey(clientId))
        {
            statueBreakScores.Add(clientId, 0);
        }

        actions.PublishScore(clientId, statueBreakScores[clientId]);
    }

    internal int GetScore(ulong clientId)
    {
        // Read the existing log-facing score with the same zero fallback for an unknown client.
        return statueBreakScores.TryGetValue(clientId, out int score) ? score : 0;
    }

    internal void CompleteScoreObjective(FinalMatchRuleDefinition definition)
    {
        // Include connected zero-score participants before forwarding the unchanged winner policy.
        if (!actions.IsServer || !actions.HasMatch)
        {
            return;
        }

        if (actions.HasNetworkManager)
        {
            foreach (ulong clientId in actions.ConnectedClientIds)
            {
                EnsureScoreEntry(clientId);
            }
        }

        actions.CompleteScoreObjective(statueBreakScores, ResolveRuleContext(definition));
    }

    private IFinalMatchRuleHandler CreateHandler(FinalMatchRuleDefinition definition)
    {
        // Preserve the existing enum dispatch and first-objective fallback for unknown rule values.
        FinalMatchRuleType ruleType = definition != null
            ? definition.RuleType
            : FinalMatchRuleType.FirstObjectivePickup;
        return ruleType switch
        {
            FinalMatchRuleType.BreakStatues => new BreakStatuesFinalRuleHandler(this, actions),
            _ => new FirstObjectivePickupFinalRuleHandler(actions)
        };
    }

    private static string ResolveRuleContext(FinalMatchRuleDefinition definition)
    {
        // Retain the original context string passed to score completion and its logs.
        return definition != null && !string.IsNullOrWhiteSpace(definition.RuleId)
            ? definition.RuleId
            : "final-rule";
    }
}
