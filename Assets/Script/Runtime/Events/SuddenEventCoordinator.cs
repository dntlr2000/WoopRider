using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Owns one match's sudden-event schedule and lifecycle while the existing owner executes world actions.
internal sealed class SuddenEventCoordinator
{
    private readonly ISuddenEventHost host;
    private readonly System.Func<SuddenEventSettings> readSettings;
    private Coroutine suddenEventRoutine;
    private SuddenEventType activeSuddenEvent = SuddenEventType.None;
    private float activeSuddenEventEndTime;

    // Read the serialized values at their original use sites, including changes while a timer is pending.
    private bool suddenEventsEnabled => readSettings().Enabled;
    private float suddenEventStartDelay => readSettings().StartDelay;
    private float suddenEventDuration => readSettings().Duration;
    private bool loadSuddenEventDefinitionsFromResources => readSettings().LoadDefinitionsFromResources;
    private SuddenEventDefinition[] suddenEventDefinitions => readSettings().Definitions;
    private SuddenEventType[] fallbackSuddenEventTypes => readSettings().FallbackTypes;

    internal SuddenEventType ActiveEventType => activeSuddenEvent;
    internal bool IsMixedBoxLootActive => activeSuddenEvent == SuddenEventType.MixedStatueLoot && IsActive();

    internal SuddenEventCoordinator(ISuddenEventHost host, System.Func<SuddenEventSettings> readSettings)
    {
        // Bind a manager-lifetime adapter without starting timers or loading resources early.
        this.host = host;
        this.readSettings = readSettings;
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

    internal void StartSchedule()
    {
        // Start the one-shot sudden event timer for the current main match.
        StopSchedule();
        if (!suddenEventsEnabled)
        {
            return;
        }

        suddenEventRoutine = host.StartCoroutine(TriggerSuddenEventAfterDelay(Mathf.Max(0f, suddenEventStartDelay)));
    }

    internal void StopSchedule()
    {
        // Stop pending or active sudden event state when the main match ends or the manager despawns.
        if (suddenEventRoutine != null)
        {
            host.StopCoroutine(suddenEventRoutine);
            suddenEventRoutine = null;
        }

        bool shouldLetMatchStateFadeAudio = host.HasMatch &&
            host.MatchState == NetworkMatchState.FinalTransition;
        if (host.IsServer && activeSuddenEvent != SuddenEventType.None && !shouldLetMatchStateFadeAudio)
        {
            host.StopBgm(revealBaseBgm: false);
        }

        host.StopPenguins();
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
        return host.IsServer &&
            suddenEventsEnabled &&
            host.HasMatch &&
            host.MatchState == NetworkMatchState.MatchMain;
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
            host.StartPenguins(selection.Definition as PenguinSuddenEventDefinition);
        }

        string title = ResolveSuddenEventTitle(selection);
        host.ShowNotice(title, 4f);
        host.PlayWarning();
        host.PlayBgm(eventKind);
        Debug.Log($"[GameplayPickupManager] Sudden event started kind={eventKind} duration={duration:0.0}s title={title}");
        Tick();
    }

    private float ResolveSuddenEventDuration(SuddenEventDefinition definition)
    {
        // Keep sudden events inside the remaining main-match window.
        float configuredDuration = Mathf.Max(0f, definition != null
            ? definition.ResolveDuration(suddenEventDuration)
            : suddenEventDuration);
        if (!host.HasMatch)
        {
            return configuredDuration;
        }

        float remainingMainTime = Mathf.Max(0f, host.RemainingTime);
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

    internal void Tick()
    {
        // Tick active sudden events and apply any repeating effects they need.
        if (activeSuddenEvent == SuddenEventType.None)
        {
            return;
        }

        if (!IsActive())
        {
            ClearActiveSuddenEvent();
            return;
        }

        if (activeSuddenEvent == SuddenEventType.EndlessAutoFire)
        {
            host.ApplyAutoFireUntil(activeSuddenEventEndTime);
        }
    }

    internal bool IsActive()
    {
        // Check whether the current sudden event should still affect main-match gameplay.
        return host.IsServer &&
            suddenEventsEnabled &&
            host.HasMatch &&
            host.MatchState == NetworkMatchState.MatchMain &&
            Time.time < activeSuddenEventEndTime;
    }

    private void ClearActiveSuddenEvent()
    {
        // Clear local event state once the event expires or the main match ends.
        if (activeSuddenEvent != SuddenEventType.None)
        {
            Debug.Log($"[GameplayPickupManager] Sudden event ended kind={activeSuddenEvent}");
            bool revealBaseBgm = host.HasMatch &&
                host.MatchState == NetworkMatchState.MatchMain;
            host.StopBgm(revealBaseBgm);
        }

        host.StopPenguins();
        activeSuddenEvent = SuddenEventType.None;
        activeSuddenEventEndTime = 0f;
    }
}
