using UnityEngine;

internal enum OneShotEffectScaleMode
{
    MultiplyPrefabScale,
    SetUniformScale
}

internal static class OneShotEffectPlayer
{
    public static void PlayRestartedOneShot(
        GameObject effectPrefab,
        Vector3 position,
        Quaternion rotation,
        string effectName,
        float scale,
        float lifetime,
        OneShotEffectScaleMode scaleMode,
        bool activateRoot)
    {
        // Preserve caller-captured values for effects whose scale and lifetime were already parameters.
        if (effectPrefab == null)
        {
            return;
        }

        GameObject effectObject = Object.Instantiate(effectPrefab, position, rotation);
        effectObject.name = effectName;
        ApplyScale(effectObject, scale, scaleMode);
        ActivateAndRestartParticles(effectObject, activateRoot);
        Object.Destroy(effectObject, Mathf.Max(0.1f, lifetime));
    }

    public static void PlayRestartedOneShot(
        GameObject effectPrefab,
        Vector3 position,
        Quaternion rotation,
        string effectName,
        System.Func<float> readScale,
        System.Func<float> readLifetime,
        OneShotEffectScaleMode scaleMode,
        bool activateRoot)
    {
        // Read owner settings after prefab initialization and after particle activation at their original points.
        if (effectPrefab == null)
        {
            return;
        }

        GameObject effectObject = Object.Instantiate(effectPrefab, position, rotation);
        effectObject.name = effectName;
        ApplyScale(effectObject, readScale(), scaleMode);
        ActivateAndRestartParticles(effectObject, activateRoot);
        Object.Destroy(effectObject, Mathf.Max(0.1f, readLifetime()));
    }

    private static void ApplyScale(GameObject effectObject, float scale, OneShotEffectScaleMode scaleMode)
    {
        // Preserve the existing choice between multiplying the prefab scale and replacing it uniformly.
        if (scaleMode == OneShotEffectScaleMode.MultiplyPrefabScale)
        {
            effectObject.transform.localScale *= Mathf.Max(0.01f, scale);
        }
        else
        {
            effectObject.transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);
        }
    }

    private static void ActivateAndRestartParticles(GameObject effectObject, bool activateRoot)
    {
        // Keep optional root activation and every particle child's activation, loop flag and restart order.
        if (activateRoot)
        {
            effectObject.SetActive(true);
        }

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
            main.loop = false;
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }
    }

    public static void SpawnAuthoredEffect(GameObject effectPrefab, Vector3 position, Quaternion rotation, float scale, float lifetime)
    {
        // Preserve prefab-driven playback while applying the existing cannon scale and cleanup delay.
        GameObject effectObject = Object.Instantiate(effectPrefab, position, rotation);
        effectObject.transform.localScale *= Mathf.Max(0.01f, scale);
        Object.Destroy(effectObject, lifetime);
    }

    public static void CreateFallbackCannonExplosion(Vector3 position, float gameplayRadius, float effectScale)
    {
        // Keep the existing primitive cannon flash, disabled collider, tint, and short lifetime.
        GameObject explosion = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        explosion.name = "FallbackCannonExplosion";
        explosion.transform.position = position;
        explosion.transform.localScale = Vector3.one * Mathf.Max(0.05f, gameplayRadius * 2f * effectScale);

        if (explosion.TryGetComponent(out Collider explosionCollider))
        {
            explosionCollider.enabled = false;
        }

        if (explosion.TryGetComponent(out Renderer explosionRenderer))
        {
            explosionRenderer.material.color = new Color(1f, 0.45f, 0.05f, 0.65f);
        }

        Object.Destroy(explosion, 0.35f);
    }
}
