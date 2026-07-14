using System;
using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class SoundManager : MonoBehaviour
{
    [System.Serializable]
    private class SuddenEventBgmBinding
    {
        public SuddenEventType EventType = SuddenEventType.None;
        public AudioClip Clip;
    }

    public static SoundManager Instance { get; private set; }

    [Header("Sources")]
    [SerializeField] private AudioSource bgmSource;
    [SerializeField] private AudioSource sfxSource;
    [SerializeField] private AudioSource suddenEventBgmSource;

    [Header("BGM")]
    [SerializeField] private AudioClip[] bgmPlaylist;
    [SerializeField] private bool playBgmOnStart = true;
    [SerializeField] private int startingBgmIndex;
    [SerializeField] private bool loopBgm = true;

    [Header("Match State BGM")]
    [SerializeField] private bool useMatchStateBgm = true;
    [SerializeField] private int mainMatchBgmIndex = 0;
    [SerializeField] private int finalMatchBgmIndex = 2;
    [Min(0f)]
    [SerializeField] private float finalTransitionFadeOutDuration = 0.3f;

    [Header("Sudden Event BGM")]
    [SerializeField] private SuddenEventBgmBinding[] suddenEventBgms;
    [Range(0f, 1f)]
    [SerializeField] private float suddenEventBgmVolume = 0.85f;
    [Min(0f)]
    [SerializeField] private float suddenEventFadeInDuration = 0.3f;

    [Header("Channel Volume")]
    [Range(0f, 1f)]
    [SerializeField] private float bgmVolume = 0.7f;
    [Range(0f, 1f)]
    [SerializeField] private float sfxVolume = 1f;
    [SerializeField] private bool persistAcrossScenes = true;

    [Header("Server Mode")]
    [SerializeField] private bool muteInServerMode = true;

    private float masterVolume = 1f;
    private MatchStateController matchStateController;
    private int currentBgmIndex = -1;
    private bool serverAudioMuted;
    private bool suddenEventBgmActive;
    private SuddenEventType activeSuddenEventBgm = SuddenEventType.None;
    private Coroutine bgmFadeRoutine;
    private Coroutine suddenEventFadeRoutine;

    public float MasterVolume => masterVolume;
    public float BgmVolume => bgmVolume;
    public float SfxVolume => sfxVolume;

    private void Awake()
    {
        // Keep one sound manager alive and create missing AudioSources for scene objects with only this component.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        if (persistAcrossScenes)
        {
            DontDestroyOnLoad(gameObject);
        }

        EnsureAudioSources();
        if (ShouldMuteAudioForServer())
        {
            ApplyServerAudioMute();
            return;
        }

        ApplyConfiguredMasterVolume(GameConfigStore.MasterVolume);
    }

    private void OnEnable()
    {
        // Follow saved config changes so the settings slider immediately affects active audio.
        if (Instance == this)
        {
            if (ShouldMuteAudioForServer())
            {
                ApplyServerAudioMute();
                return;
            }

            GameConfigStore.MasterVolumeChanged += ApplyConfiguredMasterVolume;
            ApplyConfiguredMasterVolume(GameConfigStore.MasterVolume);
            BindMatchStateController();
        }
    }

    private void OnDisable()
    {
        // Remove the config listener when this manager is disabled or destroyed.
        if (Instance == this)
        {
            GameConfigStore.MasterVolumeChanged -= ApplyConfiguredMasterVolume;
            UnbindMatchStateController();
        }
    }

    private void Start()
    {
        // Start audio from the selected policy after all scene objects have had a chance to initialize.
        if (ShouldMuteAudioForServer())
        {
            ApplyServerAudioMute();
            return;
        }

        if (useMatchStateBgm)
        {
            BindMatchStateController();
            ApplyMatchStateBgm(matchStateController != null ? matchStateController.State.Value : NetworkMatchState.Lobby);
            return;
        }

        if (playBgmOnStart)
        {
            PlayBgm(startingBgmIndex);
        }
    }

    private void Update()
    {
        // Retry binding because network scene objects may spawn after this manager initializes.
        if (ShouldMuteAudioForServer())
        {
            ApplyServerAudioMute();
            UnbindMatchStateController();
            return;
        }

        if (useMatchStateBgm && matchStateController == null)
        {
            BindMatchStateController();
        }
    }

    public static void ApplyGlobalMasterVolume(float value)
    {
        // Let config code apply a master volume even before a SoundManager exists in the scene.
        float clampedValue = Mathf.Clamp01(value);
        if (Instance != null && Instance.ShouldMuteAudioForServer())
        {
            Instance.ApplyServerAudioMute();
            return;
        }

        if (Instance == null && IsServerRuntime())
        {
            AudioListener.volume = 0f;
            return;
        }

        AudioListener.volume = clampedValue;
        if (Instance != null)
        {
            Instance.ApplyConfiguredMasterVolume(clampedValue);
        }
    }

    public void PlayBgm(int index)
    {
        // Play one clip from the configured BGM playlist if the requested index is valid.
        if (ShouldMuteAudioForServer())
        {
            ApplyServerAudioMute();
            return;
        }

        if (bgmSource == null || bgmPlaylist == null || bgmPlaylist.Length == 0)
        {
            return;
        }

        StopFadeRoutine(ref bgmFadeRoutine);
        int resolvedIndex = Mathf.Clamp(index, 0, bgmPlaylist.Length - 1);
        AudioClip clip = bgmPlaylist[resolvedIndex];
        if (clip == null)
        {
            return;
        }

        if (currentBgmIndex == resolvedIndex && bgmSource.clip == clip && bgmSource.isPlaying)
        {
            return;
        }

        currentBgmIndex = resolvedIndex;
        bgmSource.clip = clip;
        bgmSource.loop = loopBgm;
        bgmSource.Play();
        RefreshSourceVolumes();
    }

    public void StopBgm()
    {
        // Stop the current background music while preserving the assigned clip.
        StopFadeRoutine(ref bgmFadeRoutine);
        if (bgmSource != null)
        {
            bgmSource.Stop();
        }

        currentBgmIndex = -1;
        RefreshSourceVolumes();
    }

    public void PlaySuddenEventBgm(SuddenEventType eventType)
    {
        // Play an event override BGM while keeping the base match BGM running silently underneath.
        if (ShouldMuteAudioForServer())
        {
            ApplyServerAudioMute();
            return;
        }

        AudioClip clip = ResolveSuddenEventBgmClip(eventType);
        if (clip == null || suddenEventBgmSource == null)
        {
            StopSuddenEventBgm();
            return;
        }

        if (suddenEventBgmActive &&
            activeSuddenEventBgm == eventType &&
            suddenEventBgmSource.clip == clip &&
            suddenEventBgmSource.isPlaying)
        {
            return;
        }

        suddenEventBgmActive = true;
        activeSuddenEventBgm = eventType;
        suddenEventBgmSource.clip = clip;
        suddenEventBgmSource.loop = true;
        suddenEventBgmSource.Play();
        RefreshSourceVolumes();
        StartSourceFadeIn(suddenEventBgmSource, Mathf.Clamp01(suddenEventBgmVolume), ref suddenEventFadeRoutine);
    }

    public void StopSuddenEventBgm()
    {
        // Stop the event override and fade the base BGM back in for normal event expiry.
        StopSuddenEventBgm(revealBaseBgm: true);
    }

    public void StopSuddenEventBgm(bool revealBaseBgm)
    {
        // Stop the event override channel and reveal the base BGM at its current playback point.
        bool hadEventBgm = suddenEventBgmActive;
        suddenEventBgmActive = false;
        activeSuddenEventBgm = SuddenEventType.None;
        StopFadeRoutine(ref suddenEventFadeRoutine);
        if (suddenEventBgmSource != null)
        {
            suddenEventBgmSource.Stop();
        }

        if (!revealBaseBgm)
        {
            if (suddenEventBgmSource != null)
            {
                suddenEventBgmSource.volume = 0f;
            }

            if (hadEventBgm && bgmSource != null)
            {
                bgmSource.volume = 0f;
            }

            return;
        }

        RefreshSourceVolumes();
        if (hadEventBgm && bgmSource != null && bgmSource.isPlaying)
        {
            StartSourceFadeIn(bgmSource, Mathf.Clamp01(bgmVolume), ref bgmFadeRoutine);
        }
    }

    public void SetBgmVolume(float value)
    {
        // Adjust the BGM channel volume independently from the saved master volume.
        bgmVolume = Mathf.Clamp01(value);
        RefreshSourceVolumes();
    }

    public void SetSfxVolume(float value)
    {
        // Adjust the SFX channel volume independently from the saved master volume.
        sfxVolume = Mathf.Clamp01(value);
        RefreshSourceVolumes();
    }

    public void PlaySfx(AudioClip clip)
    {
        // Play a one-shot SFX at normal scale through the shared SFX source.
        PlaySfx(clip, 1f);
    }

    public void PlaySfx(AudioClip clip, float volumeScale)
    {
        // Play a one-shot SFX through the shared source using the current SFX channel volume.
        if (ShouldMuteAudioForServer())
        {
            ApplyServerAudioMute();
            return;
        }

        if (sfxSource == null || clip == null)
        {
            return;
        }

        sfxSource.PlayOneShot(clip, Mathf.Clamp01(volumeScale));
    }

    private void ApplyConfiguredMasterVolume(float value)
    {
        // Apply the saved master volume globally and refresh channel-specific source volumes.
        if (ShouldMuteAudioForServer())
        {
            ApplyServerAudioMute();
            return;
        }

        serverAudioMuted = false;
        SetAudioSourcesMuted(false);
        masterVolume = Mathf.Clamp01(value);
        AudioListener.volume = masterVolume;
        RefreshSourceVolumes();
    }

    private void EnsureAudioSources()
    {
        // Create separate 2D AudioSources for BGM and SFX when they were not assigned in the inspector.
        if (bgmSource == null)
        {
            bgmSource = gameObject.AddComponent<AudioSource>();
        }

        if (sfxSource == null)
        {
            sfxSource = gameObject.AddComponent<AudioSource>();
        }

        if (suddenEventBgmSource == null)
        {
            suddenEventBgmSource = gameObject.AddComponent<AudioSource>();
        }

        ConfigureAudioSource(bgmSource, loopBgm);
        ConfigureAudioSource(sfxSource, false);
        ConfigureAudioSource(suddenEventBgmSource, true);
    }

    private void ConfigureAudioSource(AudioSource source, bool loop)
    {
        // Keep manager-owned sources as 2D sounds so BGM and UI sounds are not spatialized.
        if (source == null)
        {
            return;
        }

        source.playOnAwake = false;
        source.loop = loop;
        source.spatialBlend = 0f;
    }

    private void RefreshSourceVolumes()
    {
        // Push channel volume changes into the active AudioSources.
        if (serverAudioMuted)
        {
            StopFadeRoutine(ref bgmFadeRoutine);
            StopFadeRoutine(ref suddenEventFadeRoutine);
            if (bgmSource != null)
            {
                bgmSource.volume = 0f;
            }

            if (sfxSource != null)
            {
                sfxSource.volume = 0f;
            }

            if (suddenEventBgmSource != null)
            {
                suddenEventBgmSource.volume = 0f;
            }

            return;
        }

        if (bgmSource != null)
        {
            bgmSource.volume = suddenEventBgmActive ? 0f : Mathf.Clamp01(bgmVolume);
        }

        if (sfxSource != null)
        {
            sfxSource.volume = Mathf.Clamp01(sfxVolume);
        }

        if (suddenEventBgmSource != null)
        {
            suddenEventBgmSource.volume = suddenEventBgmActive ? Mathf.Clamp01(suddenEventBgmVolume) : 0f;
        }
    }

    private void StartSourceFadeIn(AudioSource source, float targetVolume, ref Coroutine routine)
    {
        // Fade in only event-related BGM transitions so normal match-state BGM starts remain immediate.
        if (source == null)
        {
            return;
        }

        StopFadeRoutine(ref routine);
        float duration = Mathf.Max(0f, suddenEventFadeInDuration);
        if (duration <= 0f || targetVolume <= 0f)
        {
            source.volume = Mathf.Clamp01(targetVolume);
            return;
        }

        routine = StartCoroutine(FadeInSourceVolume(source, Mathf.Clamp01(targetVolume), duration));
    }

    private IEnumerator FadeInSourceVolume(AudioSource source, float targetVolume, float duration)
    {
        // Raise one AudioSource from silence to its target volume over a short transition window.
        source.volume = 0f;
        float elapsed = 0f;
        while (elapsed < duration && source != null)
        {
            elapsed += Time.deltaTime;
            source.volume = Mathf.Lerp(0f, targetVolume, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        if (source != null)
        {
            source.volume = targetVolume;
        }
    }

    private void StartSourceFadeOut(AudioSource source, float duration, bool stopOnComplete, ref Coroutine routine)
    {
        // Fade out only transition-state BGM endings so normal lobby/result stops can remain immediate.
        if (source == null)
        {
            return;
        }

        StopFadeRoutine(ref routine);
        float resolvedDuration = Mathf.Max(0f, duration);
        if (resolvedDuration <= 0f || source.volume <= 0f)
        {
            source.volume = 0f;
            if (stopOnComplete)
            {
                source.Stop();
            }

            return;
        }

        routine = StartCoroutine(FadeOutSourceVolume(source, resolvedDuration, stopOnComplete));
    }

    private IEnumerator FadeOutSourceVolume(AudioSource source, float duration, bool stopOnComplete)
    {
        // Lower one AudioSource from its current volume to silence over a short transition window.
        float startVolume = source != null ? source.volume : 0f;
        float elapsed = 0f;
        while (elapsed < duration && source != null)
        {
            elapsed += Time.deltaTime;
            source.volume = Mathf.Lerp(startVolume, 0f, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        if (source != null)
        {
            source.volume = 0f;
            if (stopOnComplete)
            {
                source.Stop();
            }
        }
    }

    private void StopFadeRoutine(ref Coroutine routine)
    {
        // Stop a pending fade coroutine before another transition writes to the same source volume.
        if (routine == null)
        {
            return;
        }

        StopCoroutine(routine);
        routine = null;
    }

    private void BindMatchStateController()
    {
        // Subscribe to match state changes so BGM follows lobby, main match, final match, and result flow.
        if (!useMatchStateBgm || matchStateController != null || MatchStateController.Instance == null)
        {
            return;
        }

        matchStateController = MatchStateController.Instance;
        matchStateController.State.OnValueChanged += OnMatchStateChanged;
        ApplyMatchStateBgm(matchStateController.State.Value);
    }

    private void UnbindMatchStateController()
    {
        // Detach from match state changes before this manager is disabled or replaced.
        if (matchStateController == null)
        {
            return;
        }

        matchStateController.State.OnValueChanged -= OnMatchStateChanged;
        matchStateController = null;
    }

    private void OnMatchStateChanged(NetworkMatchState previousState, NetworkMatchState currentState)
    {
        // NetworkVariable callback used by both host and clients to update local music.
        ApplyMatchStateBgm(currentState);
    }

    private void ApplyMatchStateBgm(NetworkMatchState state)
    {
        // Play only the BGM that currently has a matching gameplay state; silence menu, waiting, transition, and result.
        switch (state)
        {
            case NetworkMatchState.MatchMain:
                PlayBgm(mainMatchBgmIndex);
                break;
            case NetworkMatchState.FinalTransition:
                FadeOutAudioForFinalTransition();
                break;
            case NetworkMatchState.FinalMatch:
                StopSuddenEventBgm(revealBaseBgm: false);
                PlayBgm(finalMatchBgmIndex);
                break;
            default:
                StopSuddenEventBgm(revealBaseBgm: false);
                StopBgm();
                break;
        }
    }

    private void FadeOutAudioForFinalTransition()
    {
        // Fade out whichever BGM channel is audible when the main match hands off to the final transition.
        if (suddenEventBgmActive && suddenEventBgmSource != null && suddenEventBgmSource.isPlaying)
        {
            suddenEventBgmActive = false;
            activeSuddenEventBgm = SuddenEventType.None;
            if (bgmSource != null)
            {
                StopFadeRoutine(ref bgmFadeRoutine);
                bgmSource.volume = 0f;
                bgmSource.Stop();
                currentBgmIndex = -1;
            }

            StartSourceFadeOut(suddenEventBgmSource, finalTransitionFadeOutDuration, stopOnComplete: true, ref suddenEventFadeRoutine);
            return;
        }

        StopSuddenEventBgm(revealBaseBgm: false);
        StartSourceFadeOut(bgmSource, finalTransitionFadeOutDuration, stopOnComplete: true, ref bgmFadeRoutine);
    }

    private AudioClip ResolveSuddenEventBgmClip(SuddenEventType eventType)
    {
        // Find the clip configured for a sudden event so new events can be added without code changes.
        if (eventType == SuddenEventType.None || suddenEventBgms == null)
        {
            return null;
        }

        for (int i = 0; i < suddenEventBgms.Length; i++)
        {
            SuddenEventBgmBinding binding = suddenEventBgms[i];
            if (binding != null && binding.EventType == eventType)
            {
                return binding.Clip;
            }
        }

        return null;
    }

    private bool ShouldMuteAudioForServer()
    {
        // Keep dedicated server and server-only local test processes silent.
        return muteInServerMode && IsServerRuntime();
    }

    private static bool IsServerRuntime()
    {
        // Treat batch mode, -server launch, and NGO server-only runtime as audio-disabled server contexts.
        if (Application.isBatchMode)
        {
            return true;
        }

        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "-server", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return NetworkManager.Singleton != null &&
            NetworkManager.Singleton.IsServer &&
            !NetworkManager.Singleton.IsClient;
    }

    private void ApplyServerAudioMute()
    {
        // Stop every manager-owned sound and force global volume to zero for server-only processes.
        serverAudioMuted = true;
        AudioListener.volume = 0f;
        StopBgm();
        StopSuddenEventBgm();
        SetAudioSourcesMuted(true);
        RefreshSourceVolumes();
    }

    private void SetAudioSourcesMuted(bool muted)
    {
        // Toggle mute on manager-owned sources without relying only on volume values.
        if (bgmSource != null)
        {
            bgmSource.mute = muted;
        }

        if (sfxSource != null)
        {
            sfxSource.mute = muted;
        }

        if (suddenEventBgmSource != null)
        {
            suddenEventBgmSource.mute = muted;
        }
    }
}
