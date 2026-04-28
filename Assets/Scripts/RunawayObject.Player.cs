using UnityEngine;

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

        if (preferPlayerCameraForDistance && player != null)
        {
            Camera playerCamera = player.GetComponentInChildren<Camera>();
            if (playerCamera != null)
            {
                playerPositionReference = playerCamera.transform;
                LogDebug(
                    $"Using child camera '{playerPositionReference.name}' under player '{player.name}' for flee distance checks."
                );
                return playerPositionReference;
            }
        }

        if (preferPlayerCameraForDistance && Camera.main != null)
        {
            playerPositionReference = Camera.main.transform;
            LogDebug(
                $"Using Camera.main '{playerPositionReference.name}' for flee distance checks."
            );
            return playerPositionReference;
        }

        if (player != null)
        {
            playerPositionReference = player;
            LogDebug(
                $"Using assigned player transform '{playerPositionReference.name}' for flee distance checks."
            );
        }

        return playerPositionReference;
    }
}
