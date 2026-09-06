using System.Collections;
using UnityEngine;

// Exposes only the live match context and effects needed by the event lifecycle.
internal interface ISuddenEventHost
{
    bool IsServer { get; }
    bool HasMatch { get; }
    NetworkMatchState MatchState { get; }
    float RemainingTime { get; }

    // Run the original IEnumerator on the existing MonoBehaviour, preserving immediate first execution.
    Coroutine StartCoroutine(IEnumerator routine);
    // Cancel the original coroutine on the same MonoBehaviour that started it.
    void StopCoroutine(Coroutine routine);
    // Start the existing Penguin implementation with its optional event definition.
    void StartPenguins(PenguinSuddenEventDefinition definition);
    // Stop the existing Penguin implementation at the original event boundaries.
    void StopPenguins();
    // Apply the original repeated auto-fire buff with its unchanged source tag.
    void ApplyAutoFireUntil(float endTime);
    // Forward an event notice to the existing match controller when one is present.
    void ShowNotice(string title, float duration);
    // Issue the original warning RPC at the same point in activation.
    void PlayWarning();
    // Issue the original event BGM RPC with the same event enum.
    void PlayBgm(SuddenEventType eventType);
    // Issue the original BGM-stop RPC with the existing base-music reveal policy.
    void StopBgm(bool revealBaseBgm);
}

// Carries current serialized settings by value while retaining the original asset and array references.
internal readonly struct SuddenEventSettings
{
    internal readonly bool Enabled;
    internal readonly float StartDelay;
    internal readonly float Duration;
    internal readonly bool LoadDefinitionsFromResources;
    internal readonly SuddenEventDefinition[] Definitions;
    internal readonly SuddenEventType[] FallbackTypes;

    internal SuddenEventSettings(bool enabled, float startDelay, float duration,
        bool loadDefinitionsFromResources, SuddenEventDefinition[] definitions, SuddenEventType[] fallbackTypes)
    {
        // Preserve the original values without cloning, filtering, validating or normalizing them.
        Enabled = enabled;
        StartDelay = startDelay;
        Duration = duration;
        LoadDefinitionsFromResources = loadDefinitionsFromResources;
        Definitions = definitions;
        FallbackTypes = fallbackTypes;
    }
}
