// Defines the existing final-rule lifecycle without exposing the manager's private box slots.
internal interface IFinalMatchRuleHandler
{
    FinalMatchRuleType RuleType { get; }
    // Begin the selected rule after stage preparation has completed.
    void Start(FinalMatchRuleDefinition definition);
    // Apply the rule's existing timer-expiration behavior.
    void ResolveOnTimer();
    // Intercept a broken box only when the active rule handles that slot.
    bool TryHandleBoxBroken(int slotId, ulong attackerClientId, bool isFinalScoreBox);
    // Release only rule-local state at the original stop boundary.
    void Stop();
}

internal sealed class FirstObjectivePickupFinalRuleHandler : IFinalMatchRuleHandler
{
    private readonly IFinalMatchRuleActions actions;

    internal FirstObjectivePickupFinalRuleHandler(IFinalMatchRuleActions actions)
    {
        // Retain only the world operations needed to start the first-contact objective.
        this.actions = actions;
    }

    public FinalMatchRuleType RuleType => FinalMatchRuleType.FirstObjectivePickup;

    public void Start(FinalMatchRuleDefinition definition)
    {
        // Delegate the original objective slot selection and pickup spawn to the network owner.
        actions.SpawnFirstObjective(definition);
    }

    public void ResolveOnTimer()
    {
        // Preserve the first-contact rule's existing no-op timer behavior.
    }

    public bool TryHandleBoxBroken(int slotId, ulong attackerClientId, bool isFinalScoreBox)
    {
        // This rule leaves every broken box to the ordinary pickup flow.
        return false;
    }

    public void Stop()
    {
        // Preserve the first-contact rule's existing no-op stop behavior.
    }
}

internal sealed class BreakStatuesFinalRuleHandler : IFinalMatchRuleHandler
{
    private readonly FinalMatchRuleSession session;
    private readonly IFinalMatchRuleActions actions;
    private FinalMatchRuleDefinition activeDefinition;

    internal BreakStatuesFinalRuleHandler(FinalMatchRuleSession session, IFinalMatchRuleActions actions)
    {
        // Bind the score owner and narrow world actions without depending on the network manager.
        this.session = session;
        this.actions = actions;
    }

    public FinalMatchRuleType RuleType => FinalMatchRuleType.BreakStatues;

    public void Start(FinalMatchRuleDefinition definition)
    {
        // Preserve definition assignment, score reset and statue spawning in that order.
        activeDefinition = definition;
        session.ResetScores();
        actions.SpawnStatues(activeDefinition);
    }

    public void ResolveOnTimer()
    {
        // Resolve the current score ledger through the existing match winner policy.
        session.CompleteScoreObjective(activeDefinition);
    }

    public bool TryHandleBoxBroken(int slotId, ulong attackerClientId, bool isFinalScoreBox)
    {
        // Handle only the same final-score slots accepted by the former nested implementation.
        if (!isFinalScoreBox)
        {
            return false;
        }

        actions.BreakStatueAndScheduleRespawn(slotId, attackerClientId, activeDefinition);
        return true;
    }

    public void Stop()
    {
        // Clear the selected asset at the same rule-stop boundary as before.
        activeDefinition = null;
    }
}
