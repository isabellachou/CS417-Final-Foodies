using UnityEngine;
using UnityEngine.InputSystem;

public partial class RunawayObject
{
    private void OnDebugActivate(InputAction.CallbackContext ctx)
    {
        ActivateRunaway();
    }

    private void LogDebug(string message)
    {
        if (!debugLogging)
            return;

        Debug.Log($"[RunawayObject:{name}] {message}", this);
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:0.##}, {value.y:0.##}, {value.z:0.##})";
    }
}
