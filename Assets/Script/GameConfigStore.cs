using System;
using UnityEngine;

public static class GameConfigStore
{
    public const float DefaultMouseSensitivity = 1f;
    public const float DefaultMasterVolume = 1f;
    public const float MinMouseSensitivity = 0.1f;
    public const float MaxMouseSensitivity = 5f;

    private const string MouseSensitivityKey = "WoopRider.Config.MouseSensitivity";
    private const string MasterVolumeKey = "WoopRider.Config.MasterVolume";

    public static event Action<float> MouseSensitivityChanged;
    public static event Action<float> MasterVolumeChanged;

    public static float MouseSensitivity => ClampMouseSensitivity(PlayerPrefs.GetFloat(MouseSensitivityKey, DefaultMouseSensitivity));
    public static float MasterVolume => Mathf.Clamp01(PlayerPrefs.GetFloat(MasterVolumeKey, DefaultMasterVolume));

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplySavedSettingsAfterSceneLoad()
    {
        // Reapply saved settings whenever the game loads a scene.
        ApplyRuntimeSettings();
    }

    public static void SetMouseSensitivity(float value)
    {
        // Persist mouse sensitivity and notify active camera bindings.
        float clampedValue = ClampMouseSensitivity(value);
        PlayerPrefs.SetFloat(MouseSensitivityKey, clampedValue);
        PlayerPrefs.Save();
        MouseSensitivityChanged?.Invoke(clampedValue);
    }

    public static void SetMasterVolume(float value)
    {
        // Persist the planned master volume setting and apply it to Unity's global listener volume.
        float clampedValue = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(MasterVolumeKey, clampedValue);
        PlayerPrefs.Save();
        ApplyMasterVolume(clampedValue);
        MasterVolumeChanged?.Invoke(clampedValue);
    }

    public static void ResetToDefaults()
    {
        // Restore every stored config value to the current design defaults.
        SetMouseSensitivity(DefaultMouseSensitivity);
        SetMasterVolume(DefaultMasterVolume);
    }

    public static void ApplyRuntimeSettings()
    {
        // Push saved settings into systems that need a runtime value.
        float sensitivity = MouseSensitivity;
        float volume = MasterVolume;
        ApplyMasterVolume(volume);
        MouseSensitivityChanged?.Invoke(sensitivity);
        MasterVolumeChanged?.Invoke(volume);
    }

    private static float ClampMouseSensitivity(float value)
    {
        // Keep sensitivity inside the range exposed to the config UI.
        return Mathf.Clamp(value, MinMouseSensitivity, MaxMouseSensitivity);
    }

    private static void ApplyMasterVolume(float value)
    {
        // Use AudioListener volume as a lightweight placeholder until a full audio mixer exists.
        AudioListener.volume = Mathf.Clamp01(value);
    }
}
