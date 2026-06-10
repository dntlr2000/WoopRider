using UnityEngine;

public class SimpleProjectileVisual : MonoBehaviour
{
    private static readonly Color ProjectileColor = new(1f, 0.85f, 0.15f, 1f);

    private Vector3 direction;
    private float speed;
    private float maxDistance;
    private float traveledDistance;
    private float expireTime;

    public static void Spawn(Vector3 origin, Vector3 targetPoint, float projectileSpeed, float projectileRadius, float lifeTime)
    {
        // Create a temporary primitive projectile until a real prefab is ready.
        Vector3 toTarget = targetPoint - origin;
        float distance = Mathf.Max(toTarget.magnitude, 0.1f);
        Vector3 travelDirection = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.forward;

        GameObject projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        projectile.name = "ProjectileVisual";
        projectile.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(travelDirection, Vector3.up));
        projectile.transform.localScale = Vector3.one * Mathf.Max(0.01f, projectileRadius * 2f);

        if (projectile.TryGetComponent(out Collider projectileCollider))
        {
            projectileCollider.enabled = false;
        }

        if (projectile.TryGetComponent(out Renderer projectileRenderer))
        {
            projectileRenderer.material.color = ProjectileColor;
        }

        SimpleProjectileVisual visual = projectile.AddComponent<SimpleProjectileVisual>();
        visual.Initialize(travelDirection, projectileSpeed, distance, lifeTime);
    }

    private void Initialize(Vector3 travelDirection, float projectileSpeed, float distance, float lifeTime)
    {
        // Store movement values and a lifetime fallback for the temporary projectile.
        direction = travelDirection;
        speed = Mathf.Max(0.01f, projectileSpeed);
        maxDistance = Mathf.Max(0.1f, distance);
        expireTime = Time.time + Mathf.Max(0.1f, lifeTime);
    }

    private void Update()
    {
        // Move forward until the projectile reaches its aimed point or expires.
        float step = speed * Time.deltaTime;
        transform.position += direction * step;
        traveledDistance += step;

        if (traveledDistance >= maxDistance || Time.time >= expireTime)
        {
            Destroy(gameObject);
        }
    }
}
