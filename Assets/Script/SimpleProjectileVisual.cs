using System.Collections.Generic;
using UnityEngine;

public class SimpleProjectileVisual : MonoBehaviour
{
    private const string DefaultProjectileVisualResourcePath = "fbx/Equip_Bullets/BasicBullet";
    private const float PrefabMaxSizeRadiusMultiplier = 4f;
    private static readonly Color ProjectileColor = new(1f, 0.85f, 0.15f, 1f);
    private static readonly Dictionary<ulong, SimpleProjectileVisual> ActiveNetworkVisuals = new();

    private Vector3 direction;
    private Vector3 velocity;
    private float speed;
    private float maxDistance;
    private float traveledDistance;
    private float expireTime;
    private float gravity;
    private float radius;
    private float spawnTime;
    private bool useBallisticMotion;
    private ulong networkVisualId;

    public static void Spawn(
        Vector3 origin,
        Vector3 targetPoint,
        float projectileSpeed,
        float projectileRadius,
        float lifeTime,
        GameObject visualPrefab = null,
        string visualResourcePath = null,
        ulong networkId = 0)
    {
        // Create a projectile wrapper and attach the configured equipment visual to it.
        Vector3 toTarget = targetPoint - origin;
        float distance = Mathf.Max(toTarget.magnitude, 0.1f);
        Vector3 travelDirection = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.forward;

        GameObject projectile = new("ProjectileVisual");
        projectile.name = "ProjectileVisual";
        projectile.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(travelDirection, Vector3.up));
        CreateProjectileVisual(projectile.transform, projectileRadius, visualPrefab, visualResourcePath);

        SimpleProjectileVisual visual = projectile.AddComponent<SimpleProjectileVisual>();
        visual.Initialize(travelDirection, projectileSpeed, distance, lifeTime);
        visual.RegisterNetworkVisual(networkId);
    }

    public static void SpawnBallistic(
        Vector3 origin,
        Vector3 targetPoint,
        float projectileSpeed,
        float projectileRadius,
        float lifeTime,
        float projectileGravity,
        GameObject visualPrefab = null,
        string visualResourcePath = null,
        ulong networkId = 0)
    {
        // Create a gravity-driven projectile visual for cannon-style attacks.
        Vector3 toTarget = targetPoint - origin;
        Vector3 travelDirection = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.forward;

        GameObject projectile = new("CannonProjectileVisual");
        projectile.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(travelDirection, Vector3.up));
        CreateProjectileVisual(projectile.transform, projectileRadius, visualPrefab, visualResourcePath);

        SimpleProjectileVisual visual = projectile.AddComponent<SimpleProjectileVisual>();
        visual.InitializeBallistic(travelDirection * Mathf.Max(0.01f, projectileSpeed), projectileRadius, lifeTime, projectileGravity);
        visual.RegisterNetworkVisual(networkId);
    }

    public static void StopNetworkVisual(ulong networkId, Vector3 impactPoint)
    {
        // Remove the matching client-side projectile as soon as the server confirms its impact.
        if (networkId == 0 || !ActiveNetworkVisuals.TryGetValue(networkId, out SimpleProjectileVisual visual) || visual == null)
        {
            return;
        }

        ActiveNetworkVisuals.Remove(networkId);
        visual.networkVisualId = 0;
        visual.transform.position = impactPoint;
        visual.gameObject.SetActive(false);
        Destroy(visual.gameObject);
    }

    private static void CreateProjectileVisual(Transform parent, float projectileRadius, GameObject visualPrefab, string visualResourcePath)
    {
        // Instantiate the requested model, falling back to the default Resources bullet or a primitive sphere.
        GameObject resolvedPrefab = visualPrefab != null ? visualPrefab : LoadProjectileVisualPrefab(visualResourcePath);
        if (resolvedPrefab != null)
        {
            InstantiatePrefabVisual(parent, resolvedPrefab, projectileRadius);
            return;
        }

        CreateFallbackSphereVisual(parent, projectileRadius);
    }

    private static GameObject LoadProjectileVisualPrefab(string visualResourcePath)
    {
        // Resolve an optional Resources path, using the current basic bullet model when none is configured.
        string resourcePath = string.IsNullOrWhiteSpace(visualResourcePath)
            ? DefaultProjectileVisualResourcePath
            : visualResourcePath.Trim();

        GameObject prefab = Resources.Load<GameObject>(resourcePath);
        if (prefab != null || resourcePath == DefaultProjectileVisualResourcePath)
        {
            return prefab;
        }

        return Resources.Load<GameObject>(DefaultProjectileVisualResourcePath);
    }

    private static void InstantiatePrefabVisual(Transform parent, GameObject prefab, float projectileRadius)
    {
        // Place a model under the projectile wrapper and normalize its visible size for current projectile radius.
        GameObject visual = Instantiate(prefab, parent);
        visual.name = $"{prefab.name}_Visual";
        visual.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        visual.transform.localScale = Vector3.one;

        DisableVisualColliders(visual);
        ScaleVisualToProjectileRadius(visual, projectileRadius);
    }

    private static void CreateFallbackSphereVisual(Transform parent, float projectileRadius)
    {
        // Keep the old primitive sphere as a safety fallback when no projectile model can be loaded.
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "FallbackProjectileSphere";
        sphere.transform.SetParent(parent, false);
        sphere.transform.localScale = Vector3.one * Mathf.Max(0.01f, projectileRadius * 2f);

        if (sphere.TryGetComponent(out Collider projectileCollider))
        {
            projectileCollider.enabled = false;
        }

        if (sphere.TryGetComponent(out Renderer projectileRenderer))
        {
            projectileRenderer.material.color = ProjectileColor;
        }
    }

    private static void DisableVisualColliders(GameObject visual)
    {
        // Prevent decorative projectile models from participating in physics or blocking aim rays.
        Collider[] visualColliders = visual.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < visualColliders.Length; i++)
        {
            if (visualColliders[i] != null)
            {
                visualColliders[i].enabled = false;
            }
        }
    }

    private static void ScaleVisualToProjectileRadius(GameObject visual, float projectileRadius)
    {
        // Normalize imported FBX scale so different projectile models appear at gameplay-sized proportions.
        if (!TryCalculateRendererBounds(visual, out Bounds bounds))
        {
            visual.transform.localScale = Vector3.one * Mathf.Max(0.01f, projectileRadius * 2f);
            return;
        }

        float maxSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        if (maxSize <= 0.0001f)
        {
            return;
        }

        float targetMaxSize = Mathf.Max(0.02f, projectileRadius * PrefabMaxSizeRadiusMultiplier);
        visual.transform.localScale *= targetMaxSize / maxSize;

        if (TryCalculateRendererBounds(visual, out Bounds scaledBounds) && visual.transform.parent != null)
        {
            Vector3 localCenter = visual.transform.parent.InverseTransformPoint(scaledBounds.center);
            visual.transform.localPosition -= localCenter;
        }
    }

    private static bool TryCalculateRendererBounds(GameObject visual, out Bounds bounds)
    {
        // Combine child renderer bounds so imported models can be centered and scaled as one projectile.
        Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
        bounds = default;
        bool hasBounds = false;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer targetRenderer = renderers[i];
            if (targetRenderer == null)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = targetRenderer.bounds;
                hasBounds = true;
                continue;
            }

            bounds.Encapsulate(targetRenderer.bounds);
        }

        return hasBounds;
    }

    private void Initialize(Vector3 travelDirection, float projectileSpeed, float distance, float lifeTime)
    {
        // Store movement values and a lifetime fallback for the temporary projectile.
        direction = travelDirection;
        speed = Mathf.Max(0.01f, projectileSpeed);
        maxDistance = Mathf.Max(0.1f, distance);
        expireTime = Time.time + Mathf.Max(0.1f, lifeTime);
        spawnTime = Time.time;
    }

    private void InitializeBallistic(Vector3 initialVelocity, float projectileRadius, float lifeTime, float projectileGravity)
    {
        // Store ballistic movement values for a temporary cannon projectile visual.
        velocity = initialVelocity;
        radius = Mathf.Max(0.01f, projectileRadius);
        gravity = Mathf.Max(0f, projectileGravity);
        expireTime = Time.time + Mathf.Max(0.1f, lifeTime);
        spawnTime = Time.time;
        useBallisticMotion = true;
    }

    private void RegisterNetworkVisual(ulong networkId)
    {
        // Track server-approved projectile visuals so an impact RPC can remove the exact instance.
        if (networkId == 0)
        {
            return;
        }

        if (ActiveNetworkVisuals.TryGetValue(networkId, out SimpleProjectileVisual existingVisual) && existingVisual != null)
        {
            Destroy(existingVisual.gameObject);
        }

        networkVisualId = networkId;
        ActiveNetworkVisuals[networkId] = this;
    }

    private void OnDestroy()
    {
        // Remove expired or locally collided visuals from the network lookup table.
        if (networkVisualId != 0 &&
            ActiveNetworkVisuals.TryGetValue(networkVisualId, out SimpleProjectileVisual registeredVisual) &&
            registeredVisual == this)
        {
            ActiveNetworkVisuals.Remove(networkVisualId);
        }
    }

    private void Update()
    {
        if (useBallisticMotion)
        {
            UpdateBallisticMotion();
            return;
        }

        // Move forward until the projectile reaches its aimed point or expires.
        float step = speed * Time.deltaTime;
        transform.position += direction * step;
        traveledDistance += step;

        if (traveledDistance >= maxDistance || Time.time >= expireTime)
        {
            Destroy(gameObject);
        }
    }

    private void UpdateBallisticMotion()
    {
        // Move the cannon projectile with gravity and remove it when it locally touches something or expires.
        Vector3 previousPosition = transform.position;
        velocity += Vector3.down * gravity * Time.deltaTime;
        Vector3 nextPosition = previousPosition + velocity * Time.deltaTime;
        Vector3 movement = nextPosition - previousPosition;

        if (Time.time - spawnTime > 0.08f &&
            movement.sqrMagnitude > 0.000001f &&
            Physics.SphereCast(previousPosition, radius, movement.normalized, out RaycastHit hit, movement.magnitude, ~0, QueryTriggerInteraction.Ignore))
        {
            transform.position = hit.point;
            Destroy(gameObject);
            return;
        }

        transform.position = nextPosition;
        if (velocity.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
        }

        if (Time.time >= expireTime)
        {
            Destroy(gameObject);
        }
    }
}

public static class SimpleHitscanVisual
{
    private static readonly Color HitscanColor = new(0.25f, 0.95f, 1f, 0.85f);

    public static void Spawn(Vector3 origin, Vector3 targetPoint, float width = 0.035f, float lifetime = 0.08f)
    {
        // Draw a short-lived line renderer so hitscan weapons still have readable fire feedback.
        GameObject tracerObject = new("HitscanTracerVisual");
        LineRenderer lineRenderer = tracerObject.AddComponent<LineRenderer>();
        lineRenderer.positionCount = 2;
        lineRenderer.useWorldSpace = true;
        lineRenderer.SetPosition(0, origin);
        lineRenderer.SetPosition(1, targetPoint);
        lineRenderer.startWidth = Mathf.Max(0.001f, width);
        lineRenderer.endWidth = Mathf.Max(0.001f, width * 0.35f);
        lineRenderer.startColor = HitscanColor;
        lineRenderer.endColor = new Color(HitscanColor.r, HitscanColor.g, HitscanColor.b, 0f);

        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            lineRenderer.material = new Material(shader);
        }

        Object.Destroy(tracerObject, Mathf.Max(0.01f, lifetime));
    }
}
