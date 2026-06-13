using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class PlayableCharacterAnimatorControllerGenerator
{
    private const string OutputFolder = "Assets/Resources/PlayableCharacters";
    private const string MoveSpeedParameter = "MoveSpeed";
    private const string IsGroundedParameter = "IsGrounded";
    private const float MoveThreshold = 0.1f;

    private static readonly string[] RequiredStates =
    {
        "Run",
        "Jump",
        "Damaged",
        "GroundShoot",
        "GroundIdle",
        "Hook",
        "Fall",
        "Land"
    };

    [MenuItem("Tools/WoopRider/Generate Playable Character Animators")]
    public static void GenerateControllers()
    {
        // Generate all playable character controllers from the known FBX animation sets.
        EnsureOutputFolder();

        GenerateCharacterController(new CharacterDefinition(
            "Woop_WoopRiders",
            "Assets/Resources/fbx/Woop_WoopRiders.fbx",
            "Assets/Resources/PlayableCharacters/Woop_WoopRiders.controller",
            new Dictionary<string, string[]>
            {
                { "Run", new[] { "WoopSD_Run", "Run" } },
                { "Jump", new[] { "WoopSD_Jump", "Jump" } },
                { "Damaged", new[] { "WoopSD_Damaged", "Damaged" } },
                { "GroundShoot", new[] { "WoopSD_GroundShoot", "GroundShoot", "Shoot" } },
                { "GroundIdle", new[] { "WoopSD_GroundIdle", "GroundIdle", "Idle" } },
                { "Hook", new[] { "WoopSD_Hook", "Hook" } },
                { "Fall", new[] { "WoopSD_Fall", "Fall" } },
                { "Land", new[] { "WoopSD_Land", "Land" } }
            }));

        GenerateCharacterController(new CharacterDefinition(
            "Bangae_Playable",
            "Assets/Resources/fbx/Bangae_Playable.fbx",
            "Assets/Resources/PlayableCharacters/Bangae_Playable.controller",
            new Dictionary<string, string[]>
            {
                { "Run", new[] { "Bangae_Run", "Run" } },
                { "Jump", new[] { "Bangae_Jump", "Jump" } },
                { "Damaged", new[] { "Bangae_Damaged", "Damaged" } },
                { "GroundShoot", new[] { "Bangae_Shoot", "GroundShoot", "Shoot" } },
                { "GroundIdle", new[] { "Bangae_Idle", "GroundIdle", "Idle" } },
                { "Hook", new[] { "Bangae_Hook", "Hook" } },
                { "Fall", new[] { "Bangae_Fall", "Fall" } },
                { "Land", new[] { "Bangae_Land", "Land" } }
            }));

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[PlayableCharacterAnimatorControllerGenerator] Generated playable character animator controllers.");
    }

    private static void GenerateCharacterController(CharacterDefinition definition)
    {
        // Resolve clips, configure loops, and build one deterministic animator controller asset.
        ConfigureClipLoops(definition);
        Dictionary<string, AnimationClip> clipsByState = ResolveRequiredClips(definition);

        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(definition.OutputControllerPath) != null)
        {
            AssetDatabase.DeleteAsset(definition.OutputControllerPath);
        }

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(definition.OutputControllerPath);
        ConfigureParameters(controller);
        ConfigureBaseLayer(controller, clipsByState);

        EditorUtility.SetDirty(controller);
        Debug.Log($"[PlayableCharacterAnimatorControllerGenerator] Generated {definition.DisplayName} controller at {definition.OutputControllerPath}.");
    }

    private static void ConfigureClipLoops(CharacterDefinition definition)
    {
        // Persist loop settings for clips that should stay active while their states are held.
        ModelImporter importer = AssetImporter.GetAtPath(definition.ModelPath) as ModelImporter;
        if (importer == null)
        {
            throw new InvalidOperationException($"Model importer not found at {definition.ModelPath}.");
        }

        ModelImporterClipAnimation[] clipAnimations = importer.clipAnimations;
        if (clipAnimations == null || clipAnimations.Length == 0)
        {
            clipAnimations = importer.defaultClipAnimations;
        }

        bool changed = false;
        for (int i = 0; i < clipAnimations.Length; i++)
        {
            ModelImporterClipAnimation clipAnimation = clipAnimations[i];
            bool shouldLoop = ShouldLoopClip(definition, clipAnimation.name);
            if (clipAnimation.loopTime != shouldLoop)
            {
                clipAnimation.loopTime = shouldLoop;
                clipAnimations[i] = clipAnimation;
                changed = true;
            }
        }

        if (changed)
        {
            importer.clipAnimations = clipAnimations;
            importer.SaveAndReimport();
        }
    }

    private static bool ShouldLoopClip(CharacterDefinition definition, string clipName)
    {
        // Loop movement and held airborne clips while leaving action clips as one-shots.
        return MatchesState(definition, clipName, "Run") ||
               MatchesState(definition, clipName, "GroundIdle") ||
               MatchesState(definition, clipName, "Fall");
    }

    private static bool MatchesState(CharacterDefinition definition, string clipName, string stateName)
    {
        // Check whether an imported clip name maps to a controller state alias.
        if (!definition.ClipAliases.TryGetValue(stateName, out string[] aliases))
        {
            return false;
        }

        string normalizedClipName = NormalizeName(clipName);
        for (int i = 0; i < aliases.Length; i++)
        {
            string normalizedAlias = NormalizeName(aliases[i]);
            if (normalizedClipName == normalizedAlias || normalizedClipName.EndsWith(normalizedAlias, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, AnimationClip> ResolveRequiredClips(CharacterDefinition definition)
    {
        // Find one animation clip for every shared playable character state.
        Dictionary<string, AnimationClip> availableClips = LoadAnimationClips(definition.ModelPath);
        Dictionary<string, AnimationClip> resolvedClips = new();

        for (int i = 0; i < RequiredStates.Length; i++)
        {
            string stateName = RequiredStates[i];
            if (!definition.ClipAliases.TryGetValue(stateName, out string[] aliases))
            {
                throw new InvalidOperationException($"{definition.DisplayName} does not define aliases for {stateName}.");
            }

            AnimationClip clip = ResolveClip(availableClips, aliases);
            if (clip == null)
            {
                throw new InvalidOperationException(
                    $"Could not resolve clip for {definition.DisplayName}.{stateName}. Available clips: {string.Join(", ", availableClips.Keys)}");
            }

            resolvedClips[stateName] = clip;
        }

        return resolvedClips;
    }

    private static Dictionary<string, AnimationClip> LoadAnimationClips(string modelPath)
    {
        // Load embedded FBX animation clips by both display and normalized names.
        Dictionary<string, AnimationClip> clipsByName = new();
        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(modelPath);

        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] is not AnimationClip clip || clip.name.StartsWith("__preview__", StringComparison.Ordinal))
            {
                continue;
            }

            clipsByName[clip.name] = clip;
            clipsByName[NormalizeName(clip.name)] = clip;
        }

        return clipsByName;
    }

    private static AnimationClip ResolveClip(Dictionary<string, AnimationClip> availableClips, string[] aliases)
    {
        // Resolve a clip through exact, normalized, and suffix alias matching.
        for (int i = 0; i < aliases.Length; i++)
        {
            if (availableClips.TryGetValue(aliases[i], out AnimationClip exactClip))
            {
                return exactClip;
            }

            string normalizedAlias = NormalizeName(aliases[i]);
            if (availableClips.TryGetValue(normalizedAlias, out AnimationClip normalizedClip))
            {
                return normalizedClip;
            }
        }

        foreach (KeyValuePair<string, AnimationClip> pair in availableClips)
        {
            string normalizedClipName = NormalizeName(pair.Key);
            for (int i = 0; i < aliases.Length; i++)
            {
                string normalizedAlias = NormalizeName(aliases[i]);
                if (normalizedClipName.EndsWith(normalizedAlias, StringComparison.Ordinal) ||
                    normalizedClipName.Contains(normalizedAlias, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    private static void ConfigureParameters(AnimatorController controller)
    {
        // Add the common parameters expected by gameplay and future animation driver code.
        controller.AddParameter(new AnimatorControllerParameter
        {
            name = MoveSpeedParameter,
            type = AnimatorControllerParameterType.Float,
            defaultFloat = 0f
        });
        controller.AddParameter(new AnimatorControllerParameter
        {
            name = IsGroundedParameter,
            type = AnimatorControllerParameterType.Bool,
            defaultBool = true
        });
        controller.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Damaged", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Shoot", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Hook", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Land", AnimatorControllerParameterType.Trigger);
    }

    private static void ConfigureBaseLayer(AnimatorController controller, Dictionary<string, AnimationClip> clipsByState)
    {
        // Build the state machine transitions for grounded movement, airborne motion, and one-shot actions.
        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;

        Dictionary<string, AnimatorState> states = new()
        {
            { "GroundIdle", AddState(stateMachine, "GroundIdle", clipsByState["GroundIdle"], 240, 0) },
            { "Run", AddState(stateMachine, "Run", clipsByState["Run"], 500, 0) },
            { "Jump", AddState(stateMachine, "Jump", clipsByState["Jump"], 240, 180) },
            { "Fall", AddState(stateMachine, "Fall", clipsByState["Fall"], 500, 180) },
            { "Land", AddState(stateMachine, "Land", clipsByState["Land"], 760, 180) },
            { "GroundShoot", AddState(stateMachine, "GroundShoot", clipsByState["GroundShoot"], 240, -180) },
            { "Hook", AddState(stateMachine, "Hook", clipsByState["Hook"], 500, -180) },
            { "Damaged", AddState(stateMachine, "Damaged", clipsByState["Damaged"], 760, -180) }
        };

        stateMachine.defaultState = states["GroundIdle"];

        AddGroundMovementTransitions(states["GroundIdle"], states["Run"], states["Fall"]);
        AddGroundMovementTransitions(states["Run"], states["GroundIdle"], states["Fall"]);
        AddJumpTransitions(states["Jump"], states["Fall"], states["Land"]);
        AddFallAndLandTransitions(states["Fall"], states["Land"], states["GroundIdle"], states["Run"]);

        AddOneShotReturnTransitions(states["GroundShoot"], states["GroundIdle"], states["Run"], states["Fall"]);
        AddOneShotReturnTransitions(states["Hook"], states["GroundIdle"], states["Run"], states["Fall"]);
        AddOneShotReturnTransitions(states["Damaged"], states["GroundIdle"], states["Run"], states["Fall"]);

        AddAnyStateTriggerTransition(stateMachine, states["Damaged"], "Damaged", 0.03f);
        AddAnyStateTriggerTransition(stateMachine, states["Hook"], "Hook", 0.05f);
        AddAnyStateTriggerTransition(stateMachine, states["GroundShoot"], "Shoot", 0.03f);
        AddAnyStateTriggerTransition(stateMachine, states["Jump"], "Jump", 0.05f);
        AddAnyStateTriggerTransition(stateMachine, states["Land"], "Land", 0.05f);
    }

    private static AnimatorState AddState(AnimatorStateMachine stateMachine, string stateName, Motion motion, float x, float y)
    {
        // Create a named state with its imported motion at a stable graph position.
        AnimatorState state = stateMachine.AddState(stateName, new Vector3(x, y, 0f));
        state.motion = motion;
        return state;
    }

    private static void AddGroundMovementTransitions(AnimatorState fromState, AnimatorState alternateGroundState, AnimatorState fallState)
    {
        // Link idle and run through speed checks, and leave either state when airborne.
        AnimatorStateTransition toAlternate = fromState.AddTransition(alternateGroundState);
        ConfigureTransition(toAlternate, false, 0f, 0.1f);
        toAlternate.AddCondition(
            alternateGroundState.name == "Run" ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less,
            MoveThreshold,
            MoveSpeedParameter);
        toAlternate.AddCondition(AnimatorConditionMode.If, 0f, IsGroundedParameter);

        AnimatorStateTransition toFall = fromState.AddTransition(fallState);
        ConfigureTransition(toFall, false, 0f, 0.08f);
        toFall.AddCondition(AnimatorConditionMode.IfNot, 0f, IsGroundedParameter);
    }

    private static void AddJumpTransitions(AnimatorState jumpState, AnimatorState fallState, AnimatorState landState)
    {
        // Move from the jump impulse into either fall or land after the authored jump motion plays.
        AnimatorStateTransition toFall = jumpState.AddTransition(fallState);
        ConfigureTransition(toFall, true, 0.75f, 0.08f);
        toFall.AddCondition(AnimatorConditionMode.IfNot, 0f, IsGroundedParameter);

        AnimatorStateTransition toLand = jumpState.AddTransition(landState);
        ConfigureTransition(toLand, true, 0.75f, 0.08f);
        toLand.AddCondition(AnimatorConditionMode.If, 0f, IsGroundedParameter);
    }

    private static void AddFallAndLandTransitions(
        AnimatorState fallState,
        AnimatorState landState,
        AnimatorState idleState,
        AnimatorState runState)
    {
        // Land when grounded, then return to idle or run according to current movement speed.
        AnimatorStateTransition fallToLand = fallState.AddTransition(landState);
        ConfigureTransition(fallToLand, false, 0f, 0.08f);
        fallToLand.AddCondition(AnimatorConditionMode.If, 0f, IsGroundedParameter);

        AddGroundedReturnTransition(landState, idleState, AnimatorConditionMode.Less);
        AddGroundedReturnTransition(landState, runState, AnimatorConditionMode.Greater);

        AnimatorStateTransition landToFall = landState.AddTransition(fallState);
        ConfigureTransition(landToFall, false, 0f, 0.08f);
        landToFall.AddCondition(AnimatorConditionMode.IfNot, 0f, IsGroundedParameter);
    }

    private static void AddOneShotReturnTransitions(
        AnimatorState actionState,
        AnimatorState idleState,
        AnimatorState runState,
        AnimatorState fallState)
    {
        // Return action states to the appropriate movement state after their clip finishes.
        AddGroundedReturnTransition(actionState, idleState, AnimatorConditionMode.Less);
        AddGroundedReturnTransition(actionState, runState, AnimatorConditionMode.Greater);

        AnimatorStateTransition actionToFall = actionState.AddTransition(fallState);
        ConfigureTransition(actionToFall, true, 0.85f, 0.08f);
        actionToFall.AddCondition(AnimatorConditionMode.IfNot, 0f, IsGroundedParameter);
    }

    private static void AddGroundedReturnTransition(
        AnimatorState fromState,
        AnimatorState toState,
        AnimatorConditionMode speedCondition)
    {
        // Return to a grounded locomotion state only when the character is actually grounded.
        AnimatorStateTransition transition = fromState.AddTransition(toState);
        ConfigureTransition(transition, true, 0.85f, 0.08f);
        transition.AddCondition(speedCondition, MoveThreshold, MoveSpeedParameter);
        transition.AddCondition(AnimatorConditionMode.If, 0f, IsGroundedParameter);
    }

    private static void AddAnyStateTriggerTransition(
        AnimatorStateMachine stateMachine,
        AnimatorState targetState,
        string triggerName,
        float duration)
    {
        // Allow gameplay triggers to interrupt the current state with important one-shot actions.
        AnimatorStateTransition transition = stateMachine.AddAnyStateTransition(targetState);
        ConfigureTransition(transition, false, 0f, duration);
        transition.canTransitionToSelf = false;
        transition.AddCondition(AnimatorConditionMode.If, 0f, triggerName);
    }

    private static void ConfigureTransition(
        AnimatorStateTransition transition,
        bool hasExitTime,
        float exitTime,
        float duration)
    {
        // Normalize transition timing so all generated controllers behave consistently.
        transition.hasExitTime = hasExitTime;
        transition.exitTime = exitTime;
        transition.hasFixedDuration = true;
        transition.duration = duration;
        transition.offset = 0f;
        transition.interruptionSource = TransitionInterruptionSource.None;
        transition.orderedInterruption = true;
    }

    private static void EnsureOutputFolder()
    {
        // Create the Resources output folder path when it does not already exist.
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
        {
            AssetDatabase.CreateFolder("Assets", "Resources");
        }

        if (!AssetDatabase.IsValidFolder(OutputFolder))
        {
            AssetDatabase.CreateFolder("Assets/Resources", "PlayableCharacters");
        }
    }

    private static string NormalizeName(string value)
    {
        // Strip separators so Blender, FBX, and Unity clip names can be compared safely.
        return value
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("|", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private readonly struct CharacterDefinition
    {
        public readonly string DisplayName;
        public readonly string ModelPath;
        public readonly string OutputControllerPath;
        public readonly Dictionary<string, string[]> ClipAliases;

        public CharacterDefinition(
            string displayName,
            string modelPath,
            string outputControllerPath,
            Dictionary<string, string[]> clipAliases)
        {
            // Store all model paths and clip aliases needed to generate one controller.
            DisplayName = displayName;
            ModelPath = modelPath;
            OutputControllerPath = outputControllerPath;
            ClipAliases = clipAliases;
        }
    }
}
