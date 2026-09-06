using UnityEngine;

internal static class ServerAttackTargetQuery
{
    internal static bool TryFindWorldProjectileBlock(Vector3 origin, Vector3 targetPoint, float radius, Transform ownerTransform, out Vector3 hitPoint, out float hitDistance)
    {
        // Find the nearest world blocker using the original sphere-cast and self-filter rules.
        hitPoint = default;
        hitDistance = float.MaxValue;
        Vector3 direction = targetPoint - origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        RaycastHit[] hits = Physics.SphereCastAll(
            origin,
            radius,
            direction.normalized,
            distance,
            ~0,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || ShouldIgnoreWorldProjectileBlock(hit.collider, ownerTransform))
            {
                continue;
            }

            if (hit.distance < hitDistance)
            {
                hitDistance = hit.distance;
                hitPoint = hit.point;
            }
        }

        return hitDistance < float.MaxValue;
    }

    internal static bool ShouldIgnoreWorldProjectileBlock(Collider targetCollider, Transform ownerTransform)
    {
        // Exclude the owning hierarchy and player bodies from the separate world-blocker pass.
        if (targetCollider == null)
        {
            return true;
        }

        Transform targetTransform = targetCollider.transform;
        if (targetTransform == ownerTransform || targetTransform.IsChildOf(ownerTransform))
        {
            return true;
        }

        return targetCollider.GetComponentInParent<NetworkPlayerCombatState>() != null;
    }

    internal static bool TryFindProjectileTarget(
        Vector3 origin,
        Vector3 targetPoint,
        float radius,
        ulong ownerClientId,
        float fallbackTargetRadius,
        float fallbackTargetHeight,
        out NetworkPlayerCombatState targetCombatState,
        out Vector3 hitPoint,
        out float hitDistance)
    {
        // Resolve the nearest player before blockers and retain the unconditional position fallback.
        targetCombatState = null;
        hitPoint = default;
        hitDistance = float.MaxValue;
        Vector3 direction = targetPoint - origin;
        float distance = direction.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        RaycastHit[] hits = Physics.SphereCastAll(
            origin,
            radius,
            direction.normalized,
            distance,
            ~0,
            QueryTriggerInteraction.Ignore);

        float nearestTargetDistance = float.MaxValue;
        float nearestBlockDistance = float.MaxValue;
        Vector3 nearestTargetPoint = default;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null)
            {
                continue;
            }

            NetworkPlayerCombatState hitCombatState = hit.collider.GetComponentInParent<NetworkPlayerCombatState>();
            if (hitCombatState != null)
            {
                if (hitCombatState.OwnerClientId == ownerClientId)
                {
                    continue;
                }

                if (hit.distance < nearestTargetDistance)
                {
                    nearestTargetDistance = hit.distance;
                    nearestTargetPoint = hit.point;
                    targetCombatState = hitCombatState;
                }

                continue;
            }

            if (hit.distance < nearestBlockDistance)
            {
                nearestBlockDistance = hit.distance;
            }
        }

        if (TryFindFallbackTransformTarget(
            origin, radius, direction.normalized, distance, nearestBlockDistance,
            ownerClientId, fallbackTargetRadius, fallbackTargetHeight,
            ref targetCombatState, ref nearestTargetDistance, ref nearestTargetPoint))
        {
            hitPoint = nearestTargetPoint;
            hitDistance = nearestTargetDistance;
            return true;
        }

        if (targetCombatState != null && nearestTargetDistance <= nearestBlockDistance)
        {
            hitPoint = nearestTargetPoint;
            hitDistance = nearestTargetDistance;
            return true;
        }

        return false;
    }

    internal static bool TryFindFallbackTransformTarget(
        Vector3 origin,
        float projectileRadius,
        Vector3 direction,
        float distance,
        float nearestBlockDistance,
        ulong ownerClientId,
        float fallbackTargetRadius,
        float fallbackTargetHeight,
        ref NetworkPlayerCombatState targetCombatState,
        ref float nearestTargetDistance,
        ref Vector3 nearestTargetPoint)
    {
        // Preserve the scene scan and capsule approximation for hidden or absent player colliders.
        NetworkPlayerCombatState[] combatStates = Object.FindObjectsByType<NetworkPlayerCombatState>(FindObjectsSortMode.None);
        float radius = Mathf.Max(projectileRadius, fallbackTargetRadius);
        for (int i = 0; i < combatStates.Length; i++)
        {
            NetworkPlayerCombatState candidate = combatStates[i];
            if (candidate == null || !candidate.IsSpawned || candidate.OwnerClientId == ownerClientId)
            {
                continue;
            }

            if (!TryIntersectTargetCapsule(origin, direction, distance, candidate.transform.position, radius, fallbackTargetHeight, out float candidateDistance, out Vector3 candidatePoint))
            {
                continue;
            }

            if (candidateDistance < nearestTargetDistance && candidateDistance <= nearestBlockDistance)
            {
                nearestTargetDistance = candidateDistance;
                nearestTargetPoint = candidatePoint;
                targetCombatState = candidate;
            }
        }

        return targetCombatState != null && nearestTargetDistance <= nearestBlockDistance;
    }

    internal static bool TryIntersectTargetCapsule(Vector3 origin, Vector3 direction, float distance, Vector3 targetPosition, float radius, float fallbackTargetHeight, out float hitDistance, out Vector3 hitPoint)
    {
        // Keep the existing lower-and-upper point approximation for a player's vertical body.
        hitDistance = 0f;
        hitPoint = default;

        Vector3 bottom = targetPosition;
        Vector3 top = targetPosition + Vector3.up * Mathf.Max(0f, fallbackTargetHeight);
        float bottomDistance = DistanceFromRaySegment(origin, direction, bottom, distance, out float bottomAlongRay);
        float topDistance = DistanceFromRaySegment(origin, direction, top, distance, out float topAlongRay);

        if (bottomDistance > radius && topDistance > radius)
        {
            return false;
        }

        hitDistance = bottomDistance <= topDistance ? bottomAlongRay : topAlongRay;
        hitPoint = origin + direction * hitDistance;
        return true;
    }

    internal static float DistanceFromRaySegment(Vector3 origin, Vector3 direction, Vector3 point, float maxDistance, out float alongRay)
    {
        // Measure the point-to-segment distance without changing the existing projection clamp.
        alongRay = Mathf.Clamp(Vector3.Dot(point - origin, direction), 0f, maxDistance);
        Vector3 closestPoint = origin + direction * alongRay;
        return Vector3.Distance(point, closestPoint);
    }
}
