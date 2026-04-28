using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
[RequireComponent(typeof(Rigidbody))]
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
    private List<Transform> escapePoints = new();

    [Header("Movement")]
    [SerializeField]
    private float fleeSpeed = 1.5f;

    [SerializeField]
    private float targetReachDistance = 0.25f;

    [SerializeField]
    private bool keepFleeingAfterReachingTarget = false;

    [SerializeField]
    private bool returnToOriginalPositionAfterEscape = false;

    [SerializeField]
    private float returnSpeed = 1.5f;

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
    private InputActionReference debugAction;

    public event Action<RunawayObject> OnRunawayStarted;
    public event Action<RunawayObject> OnRunawayCaught;
    public event Action<RunawayObject> OnRunawayEscaped;

    public RunawayState State { get; private set; } = RunawayState.Idle;
    public bool IsOnCooldown => Time.time < cooldownUntil;

    private XRGrabInteractable grabInteractable;
    private Rigidbody rb;
    private Transform currentTarget;
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private bool originalIsKinematic;
    private bool originalUseGravity;
    private CollisionDetectionMode originalCollisionDetectionMode;
    private RigidbodyInterpolation originalInterpolation;
    private float cooldownUntil;
    private Coroutine activationCoroutine;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        rb = GetComponent<Rigidbody>();

        originalPosition = transform.position;
        originalRotation = transform.rotation;
        originalIsKinematic = rb.isKinematic;
        originalUseGravity = rb.useGravity;
        originalCollisionDetectionMode = rb.collisionDetectionMode;
        originalInterpolation = rb.interpolation;
    }

    private void OnEnable()
    {
        grabInteractable.selectEntered.AddListener(OnGrabbed);
        debugAction.action.Enable();
        debugAction.action.performed += OnDebugActivate;
    }

    private void OnDisable()
    {
        grabInteractable.selectEntered.RemoveListener(OnGrabbed);
    }

    private void Update() { }

    private void OnDebugActivate(InputAction.CallbackContext ctx)
    {
        Debug.Log("Debug activate triggered");
        ActivateRunaway();
    }

    private void FixedUpdate()
    {
        if (grabInteractable.isSelected)
            return;

        if (State == RunawayState.Fleeing)
        {
            MoveTowardTarget();
            return;
        }

        if (State == RunawayState.Returning)
            MoveTowardOriginalPosition();
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

        RestoreRigidbodySettings();

        OnRunawayCaught?.Invoke(this);

        State = State == RunawayState.Caught ? RunawayState.Idle : State;
    }

    public void ResetRunaway()
    {
        StopActivationRoutine();
        currentTarget = null;
        State = RunawayState.Idle;

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
        currentTarget = ChooseEscapePoint();

        OnRunawayStarted?.Invoke(this);

        yield return new WaitForSeconds(activationDelay);

        activationCoroutine = null;

        if (State != RunawayState.Activating)
            yield break;

        if (grabInteractable.isSelected || currentTarget == null)
        {
            State = RunawayState.Idle;
            RestoreRigidbodySettings();
            yield break;
        }

        PrepareRigidbodyForRunawayMovement();
        State = RunawayState.Fleeing;
    }

    private Transform ChooseEscapePoint()
    {
        if (escapePoints == null || escapePoints.Count == 0)
            return null;

        Transform best = null;
        float bestDistance = float.NegativeInfinity;
        Vector3 from = player != null ? player.position : transform.position;

        foreach (Transform point in escapePoints)
        {
            if (point == null || point == currentTarget)
                continue;

            float distance = Vector3.Distance(from, point.position);

            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = point;
            }
        }

        return best;
    }

    private void MoveTowardTarget()
    {
        if (currentTarget == null)
        {
            HandleEscaped();
            return;
        }

        Vector3 current = rb.position;
        Vector3 target = currentTarget.position;
        Vector3 direction = target - current;
        direction.y = 0f;

        if (direction.magnitude <= targetReachDistance)
        {
            HandleReachedTarget();
            return;
        }

        Vector3 step = direction.normalized * fleeSpeed * Time.fixedDeltaTime;

        if (step.sqrMagnitude > direction.sqrMagnitude)
            step = direction;

        MoveAndRoll(current + step, step);
    }

    private void MoveTowardOriginalPosition()
    {
        Vector3 current = rb.position;
        Vector3 direction = originalPosition - current;

        if (direction.magnitude <= targetReachDistance)
        {
            rb.MovePosition(originalPosition);
            rb.MoveRotation(originalRotation);
            RestoreRigidbodySettings();
            State = RunawayState.Idle;
            return;
        }

        Vector3 step = direction.normalized * returnSpeed * Time.fixedDeltaTime;

        if (step.sqrMagnitude > direction.sqrMagnitude)
            step = direction;

        MoveAndRoll(current + step, step);
    }

    private void HandleReachedTarget()
    {
        if (keepFleeingAfterReachingTarget)
        {
            Transform nextTarget = ChooseEscapePoint();
            if (nextTarget != null)
            {
                currentTarget = nextTarget;
                return;
            }
        }

        HandleEscaped();
    }

    private void HandleEscaped()
    {
        currentTarget = null;
        cooldownUntil = Time.time + cooldownAfterCaught;

        OnRunawayEscaped?.Invoke(this);

        if (returnToOriginalPositionAfterEscape)
        {
            State = RunawayState.Returning;
            PrepareRigidbodyForRunawayMovement();
            return;
        }

        RestoreRigidbodySettings();
        State = RunawayState.Idle;
    }

    private void MoveAndRoll(Vector3 nextPosition, Vector3 step)
    {
        rb.MovePosition(nextPosition);

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

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        if (State == RunawayState.Activating || State == RunawayState.Fleeing)
            Catch();
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

    private void RestoreRigidbodySettings()
    {
        rb.isKinematic = originalIsKinematic;
        rb.useGravity = originalUseGravity;
        rb.collisionDetectionMode = originalCollisionDetectionMode;
        rb.interpolation = originalInterpolation;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    private void StopActivationRoutine()
    {
        if (activationCoroutine == null)
            return;

        StopCoroutine(activationCoroutine);
        activationCoroutine = null;
    }
}
