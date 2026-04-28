using System.Collections;
using UnityEngine;
using UnityEngine.AI;

public partial class RunawayObject
{
    private IEnumerator ActivateRoutine()
    {
        Debug.Log("Runaway activated!");
        State = RunawayState.Activating;

        OnRunawayStarted?.Invoke(this);

        yield return new WaitForSeconds(activationDelay);

        activationCoroutine = null;

        if (State != RunawayState.Activating)
            yield break;

        if (grabInteractable.isSelected)
        {
            State = RunawayState.Idle;
            RestoreRigidbodySettings();
            yield break;
        }

        if (!StartAgentMovement(fleeSpeed))
        {
            LogDebug(
                "Failed to start NavMeshAgent movement during activation; treating as escaped."
            );
            HandleEscaped();
            yield break;
        }

        if (!SetNextFleeDestination())
        {
            LogDebug(
                "Failed to set initial flee destination during activation; treating as escaped."
            );
            HandleEscaped();
            yield break;
        }

        PlayRunStartEffects();
        State = RunawayState.Fleeing;
        LogDebug("Entered Fleeing state.");
    }

    private void UpdateFleeing()
    {
        bool agentIsOnNavMesh = agent.enabled && agent.isOnNavMesh;
        if (!agentIsOnNavMesh)
        {
            LogDebug(
                $"Agent unavailable while fleeing. enabled={agent.enabled}, isOnNavMesh={agentIsOnNavMesh}. Attempting restart."
            );

            if (StartAgentMovement(fleeSpeed) && SetNextFleeDestination())
                return;

            LogDebug("Failed to restart fleeing movement; treating as escaped.");
            HandleEscaped();
            return;
        }

        agent.speed = fleeSpeed;

        if (IsTaunting())
        {
            agent.isStopped = true;
            ApplyAgentMovementVisualOffset(destinationTauntMotionScale, true);
            return;
        }

        agent.isStopped = false;

        if (
            HasReachedDestination()
            && !tauntPlayedForCurrentDestination
            && destinationTauntDuration > 0f
        )
        {
            BeginDestinationTaunt();
            ApplyAgentMovementVisualOffset(destinationTauntMotionScale, true);
            return;
        }

        if (ShouldChooseNewFleeDestination(out string repathReason))
        {
            LogDebug($"Choosing new flee destination. Reason: {repathReason}");

            if (!SetNextFleeDestination())
                LogDebug("Repath requested, but no valid flee destination could be set.");
        }

        ApplyAgentMovementVisualOffset();
    }

    private void HandleEscaped()
    {
        currentTarget = null;
        cooldownUntil = Time.time + cooldownAfterCaught;
        tauntUntil = 0f;
        tauntPlayedForCurrentDestination = false;

        OnRunawayEscaped?.Invoke(this);

        if (returnToOriginalPositionAfterEscape)
        {
            if (StartAgentMovement(returnSpeed) && TrySetAgentDestination(originalPosition))
            {
                State = RunawayState.Returning;
                return;
            }
        }

        StopAgent();
        RestoreRigidbodySettings();
        State = RunawayState.Idle;
    }

    private void UpdateReturning()
    {
        if (!agent.enabled || !agent.isOnNavMesh)
        {
            FinishReturn();
            return;
        }

        agent.speed = returnSpeed;
        ApplyAgentMovementVisualOffset();

        if (!HasReachedDestination())
            return;

        FinishReturn();
    }

    private bool ShouldChooseNewFleeDestination(out string reason)
    {
        reason = null;

        if (HasPlayerMovedCloser())
        {
            reason = "player moved closer";
            return true;
        }

        if (Time.time >= nextFleeRepathTime)
        {
            reason = "scheduled repath interval elapsed";
            return true;
        }

        if (!agent.hasPath && !agent.pathPending)
        {
            reason = "agent has no path";
            return true;
        }

        if (HasReachedDestination())
        {
            reason = "destination reached";
            return true;
        }

        return false;
    }

    private bool SetNextFleeDestination()
    {
        if (!TryChooseFleeDestination(out Vector3 destination, out Transform target))
        {
            Vector3 origin =
                agent.enabled && agent.isOnNavMesh ? agent.nextPosition : transform.position;
            LogDebug(
                $"No valid flee destination found. origin={FormatVector(origin)}, player={FormatVector(GetPlayerPosition())}."
            );
            return false;
        }

        if (!TrySetAgentDestination(destination))
        {
            LogDebug($"Failed to apply chosen destination {FormatVector(destination)}.");
            return false;
        }

        currentTarget = target;
        tauntPlayedForCurrentDestination = false;
        nextFleeRepathTime = Time.time + fleeRepathInterval;
        nextReactiveRepathTime = Time.time + reactiveRepathInterval;
        playerDistanceAtLastDestination = GetHorizontalPlayerDistance(agent.nextPosition);
        LogDebug(
            $"Set flee destination {FormatVector(destination)} target={(target != null ? target.name : "sampled")} playerDistanceNow={playerDistanceAtLastDestination:0.##} nextReactiveCheckIn={reactiveRepathInterval:0.##}s."
        );
        return true;
    }

    private bool TryChooseFleeDestination(out Vector3 destination, out Transform target)
    {
        destination = default;
        target = null;

        Vector3 origin =
            agent.enabled && agent.isOnNavMesh ? agent.nextPosition : transform.position;
        Vector3 playerPosition = GetPlayerPosition();
        Vector3 awayDirection = origin - playerPosition;
        awayDirection.y = 0f;

        if (awayDirection.sqrMagnitude <= Mathf.Epsilon)
            awayDirection = transform.forward;

        awayDirection.Normalize();
        LogDebug(
            $"Choosing flee destination. origin={FormatVector(origin)}, player={FormatVector(playerPosition)}, targetDistance={fleeDestinationDistance:0.##}, awayDirection={FormatVector(awayDirection)}, currentTarget={(currentTarget != null ? currentTarget.name : "none")}."
        );

        float bestScore = float.NegativeInfinity;
        NavMeshPath path = new NavMeshPath();

        float angleOffset = UnityEngine.Random.Range(0f, 360f);
        int sampleCount = Mathf.Max(1, fleeDestinationSampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            float angle = angleOffset + (360f * i / sampleCount);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 candidate = playerPosition + direction * fleeDestinationDistance;

            if (
                TryScoreDestination(
                    origin,
                    playerPosition,
                    awayDirection,
                    candidate,
                    path,
                    out float score,
                    out Vector3 sampledPosition
                )
                && score > bestScore
            )
            {
                bestScore = score;
                destination = sampledPosition;
                target = null;
                LogDebug(
                    $"New best sampled destination at {FormatVector(sampledPosition)} score={score:0.##} angle={angle:0.#}."
                );
            }
        }

        LogDebug(
            bestScore > float.NegativeInfinity
                ? $"Chosen destination {FormatVector(destination)} score={bestScore:0.##} target={(target != null ? target.name : "sampled")}."
                : "Destination search produced no complete NavMesh path."
        );
        return bestScore > float.NegativeInfinity;
    }

    private bool TryScoreDestination(
        Vector3 origin,
        Vector3 playerPosition,
        Vector3 awayDirection,
        Vector3 candidate,
        NavMeshPath path,
        out float score,
        out Vector3 sampledPosition
    )
    {
        score = float.NegativeInfinity;
        sampledPosition = default;

        if (
            !NavMesh.SamplePosition(
                candidate,
                out NavMeshHit hit,
                fleeDestinationSampleRadius,
                NavMesh.AllAreas
            )
        )
        {
            LogDebug(
                $"Rejected candidate {FormatVector(candidate)}: no nearby NavMesh within {fleeDestinationSampleRadius:0.##}."
            );
            return false;
        }

        if (!NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path))
        {
            LogDebug(
                $"Rejected candidate {FormatVector(hit.position)}: NavMesh.CalculatePath returned false."
            );
            return false;
        }

        if (path.status != NavMeshPathStatus.PathComplete || path.corners.Length == 0)
        {
            LogDebug(
                $"Rejected candidate {FormatVector(hit.position)}: path status={path.status}, corners={path.corners.Length}."
            );
            return false;
        }

        Vector3 moveDirection = hit.position - origin;
        moveDirection.y = 0f;

        if (moveDirection.magnitude <= targetReachDistance)
        {
            LogDebug(
                $"Rejected candidate {FormatVector(hit.position)}: too close to current position, distance={moveDirection.magnitude:0.##}."
            );
            return false;
        }

        Vector3 playerOffset = hit.position - playerPosition;
        playerOffset.y = 0f;
        float playerDistance = playerOffset.magnitude;

        if (playerDistance <= targetReachDistance)
        {
            LogDebug(
                $"Rejected candidate {FormatVector(hit.position)}: too close to player, distance={playerDistance:0.##}."
            );
            return false;
        }

        float distanceError = Mathf.Abs(playerDistance - fleeDestinationDistance);
        float awayAlignment = Vector3.Dot(playerOffset.normalized, awayDirection);
        float pathLength = GetPathLength(path);

        score = -distanceError + awayAlignment * 0.5f + pathLength * 0.1f;
        sampledPosition = hit.position;
        return true;
    }

    private bool TrySetAgentDestination(Vector3 destination)
    {
        bool agentIsOnNavMesh = agent.enabled && agent.isOnNavMesh;
        if (!agentIsOnNavMesh)
        {
            LogDebug(
                $"Cannot set destination {FormatVector(destination)} because agent enabled={agent.enabled}, isOnNavMesh={agentIsOnNavMesh}."
            );
            return false;
        }

        if (
            !NavMesh.SamplePosition(
                destination,
                out NavMeshHit hit,
                navMeshSampleRadius,
                NavMesh.AllAreas
            )
        )
        {
            LogDebug(
                $"Cannot set destination {FormatVector(destination)}: no NavMesh within {navMeshSampleRadius:0.##}."
            );
            return false;
        }

        bool setDestination = agent.SetDestination(hit.position);
        LogDebug(
            $"SetDestination({FormatVector(hit.position)}) returned {setDestination}. pathPending={agent.pathPending}, hasPath={agent.hasPath}, remainingDistance={agent.remainingDistance:0.##}."
        );
        return setDestination;
    }

    private bool HasReachedDestination()
    {
        if (agent.pathPending)
            return false;

        if (agent.remainingDistance > agent.stoppingDistance + targetReachDistance)
            return false;

        return !agent.hasPath || agent.velocity.sqrMagnitude <= 0.01f;
    }

    private static float GetPathLength(NavMeshPath path)
    {
        if (path == null || path.corners == null || path.corners.Length < 2)
            return 0f;

        float total = 0f;
        for (int i = 1; i < path.corners.Length; i++)
            total += Vector3.Distance(path.corners[i - 1], path.corners[i]);

        return total;
    }

    private bool HasPlayerMovedCloser()
    {
        Transform playerReference = ResolvePlayerPositionReference();
        if (playerReference == null)
        {
            LogDebug(
                "Skipping player-closer repath check: no player or Camera.main reference is available."
            );
            return false;
        }

        if (Time.time < nextReactiveRepathTime)
            return false;

        float currentPlayerDistance = GetHorizontalPlayerDistance(agent.nextPosition);
        float triggerDistance = playerDistanceAtLastDestination - playerCloserRepathDistance;
        bool movedCloser = currentPlayerDistance < triggerDistance;

        return movedCloser;
    }

    private void StopRunawayMotion(bool restoreRigidbodySettings)
    {
        StopActivationRoutine();
        currentTarget = null;
        cooldownUntil = 0f;
        tauntUntil = 0f;
        tauntPlayedForCurrentDestination = false;
        State = RunawayState.Idle;

        if (agent.enabled)
            StopAgent();

        if (restoreRigidbodySettings)
            RestoreRigidbodySettings();
    }

    private void StopActivationRoutine()
    {
        if (activationCoroutine == null)
            return;

        StopCoroutine(activationCoroutine);
        activationCoroutine = null;
    }
}
