using System.Collections;
using UnityEngine;

public class OneShotParticleEffectLifetime : MonoBehaviour
{
    [SerializeField] private bool playOnEnable = true;
    [SerializeField] private bool destroyWhenFinished = true;
    [SerializeField] private float fallbackLifetime = 2f;
    [SerializeField] private float destroyDelay = 0.1f;

    private ParticleSystem[] particleSystems;
    private Coroutine destroyRoutine;

    private void Awake()
    {
        // Cache all child particle systems so one-shot effects can be replayed or cleaned up together.
        CacheParticleSystems();
    }

    private void OnEnable()
    {
        // Start the one-shot effect automatically when the prefab instance becomes active.
        if (playOnEnable)
        {
            Play();
        }
    }

    public void Play()
    {
        // Restart every particle system from a clean state, then schedule root cleanup after the effect finishes.
        CacheParticleSystems();

        for (int i = 0; i < particleSystems.Length; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null)
            {
                continue;
            }

            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Play(withChildren: false);
        }

        if (destroyWhenFinished)
        {
            RestartDestroyRoutine();
        }
    }

    private void CacheParticleSystems()
    {
        // Refresh the particle-system list when the effect is instantiated or manually replayed.
        particleSystems = GetComponentsInChildren<ParticleSystem>(includeInactive: true);
    }

    private void RestartDestroyRoutine()
    {
        // Replace any previous cleanup timer with one based on the current particle settings.
        if (destroyRoutine != null)
        {
            StopCoroutine(destroyRoutine);
        }

        destroyRoutine = StartCoroutine(DestroyAfterLifetime());
    }

    private IEnumerator DestroyAfterLifetime()
    {
        // Wait long enough for all one-shot particles to finish before destroying the root object.
        yield return new WaitForSeconds(ResolveLifetime());
        Destroy(gameObject);
    }

    private float ResolveLifetime()
    {
        // Estimate the longest child particle lifetime from duration, start delay, and particle lifetime curves.
        float lifetime = Mathf.Max(0.1f, fallbackLifetime);
        for (int i = 0; i < particleSystems.Length; i++)
        {
            ParticleSystem particleSystem = particleSystems[i];
            if (particleSystem == null)
            {
                continue;
            }

            ParticleSystem.MainModule main = particleSystem.main;
            float systemLifetime = main.duration +
                ResolveCurveMaximum(main.startDelay) +
                ResolveCurveMaximum(main.startLifetime);
            lifetime = Mathf.Max(lifetime, systemLifetime);
        }

        return lifetime + Mathf.Max(0f, destroyDelay);
    }

    private static float ResolveCurveMaximum(ParticleSystem.MinMaxCurve curve)
    {
        // Read the largest configured constant value from a particle MinMaxCurve.
        return curve.mode == ParticleSystemCurveMode.TwoConstants
            ? Mathf.Max(curve.constantMin, curve.constantMax)
            : curve.constantMax;
    }
}
