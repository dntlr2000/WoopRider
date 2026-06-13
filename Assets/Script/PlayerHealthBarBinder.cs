using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public class PlayerHealthBarBinder : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private GameObject healthRoot;
    [SerializeField] private Slider healthSlider;
    [SerializeField] private Image fillImage;
    [SerializeField] private TMP_Text healthText;

    [Header("Max Health Width")]
    [SerializeField] private RectTransform widthTarget;
    [SerializeField] private bool scaleWidthByMaxHealth = true;
    [SerializeField] private float referenceMaxHealth = 100f;
    [SerializeField] private float minWidthMultiplier = 1f;
    [SerializeField] private float maxWidthMultiplier = 2.5f;
    [SerializeField] private float widthLerpSpeed = 18f;

    [Header("Display")]
    [SerializeField] private bool hideWhenNoHealth = true;
    [SerializeField] private string healthTextFormat = "{0:0}/{1:0}";
    [Range(0f, 1f)]
    [SerializeField] private float lowHealthThreshold = 0.25f;
    [SerializeField] private Color normalHealthColor = new(0.2f, 0.9f, 0.35f);
    [SerializeField] private Color lowHealthColor = new(1f, 0.15f, 0.1f);

    private NetworkPlayerCombatState localCombatState;
    private float baseWidth;
    private bool hasBaseWidth;

    private void Awake()
    {
        // Configure the health slider as a normalized bar.
        if (healthSlider != null)
        {
            healthSlider.minValue = 0f;
            healthSlider.maxValue = 1f;
            healthSlider.wholeNumbers = false;
        }

        CacheBaseWidth();
    }

    private void Update()
    {
        // Poll replicated local health so UI stays correct across equipment swaps and damage.
        RefreshHealthBar();
    }

    public void RefreshHealthBar()
    {
        // Read the local player's combat state and render a normalized health bar.
        if (!TryGetLocalCombatState(out NetworkPlayerCombatState combatState) ||
            !combatState.HasHealth ||
            combatState.MaxHealth <= 0f)
        {
            SetVisible(!hideWhenNoHealth);
            SetHealthValues(0f, 0f, 0f);
            UpdateWidthForMaxHealth(referenceMaxHealth);
            return;
        }

        float maxHealth = Mathf.Max(1f, combatState.MaxHealth);
        float currentHealth = Mathf.Clamp(combatState.CurrentHealth, 0f, maxHealth);
        float healthPercent = Mathf.Clamp01(currentHealth / maxHealth);
        SetVisible(true);
        SetHealthValues(currentHealth, maxHealth, healthPercent);
        UpdateWidthForMaxHealth(maxHealth);
    }

    private bool TryGetLocalCombatState(out NetworkPlayerCombatState combatState)
    {
        // Resolve the NetworkPlayerCombatState that belongs to the local player object.
        if (localCombatState != null && localCombatState.IsSpawned && localCombatState.IsOwner)
        {
            combatState = localCombatState;
            return true;
        }

        localCombatState = null;
        NetworkManager manager = NetworkManager.Singleton;
        NetworkObject localPlayerObject = manager != null && manager.IsListening
            ? manager.SpawnManager?.GetLocalPlayerObject()
            : null;
        if (localPlayerObject != null && localPlayerObject.TryGetComponent(out localCombatState))
        {
            combatState = localCombatState;
            return true;
        }

        NetworkPlayerCombatState[] states = FindObjectsByType<NetworkPlayerCombatState>(FindObjectsSortMode.None);
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i] != null && states[i].IsOwner)
            {
                localCombatState = states[i];
                combatState = localCombatState;
                return true;
            }
        }

        combatState = null;
        return false;
    }

    private void SetHealthValues(float currentHealth, float maxHealth, float healthPercent)
    {
        // Push calculated health values into the optional slider, fill image, and text.
        if (healthSlider != null)
        {
            healthSlider.SetValueWithoutNotify(healthPercent);
        }

        if (fillImage != null)
        {
            fillImage.color = healthPercent <= lowHealthThreshold ? lowHealthColor : normalHealthColor;
        }

        if (healthText != null)
        {
            healthText.text = maxHealth > 0f ? string.Format(healthTextFormat, currentHealth, maxHealth) : string.Empty;
        }
    }

    private void CacheBaseWidth()
    {
        // Remember the authored UI width so max-health scaling has a stable baseline.
        RectTransform target = ResolveWidthTarget();
        if (target == null)
        {
            return;
        }

        float authoredWidth = Mathf.Max(target.rect.width, Mathf.Abs(target.sizeDelta.x));
        baseWidth = Mathf.Max(1f, authoredWidth);
        hasBaseWidth = true;
    }

    private RectTransform ResolveWidthTarget()
    {
        // Use the explicitly assigned width target, then the slider RectTransform as fallback.
        if (widthTarget != null)
        {
            return widthTarget;
        }

        return healthSlider != null ? healthSlider.GetComponent<RectTransform>() : null;
    }

    private void UpdateWidthForMaxHealth(float maxHealth)
    {
        // Scale the bar width from the reference max health so stat/equipment max-health gains are visible.
        if (!scaleWidthByMaxHealth)
        {
            return;
        }

        RectTransform target = ResolveWidthTarget();
        if (target == null)
        {
            return;
        }

        if (!hasBaseWidth)
        {
            CacheBaseWidth();
        }

        float multiplier = Mathf.Clamp(maxHealth / Mathf.Max(1f, referenceMaxHealth), minWidthMultiplier, maxWidthMultiplier);
        float targetWidth = baseWidth * multiplier;
        Vector2 sizeDelta = target.sizeDelta;
        float t = 1f - Mathf.Exp(-Mathf.Max(0f, widthLerpSpeed) * Time.deltaTime);
        sizeDelta.x = Mathf.Lerp(sizeDelta.x, targetWidth, t);
        target.sizeDelta = sizeDelta;
    }

    private void SetVisible(bool visible)
    {
        // Toggle the assigned health root without disabling this binder when possible.
        if (healthRoot != null && healthRoot != gameObject)
        {
            healthRoot.SetActive(visible);
            return;
        }

        SetComponentVisible(healthSlider, visible);
        SetComponentVisible(fillImage, visible);
        SetComponentVisible(healthText, visible);
    }

    private static void SetComponentVisible(Behaviour component, bool visible)
    {
        // Enable or disable an individual UI component when no separate root is assigned.
        if (component != null)
        {
            component.enabled = visible;
        }
    }
}
