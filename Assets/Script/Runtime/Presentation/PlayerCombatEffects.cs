using UnityEngine;

internal sealed class PlayerCombatEffects
{
    private ParticleSystem lowHealthSparkEffect;
    private GameObject attackUpEffectObject;
    private GameObject defenceUpEffectObject;
    private GameObject speedUpEffectObject;

    public ParticleSystem LowHealthSparkEffect => lowHealthSparkEffect;
    public GameObject AttackUpEffectObject => attackUpEffectObject;
    public GameObject DefenceUpEffectObject => defenceUpEffectObject;
    public GameObject SpeedUpEffectObject => speedUpEffectObject;

    public void SetLowHealthSparkPlaying(bool shouldPlay)
    {
        // Start or clear the existing warning particles without replacing their instance.
        if (shouldPlay)
        {
            if (lowHealthSparkEffect != null && !lowHealthSparkEffect.isPlaying)
            {
                lowHealthSparkEffect.Play();
            }

            return;
        }

        if (lowHealthSparkEffect != null && lowHealthSparkEffect.isPlaying)
        {
            lowHealthSparkEffect.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    public void TrackAttackBuffEffect(GameObject effectObject)
    {
        // Retain the attack effect returned by the owner's existing create-or-retain decision.
        attackUpEffectObject = effectObject;
    }

    public void TrackDefenceBuffEffect(GameObject effectObject)
    {
        // Retain the defense effect returned by the owner's existing create-or-retain decision.
        defenceUpEffectObject = effectObject;
    }

    public void TrackSpeedBuffEffect(GameObject effectObject)
    {
        // Retain the speed effect returned by the owner's existing create-or-retain decision.
        speedUpEffectObject = effectObject;
    }

    public void DestroyAttackBuffEffect()
    {
        // Remove the tracked attack effect when its original visibility predicate becomes false.
        DestroyPersistentEffect(ref attackUpEffectObject);
    }

    public void DestroyDefenceBuffEffect()
    {
        // Remove the tracked defense effect when its original visibility predicate becomes false.
        DestroyPersistentEffect(ref defenceUpEffectObject);
    }

    public void DestroySpeedBuffEffect()
    {
        // Remove the tracked speed effect when its original visibility predicate becomes false.
        DestroyPersistentEffect(ref speedUpEffectObject);
    }

    public static GameObject CreatePersistentBuffEffect(
        GameObject effectPrefab,
        Transform effectParent,
        Transform playerRoot,
        System.Func<Vector3> readLocalOffset,
        System.Func<Vector3> readLocalEulerAngles,
        System.Func<float> readScale,
        string effectName)
    {
        // Instantiate first, then read live transform settings before the original looping-particle restart.
        GameObject createdEffect = Object.Instantiate(effectPrefab, effectParent);
        createdEffect.name = effectName;
        ApplyAnchoredEffectTransform(createdEffect.transform, effectParent, playerRoot, readLocalOffset, readLocalEulerAngles, readScale);
        ConfigurePersistentLoopingEffect(createdEffect);
        return createdEffect;
    }

    public static void ApplyAnchoredEffectTransform(
        Transform effectTransform,
        Transform effectParent,
        Transform playerRoot,
        System.Func<Vector3> readLocalOffset,
        System.Func<Vector3> readLocalEulerAngles,
        System.Func<float> readScale)
    {
        // Read each setting only at its original transform assignment, including the root-only offset branch.
        if (effectTransform == null)
        {
            return;
        }

        effectTransform.localPosition = effectParent == playerRoot ? readLocalOffset() : Vector3.zero;
        effectTransform.localRotation = Quaternion.Euler(readLocalEulerAngles());
        effectTransform.localScale = Vector3.one * Mathf.Max(0.01f, readScale());
    }

    public static void ConfigurePersistentLoopingEffect(GameObject effectObject)
    {
        // Activate and restart particle children as looping effects in the existing initialization order.
        if (effectObject == null)
        {
            return;
        }

        effectObject.SetActive(true);
        ParticleSystem[] particleSystems = effectObject.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
        for (int i = 0; i < particleSystems.Length; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null)
            {
                continue;
            }

            particleSystem.gameObject.SetActive(true);
            ParticleSystem.MainModule main = particleSystem.main;
            main.loop = true;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }
    }

    public void CreateLowHealthSparkEffect(
        ParticleSystem sparkPrefab,
        Transform sparkParent,
        Transform playerRoot,
        System.Func<Vector3> readLocalOffset,
        System.Func<Vector3> readLocalEulerAngles,
        System.Func<float> readScale,
        System.Func<float> readRate)
    {
        // Create the original prefab or procedural warning before reading its live transform and emission settings.
        if (sparkPrefab != null)
        {
            lowHealthSparkEffect = Object.Instantiate(sparkPrefab, sparkParent);
            lowHealthSparkEffect.name = "LowHealthRedSparkEffect";
            ApplyAnchoredEffectTransform(lowHealthSparkEffect.transform, sparkParent, playerRoot, readLocalOffset, readLocalEulerAngles, readScale);
            return;
        }

        GameObject sparkObject = new("LowHealthRedSparkEffect");
        sparkObject.transform.SetParent(sparkParent, false);
        ApplyAnchoredEffectTransform(sparkObject.transform, sparkParent, playerRoot, readLocalOffset, readLocalEulerAngles, readScale);

        lowHealthSparkEffect = sparkObject.AddComponent<ParticleSystem>();
        ConfigureLowHealthSparkEffect(lowHealthSparkEffect, readRate);
    }

    public static void ConfigureLowHealthSparkEffect(ParticleSystem sparkEffect, System.Func<float> readRate)
    {
        // Preserve procedural particle setup and read the current warning rate at the original emission assignment.
        ParticleSystem.MainModule main = sparkEffect.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.55f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 3f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.08f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.02f, 0f, 1f), new Color(1f, 0.35f, 0.1f, 1f));

        ParticleSystem.EmissionModule emission = sparkEffect.emission;
        emission.rateOverTime = Mathf.Max(0f, readRate());

        ParticleSystem.ShapeModule shape = sparkEffect.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.55f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = sparkEffect.colorOverLifetime;
        colorOverLifetime.enabled = true;
        Gradient fadeGradient = new();
        fadeGradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.02f, 0f), 0f),
                new GradientColorKey(new Color(1f, 0.35f, 0.1f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0f, 1f)
            });
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(fadeGradient);

        ParticleSystemRenderer renderer = sparkEffect.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        Shader particleShader = Shader.Find("Sprites/Default");
        if (particleShader != null)
        {
            renderer.material = new Material(particleShader);
        }
    }

    public void DestroyLowHealthSparkEffect()
    {
        // Destroy the warning instance only when the existing owner cleanup path requests it.
        if (lowHealthSparkEffect == null)
        {
            return;
        }

        Object.Destroy(lowHealthSparkEffect.gameObject);
        lowHealthSparkEffect = null;
    }

    public void DestroyBuffEffects()
    {
        // Clear the three tracked buff instances in the original despawn cleanup order.
        DestroyPersistentEffect(ref attackUpEffectObject);
        DestroyPersistentEffect(ref defenceUpEffectObject);
        DestroyPersistentEffect(ref speedUpEffectObject);
    }

    public static void DestroyPersistentEffect(ref GameObject effectObject)
    {
        // Preserve delayed Unity destruction and clear the owner's tracked effect reference immediately.
        if (effectObject == null)
        {
            return;
        }

        Object.Destroy(effectObject);
        effectObject = null;
    }
}
