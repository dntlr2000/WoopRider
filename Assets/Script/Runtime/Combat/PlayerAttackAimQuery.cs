using UnityEngine;

internal static class PlayerAttackAimQuery
{
    internal static Vector3 ResolveAimPoint(Ray aimRay, float range, LayerMask aimMask, Transform playerTransform, ThirdPersonController controller)
    {
        // Select the nearest non-self ray hit using the original all-hit query and tie comparison.
        RaycastHit[] hits = Physics.RaycastAll(aimRay, range, aimMask, QueryTriggerInteraction.Ignore);
        float nearestDistance = float.MaxValue;
        Vector3 aimPoint = aimRay.GetPoint(range);

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (ShouldIgnoreAimHit(hit, playerTransform, controller))
            {
                continue;
            }

            if (hit.distance < nearestDistance)
            {
                nearestDistance = hit.distance;
                aimPoint = hit.point;
            }
        }

        return aimPoint;
    }

    internal static bool ShouldIgnoreAimHit(RaycastHit hit, Transform playerTransform, ThirdPersonController controller)
    {
        // Ignore missing colliders, the player's hierarchy, and the same movement controller.
        if (hit.collider == null)
        {
            return true;
        }

        Transform hitTransform = hit.collider.transform;
        if (hitTransform == playerTransform || hitTransform.IsChildOf(playerTransform))
        {
            return true;
        }

        ThirdPersonController hitController = hit.collider.GetComponentInParent<ThirdPersonController>();
        return hitController != null && hitController == controller;
    }

    internal static Vector3 ResolveMuzzlePosition(Vector3 bodyMuzzleBasePosition, Transform playerTransform, float muzzleRightOffset, float muzzleForwardOffset)
    {
        // Offset the already-resolved body anchor along the player's right and forward directions.
        return bodyMuzzleBasePosition +
            playerTransform.right * muzzleRightOffset +
            playerTransform.forward * muzzleForwardOffset;
    }

    internal static Vector3 ResolveBodyMuzzleBasePosition(Transform playerTransform, CharacterController characterController, float muzzleHeight)
    {
        // Anchor muzzle height to the player body and preserve the existing controller-height clamp.
        if (characterController == null)
        {
            return playerTransform.position + Vector3.up * muzzleHeight;
        }

        float clampedHeight = Mathf.Clamp(muzzleHeight, 0f, Mathf.Max(0.1f, characterController.height));
        return playerTransform.position + Vector3.up * clampedHeight;
    }
}
