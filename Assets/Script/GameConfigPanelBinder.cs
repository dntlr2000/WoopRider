using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GameConfigPanelBinder : MonoBehaviour
{
    [Header("Panel")]
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private bool hidePanelOnStart = true;

    [Header("Sliders")]
    [SerializeField] private Slider mouseSensitivitySlider;
    [SerializeField] private Slider masterVolumeSlider;
    [SerializeField] private bool configureSliderRanges = true;

    [Header("Value Text")]
    [SerializeField] private TMP_Text mouseSensitivityValueText;
    [SerializeField] private TMP_Text masterVolumeValueText;
    [SerializeField] private string mouseSensitivityFormat = "{0:0.00}";
    [SerializeField] private string masterVolumeFormat = "{0:0%}";

    private void Awake()
    {
        // Initialize slider ranges and panel visibility before the first button interaction.
        ConfigureSliders();
        LoadSavedValuesToControls();

        if (hidePanelOnStart)
        {
            SetPanelVisible(false);
        }
    }

    private void OnEnable()
    {
        // Subscribe UI sliders to config writes while this binder is active.
        RegisterSliderCallbacks();
        LoadSavedValuesToControls();
    }

    private void OnDisable()
    {
        // Remove slider callbacks so inactive UI does not keep stale listeners.
        UnregisterSliderCallbacks();
    }

    public void OpenPanel()
    {
        // Button hook for showing the config panel and refreshing displayed values.
        LoadSavedValuesToControls();
        SetPanelVisible(true);
    }

    public void ClosePanel()
    {
        // Button hook for hiding the config panel.
        SetPanelVisible(false);
    }

    public void TogglePanel()
    {
        // Button hook for toggling the config panel.
        bool isActive = panelRoot != null ? panelRoot.activeSelf : gameObject.activeSelf;
        if (isActive)
        {
            ClosePanel();
            return;
        }

        OpenPanel();
    }

    public void ResetToDefaults()
    {
        // Button hook for restoring saved settings and refreshing sliders.
        GameConfigStore.ResetToDefaults();
        LoadSavedValuesToControls();
    }

    public void SetMouseSensitivityFromSlider(float value)
    {
        // Slider hook for saving mouse sensitivity immediately.
        GameConfigStore.SetMouseSensitivity(value);
        RefreshMouseSensitivityText(GameConfigStore.MouseSensitivity);
    }

    public void SetMasterVolumeFromSlider(float value)
    {
        // Slider hook for saving the planned master volume immediately.
        GameConfigStore.SetMasterVolume(value);
        RefreshMasterVolumeText(GameConfigStore.MasterVolume);
    }

    private void ConfigureSliders()
    {
        // Optionally make sliders match the ranges used by the persistent config store.
        if (!configureSliderRanges)
        {
            return;
        }

        if (mouseSensitivitySlider != null)
        {
            mouseSensitivitySlider.minValue = GameConfigStore.MinMouseSensitivity;
            mouseSensitivitySlider.maxValue = GameConfigStore.MaxMouseSensitivity;
            mouseSensitivitySlider.wholeNumbers = false;
        }

        if (masterVolumeSlider != null)
        {
            masterVolumeSlider.minValue = 0f;
            masterVolumeSlider.maxValue = 1f;
            masterVolumeSlider.wholeNumbers = false;
        }
    }

    private void RegisterSliderCallbacks()
    {
        // Connect assigned sliders to saved config values.
        if (mouseSensitivitySlider != null)
        {
            mouseSensitivitySlider.onValueChanged.AddListener(SetMouseSensitivityFromSlider);
        }

        if (masterVolumeSlider != null)
        {
            masterVolumeSlider.onValueChanged.AddListener(SetMasterVolumeFromSlider);
        }
    }

    private void UnregisterSliderCallbacks()
    {
        // Disconnect slider callbacks to prevent duplicate listener registration.
        if (mouseSensitivitySlider != null)
        {
            mouseSensitivitySlider.onValueChanged.RemoveListener(SetMouseSensitivityFromSlider);
        }

        if (masterVolumeSlider != null)
        {
            masterVolumeSlider.onValueChanged.RemoveListener(SetMasterVolumeFromSlider);
        }
    }

    private void LoadSavedValuesToControls()
    {
        // Read PlayerPrefs-backed settings into sliders without triggering duplicate saves.
        float sensitivity = GameConfigStore.MouseSensitivity;
        float volume = GameConfigStore.MasterVolume;

        if (mouseSensitivitySlider != null)
        {
            mouseSensitivitySlider.SetValueWithoutNotify(sensitivity);
        }

        if (masterVolumeSlider != null)
        {
            masterVolumeSlider.SetValueWithoutNotify(volume);
        }

        RefreshMouseSensitivityText(sensitivity);
        RefreshMasterVolumeText(volume);
        GameConfigStore.ApplyRuntimeSettings();
    }

    private void RefreshMouseSensitivityText(float value)
    {
        // Update optional text next to the mouse sensitivity slider.
        if (mouseSensitivityValueText != null)
        {
            mouseSensitivityValueText.text = string.Format(mouseSensitivityFormat, value);
        }
    }

    private void RefreshMasterVolumeText(float value)
    {
        // Update optional text next to the volume slider.
        if (masterVolumeValueText != null)
        {
            masterVolumeValueText.text = string.Format(masterVolumeFormat, value);
        }
    }

    private void SetPanelVisible(bool visible)
    {
        // Toggle the assigned panel root while keeping this binder callable from menu buttons.
        if (panelRoot != null && panelRoot != gameObject)
        {
            panelRoot.SetActive(visible);
            return;
        }

        gameObject.SetActive(visible);
    }
}
