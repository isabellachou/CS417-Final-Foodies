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
    private AudioSource audioSource;

    [SerializeField]
    private ParticleSystem runStartParticles;

    [SerializeField]
    private AudioClip runStartSound;

    [SerializeField]
    private AudioClip tauntStartSound;

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
    private float fleeRepathInterval = 3f;

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
    private float bobFrequency = 1.5f;

    [SerializeField]
    private float swayDistance = 0.03f;

    [SerializeField]
    private float swayFrequency = 2f;

    [Header("Timing")]
    [SerializeField]
    private float activationDelay = 0.75f;

    [SerializeField]
    private float cooldownAfterCaught = 10f;

    [SerializeField]
    private float soundCooldown = 10f;

    [SerializeField]
    private float destinationTauntDuration = 3f;

    [SerializeField]
    [Range(0.05f, 1f)]
    private float destinationTauntMotionScale = 0.6f;

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
    private float nextSoundAllowedTime;
    private float nextFleeRepathTime;
    private float nextReactiveRepathTime;
    private float tauntUntil;
    private bool tauntPlayedForCurrentDestination;
    private float playerDistanceAtLastDestination = float.PositiveInfinity;
    private float groundClearance;
    private Vector3 lastAgentPosition;
    private Vector3 lastMovementDirection = Vector3.forward;
    private Coroutine activationCoroutine;
    private Coroutine runStartParticlesCoroutine;
    private Transform runStartParticlesOriginalParent;
    private Vector3 runStartParticlesOriginalLocalPosition;
    private Quaternion runStartParticlesOriginalLocalRotation;
    private Vector3 runStartParticlesOriginalLocalScale;
    private ParticleSystemSimulationSpace runStartParticlesOriginalSimulationSpace;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        rb = GetComponent<Rigidbody>();
        agent = GetComponent<NavMeshAgent>();
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();
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

        if (runStartParticles != null)
        {
            runStartParticlesOriginalParent = runStartParticles.transform.parent;
            runStartParticlesOriginalLocalPosition = runStartParticles.transform.localPosition;
            runStartParticlesOriginalLocalRotation = runStartParticles.transform.localRotation;
            runStartParticlesOriginalLocalScale = runStartParticles.transform.localScale;
            runStartParticlesOriginalSimulationSpace = runStartParticles.main.simulationSpace;
        }
    }

    private void OnEnable()
    {
        grabInteractable.selectEntered.AddListener(OnGrabbed);
        grabInteractable.selectExited.AddListener(OnReleased);

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
        grabInteractable.selectExited.RemoveListener(OnReleased);
        StopActivationRoutine();
        StopRunStartParticlesRoutine();
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
        if (State != RunawayState.Idle)
            StopRunawayMotion(true);
    }

    private void OnReleased(SelectExitEventArgs args)
    {
        StopRunawayMotion(true);
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
        tauntUntil = 0f;
        tauntPlayedForCurrentDestination = false;

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
        nextSoundAllowedTime = 0f;
        tauntUntil = 0f;
        tauntPlayedForCurrentDestination = false;

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

    private void StopActivationRoutine()
    {
        if (activationCoroutine == null)
            return;

        StopCoroutine(activationCoroutine);
        activationCoroutine = null;
    }

    private void BeginDestinationTaunt()
    {
        if (destinationTauntDuration <= 0f)
            return;

        tauntPlayedForCurrentDestination = true;
        tauntUntil = Time.time + destinationTauntDuration;
        agent.isStopped = true;
        PlayTauntStartSound();
        LogDebug(
            $"Entering destination taunt for {destinationTauntDuration:0.##}s at {FormatVector(agent.nextPosition)}."
        );
    }

    private bool IsTaunting()
    {
        return Time.time < tauntUntil;
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

    private void PlayRunStartEffects()
    {
        PlaySound(runStartSound);

        if (runStartParticles == null)
            return;

        StopRunStartParticlesRoutine();
        runStartParticlesCoroutine = StartCoroutine(PlayDetachedRunStartParticles());
    }

    private void PlayTauntStartSound()
    {
        PlaySound(tauntStartSound);
    }

    private void PlaySound(AudioClip clip)
    {
        if (clip == null)
            return;

        if (Time.time < nextSoundAllowedTime)
            return;

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.PlayOneShot(clip);
        nextSoundAllowedTime = Time.time + soundCooldown;
    }

    private IEnumerator PlayDetachedRunStartParticles()
    {
        Transform particlesTransform = runStartParticles.transform;
        Vector3 worldPosition = particlesTransform.position;
        Quaternion worldRotation = particlesTransform.rotation;
        ParticleSystem.MainModule main = runStartParticles.main;

        runStartParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        particlesTransform.SetParent(null, true);
        particlesTransform.SetPositionAndRotation(worldPosition, worldRotation);
        runStartParticles.Play(true);

        float waitDuration =
            main.duration + main.startLifetime.constantMax + 0.1f;
        yield return new WaitForSeconds(waitDuration);

        runStartParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        particlesTransform.SetParent(runStartParticlesOriginalParent, false);
        particlesTransform.localPosition = runStartParticlesOriginalLocalPosition;
        particlesTransform.localRotation = runStartParticlesOriginalLocalRotation;
        particlesTransform.localScale = runStartParticlesOriginalLocalScale;
        main.simulationSpace = runStartParticlesOriginalSimulationSpace;
        runStartParticlesCoroutine = null;
    }

    private void StopRunStartParticlesRoutine()
    {
        if (runStartParticlesCoroutine != null)
        {
            StopCoroutine(runStartParticlesCoroutine);
            runStartParticlesCoroutine = null;
        }

        if (runStartParticles == null)
            return;

        runStartParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        Transform particlesTransform = runStartParticles.transform;
        if (particlesTransform.parent != runStartParticlesOriginalParent)
        {
            particlesTransform.SetParent(runStartParticlesOriginalParent, false);
            particlesTransform.localPosition = runStartParticlesOriginalLocalPosition;
            particlesTransform.localRotation = runStartParticlesOriginalLocalRotation;
            particlesTransform.localScale = runStartParticlesOriginalLocalScale;
        }

        ParticleSystem.MainModule main = runStartParticles.main;
        main.simulationSpace = runStartParticlesOriginalSimulationSpace;
    }
}
