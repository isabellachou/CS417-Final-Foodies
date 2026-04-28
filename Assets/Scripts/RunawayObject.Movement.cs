using UnityEngine;
using UnityEngine.AI;

public partial class RunawayObject
{
    private bool StartAgentMovement(float speed)
    {
        PrepareRigidbodyForRunawayMovement();

        if (!agent.enabled)
            agent.enabled = true;

        if (!agent.isOnNavMesh)
        {
            if (
                !NavMesh.SamplePosition(
                    transform.position,
                    out NavMeshHit hit,
                    navMeshSampleRadius,
                    NavMesh.AllAreas
                )
            )
            {
                LogDebug(
                    $"Could not find NavMesh near {FormatVector(transform.position)} with radius {navMeshSampleRadius:0.##}."
                );
                return false;
            }

            agent.Warp(hit.position);
            LogDebug($"Warped agent onto NavMesh at {FormatVector(hit.position)}.");
        }

        agent.speed = speed;
        agent.stoppingDistance = targetReachDistance;
        agent.autoTraverseOffMeshLink = true;
        agent.updatePosition = false;
        agent.updateRotation = false;
        agent.isStopped = false;
        agent.baseOffset = 0f;
        lastAgentPosition = agent.nextPosition;
        LogDebug(
            $"Started agent movement. speed={speed:0.##}, nextPosition={FormatVector(agent.nextPosition)}, playerDistance={GetHorizontalPlayerDistance(agent.nextPosition):0.##}."
        );
        return true;
    }

    private void ApplyAgentMovementVisualOffset(float motionScale = 1f, bool forceMotion = false)
    {
        if (!agent.enabled || !agent.isOnNavMesh)
            return;

        Vector3 navPosition = agent.nextPosition;
        Vector3 velocity =
            agent.desiredVelocity.sqrMagnitude > 0.01f ? agent.desiredVelocity : agent.velocity;
        velocity.y = 0f;

        Vector3 offset = Vector3.up * groundClearance;
        float movementMultiplier = forceMotion
            ? Mathf.Clamp01(motionScale)
            : Mathf.InverseLerp(0.05f, Mathf.Max(0.05f, agent.speed), velocity.magnitude);
        float frequencyMultiplier = forceMotion ? Mathf.Clamp01(motionScale) : 1f;

        if (velocity.sqrMagnitude > 0.0001f)
        {
            lastMovementDirection = velocity.normalized;
            lastMovementDirection.y = 0f;
            if (lastMovementDirection.sqrMagnitude > 0.0001f)
                lastMovementDirection.Normalize();
        }

        Vector3 moveDirection =
            velocity.sqrMagnitude > 0.0001f ? velocity.normalized : lastMovementDirection;
        moveDirection.y = 0f;

        if (moveDirection.sqrMagnitude <= 0.0001f)
            moveDirection = transform.forward;

        if (moveDirection.sqrMagnitude > 0.0001f)
            moveDirection.Normalize();

        if (movementMultiplier > 0.01f)
        {
            Vector3 swayDirection = Vector3.Cross(Vector3.up, moveDirection).normalized;
            float bob =
                Mathf.Abs(Mathf.Sin(Time.time * bobFrequency * frequencyMultiplier * Mathf.PI * 2f))
                * bobHeight;
            float sway =
                Mathf.Sin(Time.time * swayFrequency * frequencyMultiplier * Mathf.PI * 2f)
                * swayDistance;
            offset += Vector3.up * bob * movementMultiplier;
            offset += swayDirection * sway * movementMultiplier;
        }

        rb.MovePosition(navPosition + offset);
    }

    private void FinishReturn()
    {
        StopAgent();
        tauntUntil = 0f;
        tauntPlayedForCurrentDestination = false;
        rb.position = originalPosition;
        rb.rotation = originalRotation;
        RestoreRigidbodySettings();
        State = RunawayState.Idle;
    }

    private void PrepareRigidbodyForRunawayMovement()
    {
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    private float CalculateGroundClearance()
    {
        Collider[] colliders = GetComponentsInChildren<Collider>();
        if (colliders == null || colliders.Length == 0)
            return Mathf.Max(0f, fallbackGroundClearance);

        bool hasBounds = false;
        Bounds combinedBounds = default;

        foreach (Collider objectCollider in colliders)
        {
            if (objectCollider == null || objectCollider.isTrigger || !objectCollider.enabled)
                continue;

            if (!hasBounds)
            {
                combinedBounds = objectCollider.bounds;
                hasBounds = true;
                continue;
            }

            combinedBounds.Encapsulate(objectCollider.bounds);
        }

        if (!hasBounds)
            return Mathf.Max(0f, fallbackGroundClearance);

        float pivotToBottom = transform.position.y - combinedBounds.min.y;
        return Mathf.Max(0f, pivotToBottom + groundClearancePadding);
    }

    private void RestoreRigidbodySettings()
    {
        rb.isKinematic = originalIsKinematic;
        rb.useGravity = originalUseGravity;
        rb.collisionDetectionMode = originalCollisionDetectionMode;
        rb.interpolation = originalInterpolation;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    private void StopAgent()
    {
        if (!agent.enabled)
        {
            tauntUntil = 0f;
            tauntPlayedForCurrentDestination = false;
            return;
        }

        if (agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }

        agent.enabled = false;
        tauntUntil = 0f;
        tauntPlayedForCurrentDestination = false;
    }
}
