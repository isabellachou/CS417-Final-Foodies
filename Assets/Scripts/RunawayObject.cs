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
public partial class RunawayObject : MonoBehaviour
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
    private float fleeSpeed = 0.25f;

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
    private float swayDistance = 0.02f;

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
        ResolvePrefabReferences();
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

    private void Reset()
    {
        ResolvePrefabReferences();
    }

    private void OnValidate()
    {
        ResolvePrefabReferences();
    }

    private void ResolvePrefabReferences()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
            audioSource = GetComponentInChildren<AudioSource>(true);

        if (runStartParticles == null)
            runStartParticles = GetComponentInChildren<ParticleSystem>(true);
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
}
