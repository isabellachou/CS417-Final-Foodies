using UnityEngine;
using Unity.XR.CoreUtils;

public partial class RunawayObject
{
    private float GetHorizontalPlayerDistance(Vector3 origin)
    {
        Transform playerReference = ResolvePlayerPositionReference();
        if (playerReference == null)
            return float.PositiveInfinity;

        Vector3 playerOffset = playerReference.position - origin;
        playerOffset.y = 0f;
        return playerOffset.magnitude;
    }

    private Vector3 GetPlayerPosition()
    {
        Transform playerReference = ResolvePlayerPositionReference();
        return playerReference != null ? playerReference.position : originalPosition;
    }

    private Transform ResolvePlayerPositionReference()
    {
        if (playerPositionReference != null)
            return playerPositionReference;

        XROrigin xrOrigin = FindFirstObjectByType<XROrigin>();
        if (xrOrigin != null)
        {
            if (preferPlayerCameraForDistance && xrOrigin.Camera != null)
            {
                playerPositionReference = xrOrigin.Camera.transform;
                LogDebug(
                    $"Using XR Origin camera '{playerPositionReference.name}' under '{xrOrigin.name}' for flee distance checks."
                );
                return playerPositionReference;
            }

            playerPositionReference = xrOrigin.transform;
            LogDebug(
                $"Using XR Origin transform '{playerPositionReference.name}' for flee distance checks."
            );
            return playerPositionReference;
        }

        if (preferPlayerCameraForDistance && Camera.main != null)
        {
            playerPositionReference = Camera.main.transform;
            LogDebug(
                $"Using Camera.main '{playerPositionReference.name}' for flee distance checks."
            );
            return playerPositionReference;
        }

        return playerPositionReference;
    }
}
