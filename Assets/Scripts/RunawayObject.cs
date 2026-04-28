using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(NavMeshAgent))]
public class RunawayObject : MonoBehaviour
{
    public enum RunawayState
    {
        Idle,
        Activating,
        Fleeing,
        Caught,
        Returning,
    }

    [Header("References")]
    [SerializeField]
    private Transform player;

    [SerializeField]
    private bool preferPlayerCameraForDistance = true;

    [SerializeField]
    private List<Transform> escapePoints = new();

    [Header("Movement")]
    [SerializeField]
    private float fleeSpeed = 0.5f;

    [SerializeField]
    private float targetReachDistance = 0.25f;

    [SerializeField]
    private bool returnToOriginalPositionAfterEscape = false;

    [SerializeField]
    private float returnSpeed = 1.5f;

    [SerializeField]
    private float fleeDestinationDistance = 2f;

    [SerializeField]
    private float fleeDestinationSampleRadius = 2f;

    [SerializeField]
    private int fleeDestinationSampleCount = 16;

    [SerializeField]
    private float fleeRepathInterval = 0.75f;

    [SerializeField]
    private float navMeshSampleRadius = 2f;

    [SerializeField]
    private float playerCloserRepathDistance = 0.5f;

    [SerializeField]
    private float reactiveRepathInterval = 0.25f;

    [Header("Grounding")]
    [SerializeField]
    private float fallbackGroundClearance = 0.15f;

    [SerializeField]
    private float groundClearancePadding = 0.02f;

    [Header("Movement Style")]
    [SerializeField]
    private float bobHeight = 0.06f;

    [SerializeField]
    private float bobFrequency = 2.5f;

    [SerializeField]
    private float swayDistance = 0.05f;

    [SerializeField]
    private float swayFrequency = 1.75f;

    [Header("Rolling")]
    [SerializeField]
    private bool rollWhileMoving = true;

    [SerializeField]
    private float rollRadius = 0.15f;

    [SerializeField]
    private float rollRotationMultiplier = 1f;

    [Header("Timing")]
    [SerializeField]
    private float activationDelay = 0.75f;

    [SerializeField]
    private float cooldownAfterCaught = 10f;

    [Header("Debug")]
    [SerializeField]
    private bool debugLogging = false;

    [SerializeField]
    private InputActionReference debugAction;

    public event Action<RunawayObject> OnRunawayStarted;
    public event Action<RunawayObject> OnRunawayCaught;
    public event Action<RunawayObject> OnRunawayEscaped;

    public RunawayState State { get; private set; } = RunawayState.Idle;
    public bool IsOnCooldown => Time.time < cooldownUntil;

    private XRGrabInteractable grabInteractable;
    private Rigidbody rb;
    private NavMeshAgent agent;
    private Transform currentTarget;
    private Transform playerPositionReference;
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private bool originalIsKinematic;
    private bool originalUseGravity;
    private CollisionDetectionMode originalCollisionDetectionMode;
    private RigidbodyInterpolation originalInterpolation;
    private float cooldownUntil;
    private float nextFleeRepathTime;
    private float nextReactiveRepathTime;
    private float playerDistanceAtLastDestination = float.PositiveInfinity;
    private float groundClearance;
    private Vector3 lastAgentPosition;
    private Coroutine activationCoroutine;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        rb = GetComponent<Rigidbody>();
        agent = GetComponent<NavMeshAgent>();
        if (agent == null)
            agent = gameObject.AddComponent<NavMeshAgent>();

        originalPosition = transform.position;
        originalRotation = transform.rotation;
        originalIsKinematic = rb.isKinematic;
        originalUseGravity = rb.useGravity;
        originalCollisionDetectionMode = rb.collisionDetectionMode;
        originalInterpolation = rb.interpolation;

        agent.enabled = false;
        agent.autoTraverseOffMeshLink = true;
        agent.updatePosition = false;
        agent.updateRotation = false;

        groundClearance = CalculateGroundClearance();
        ResolvePlayerPositionReference();
    }

    private void OnEnable()
    {
        grabInteractable.selectEntered.AddListener(OnGrabbed);

        if (debugAction != null)
        {
            InputAction action = debugAction.action;
            if (action != null)
            {
                action.Enable();
                action.performed += OnDebugActivate;
            }
        }
    }

    private void OnDisable()
    {
        grabInteractable.selectEntered.RemoveListener(OnGrabbed);
        StopActivationRoutine();
        StopAgent();

        if (debugAction != null)
        {
            InputAction action = debugAction.action;
            if (action != null)
            {
                action.performed -= OnDebugActivate;
                action.Disable();
            }
        }
    }

    private void Update()
    {
        if (grabInteractable.isSelected)
            return;

        if (State == RunawayState.Fleeing)
        {
            UpdateFleeing();
            return;
        }

        if (State == RunawayState.Returning)
            UpdateReturning();
    }

    private void OnDebugActivate(InputAction.CallbackContext ctx)
    {
        ActivateRunaway();
    }

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        if (State == RunawayState.Activating || State == RunawayState.Fleeing)
            Catch();
    }

    [ContextMenu("Activate Runaway")]
    public void ActivateRunaway()
    {
        if (State != RunawayState.Idle)
            return;

        if (IsOnCooldown)
            return;

        if (grabInteractable.isSelected)
            return;

        activationCoroutine = StartCoroutine(ActivateRoutine());
    }

    public void Catch()
    {
        if (State != RunawayState.Activating && State != RunawayState.Fleeing)
            return;

        StopActivationRoutine();

        State = RunawayState.Caught;
        currentTarget = null;
        cooldownUntil = Time.time + cooldownAfterCaught;

        StopAgent();
        RestoreRigidbodySettings();

        OnRunawayCaught?.Invoke(this);

        State = State == RunawayState.Caught ? RunawayState.Idle : State;
    }

    public void ResetRunaway()
    {
        StopActivationRoutine();
        currentTarget = null;
        State = RunawayState.Idle;

        StopAgent();
        RestoreRigidbodySettings();
        rb.position = originalPosition;
        rb.rotation = originalRotation;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

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

        if (ShouldChooseNewFleeDestination(out string repathReason))
        {
            LogDebug($"Choosing new flee destination. Reason: {repathReason}");

            if (!SetNextFleeDestination())
                LogDebug("Repath requested, but no valid flee destination could be set.");
        }

        ApplyAgentMovementVisualOffset();
        RollFromAgentMovement();
    }

    private void HandleEscaped()
    {
        currentTarget = null;
        cooldownUntil = Time.time + cooldownAfterCaught;

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
        RollFromAgentMovement();

        if (!HasReachedDestination())
            return;

        FinishReturn();
    }

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

        if (escapePoints != null)
        {
            foreach (Transform point in escapePoints)
            {
                if (point == null || point == currentTarget)
                    continue;

                if (
                    TryScoreDestination(
                        origin,
                        playerPosition,
                        awayDirection,
                        point.position,
                        path,
                        out float score,
                        out Vector3 sampledPosition
                    )
                    && score > bestScore
                )
                {
                    bestScore = score;
                    destination = sampledPosition;
                    target = point;
                    LogDebug(
                        $"New best escape point '{point.name}' at {FormatVector(sampledPosition)} score={score:0.##}."
                    );
                }
            }
        }

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

    private void ApplyAgentMovementVisualOffset()
    {
        if (!agent.enabled || !agent.isOnNavMesh)
            return;

        Vector3 navPosition = agent.nextPosition;
        Vector3 velocity =
            agent.desiredVelocity.sqrMagnitude > 0.01f ? agent.desiredVelocity : agent.velocity;
        velocity.y = 0f;

        float speedMultiplier = Mathf.InverseLerp(
            0.05f,
            Mathf.Max(0.05f, agent.speed),
            velocity.magnitude
        );
        Vector3 offset = Vector3.up * groundClearance;

        if (speedMultiplier > 0.01f)
        {
            Vector3 moveDirection = velocity.normalized;
            Vector3 swayDirection = Vector3.Cross(Vector3.up, moveDirection).normalized;
            float bob = Mathf.Abs(Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f)) * bobHeight;
            float sway = Mathf.Sin(Time.time * swayFrequency * Mathf.PI * 2f) * swayDistance;
            offset += Vector3.up * bob * speedMultiplier;
            offset += swayDirection * sway * speedMultiplier;
        }

        rb.MovePosition(navPosition + offset);
    }

    private void RollFromAgentMovement()
    {
        Vector3 currentAgentPosition = agent.enabled ? agent.nextPosition : transform.position;
        Vector3 step = currentAgentPosition - lastAgentPosition;
        step.y = 0f;
        lastAgentPosition = currentAgentPosition;

        if (!rollWhileMoving || rollRadius <= 0f || step.sqrMagnitude <= Mathf.Epsilon)
            return;

        Vector3 rollDirection = step.normalized;
        Vector3 rollAxis = Vector3.Cross(Vector3.up, rollDirection);

        if (rollAxis.sqrMagnitude <= Mathf.Epsilon)
            return;

        float rollDegrees = step.magnitude / rollRadius * Mathf.Rad2Deg * rollRotationMultiplier;
        Quaternion rollDelta = Quaternion.AngleAxis(rollDegrees, rollAxis.normalized);
        rb.MoveRotation(rollDelta * rb.rotation);
    }

    private void FinishReturn()
    {
        StopAgent();
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
            return;

        if (agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }

        agent.enabled = false;
    }

    private void StopActivationRoutine()
    {
        if (activationCoroutine == null)
            return;

        StopCoroutine(activationCoroutine);
        activationCoroutine = null;
    }
}
