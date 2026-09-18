using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(CapsuleCollider))]
public class PlayerMovement : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float walkSpeed = 3f;
    [SerializeField] private float runSpeed = 6f;
    [SerializeField] private float crouchSpeed = 1.5f;
    [SerializeField] private float acceleration = 50f;
    [SerializeField] private float deceleration = 30f;

    [Header("Rotation")]
    [SerializeField] private bool isFirstPerson;
    [SerializeField] private float rotationSpeed = 12f;

    [Header("Jump / Ground")]
    [SerializeField] private float jumpForce = 5f;
    [SerializeField] private float groundCheckDistance = 0.15f;
    [SerializeField] private LayerMask groundMask = ~0;
    [SerializeField] private float airControl = 0.3f;
    [SerializeField] private float extraGravity = 15f;

    [Header("Crouch")]
    [SerializeField] private float crouchHeight = 1.0f;
    [SerializeField] private float ceilingCheckDistance = 0.1f;
    [SerializeField] private LayerMask ceilingMask = ~0;

    [Header("Run Mode")]
    [SerializeField] private bool holdToRun = true;

    [Header("Audio")]
    [SerializeField] private AudioSource source;
    [SerializeField] private float walkDelaySteps = 0.5f;
    [SerializeField] private float runDelaySteps = 0.3f;

    [Header("References")]
    [SerializeField] private Transform cameraTransform;
    [SerializeField] private Animator animator;

    // --- internals -------------------------------------------------------
    private Rigidbody _rb;
    private CapsuleCollider _capsule;

    private Vector2 _input;
    private bool _jumpPressed;
    private bool _isRunning;
    private bool _isGrounded;
    private bool _isCrouching;
    private bool _wantsToCrouch;
    private bool _runKeyHeldLastFrame;

    private float _capsuleRadius;
    private float _standingHeight;
    private Vector3 _standingCenter;

    // cached animator hashes (no external dependencies)
    private static readonly int HorizontalHash = Animator.StringToHash("Horizontal");
    private static readonly int VerticalHash   = Animator.StringToHash("Vertical");
    private static readonly int SpeedHash      = Animator.StringToHash("Speed");
    private static readonly int GroundedHash   = Animator.StringToHash("IsGrounded");
    private static readonly int CrouchHash     = Animator.StringToHash("IsCrouching");

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _capsule = GetComponent<CapsuleCollider>();

        _rb.freezeRotation = true;              // we rotate manually
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        _capsuleRadius  = _capsule.radius;
        _standingHeight = _capsule.height;
        _standingCenter = _capsule.center;

        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
    }

    private void OnEnable()
    {
        if (source != null) StartCoroutine(FootstepsRoutine());
    }

    private void Update()
    {
        ReadInput();
        CheckGrounded();
        UpdateAnimator();
    }

    private void FixedUpdate()
    {
        HandleCrouchState();
        ApplyMovement();
        RotationHandler();
        ApplyJump();
        ApplyExtraGravity();
    }

    // ---------------------------------------------------------------- input
    private void ReadInput()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        float h = 0f, v = 0f;
        if (keyboard.wKey.isPressed) v += 1f;
        if (keyboard.sKey.isPressed) v -= 1f;
        if (keyboard.aKey.isPressed) h -= 1f;
        if (keyboard.dKey.isPressed) h += 1f;

        _input = Vector2.ClampMagnitude(new Vector2(h, v), 1f);

        if (keyboard.spaceKey.wasPressedThisFrame) _jumpPressed = true;

        _wantsToCrouch = keyboard.ctrlKey.isPressed || keyboard.cKey.isPressed;

        // run: hold or toggle
        bool runKey = keyboard.leftShiftKey.isPressed;
        if (holdToRun)
        {
            _isRunning = runKey;
        }
        else if (runKey && !_runKeyHeldLastFrame)
        {
            _isRunning = !_isRunning;
        }
        _runKeyHeldLastFrame = runKey;

        if (_isCrouching || _input.sqrMagnitude < 0.01f) _isRunning = holdToRun && _isRunning && false || (holdToRun ? _isRunning : _isRunning);
        if (_isCrouching) _isRunning = false;
    }

    // ------------------------------------------------------------- movement
    private Vector3 GetCameraRelativeDirection()
    {
        if (cameraTransform == null)
            return (transform.forward * _input.y + transform.right * _input.x).normalized;

        Vector3 camForward = Vector3.Scale(cameraTransform.forward, new Vector3(1, 0, 1)).normalized;
        Vector3 camRight   = Vector3.Scale(cameraTransform.right,   new Vector3(1, 0, 1)).normalized;
        return (camForward * _input.y + camRight * _input.x).normalized;
    }

    private void ApplyMovement()
    {
        Vector3 targetDirection = GetCameraRelativeDirection();

        float targetSpeed = _isCrouching ? crouchSpeed : (_isRunning ? runSpeed : walkSpeed);
        float control = _isGrounded ? 1f : airControl;

        Vector3 horizontalVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);

        if (targetDirection.sqrMagnitude > 0.01f)
        {
            Vector3 targetVel = targetDirection * targetSpeed;
            Vector3 change = (targetVel - horizontalVel) * control;
            change = Vector3.ClampMagnitude(change, acceleration * Time.fixedDeltaTime);
            _rb.AddForce(change, ForceMode.VelocityChange);
        }
        else if (_isGrounded)
        {
            Vector3 decel = -horizontalVel.normalized * deceleration * Time.fixedDeltaTime;
            if (decel.magnitude > horizontalVel.magnitude) decel = -horizontalVel;
            _rb.AddForce(decel, ForceMode.VelocityChange);
        }

        // clamp horizontal speed
        Vector3 clamped = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (clamped.magnitude > targetSpeed)
        {
            clamped = clamped.normalized * targetSpeed;
            _rb.linearVelocity = new Vector3(clamped.x, _rb.linearVelocity.y, clamped.z);
        }
    }

    // ------------------------------------------------------------- rotation
    private void RotationHandler()
    {
        if (cameraTransform == null) return;

        Vector3 camForward = Vector3.Scale(cameraTransform.forward, new Vector3(1, 0, 1)).normalized;
        if (camForward.sqrMagnitude < 0.001f) return;

        Quaternion targetRotation;

        if (isFirstPerson)
        {
            targetRotation = Quaternion.LookRotation(camForward);
        }
        else
        {
            if (_input.sqrMagnitude < 0.01f) return;
            Vector3 moveDir = GetCameraRelativeDirection();
            if (moveDir.sqrMagnitude < 0.001f) return;
            targetRotation = Quaternion.LookRotation(moveDir);
        }

        _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, targetRotation, rotationSpeed * Time.fixedDeltaTime));
    }

    // ----------------------------------------------------------- jump/grav
    private void ApplyJump()
    {
        if (_jumpPressed && _isGrounded && !_isCrouching)
        {
            _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
            _rb.AddForce(Vector3.up * jumpForce, ForceMode.VelocityChange);
        }
        _jumpPressed = false;
    }

    private void ApplyExtraGravity()
    {
        if (!_isGrounded)
            _rb.AddForce(Vector3.down * extraGravity, ForceMode.Acceleration);
    }

    private void CheckGrounded()
    {
        float currentHeight = _capsule.height;
        Vector3 origin = transform.position + _capsule.center
                         + Vector3.down * (currentHeight * 0.5f - _capsuleRadius - 0.01f);

        _isGrounded = Physics.SphereCast(
            origin,
            _capsuleRadius * 0.9f,
            Vector3.down,
            out _,
            groundCheckDistance,
            groundMask,
            QueryTriggerInteraction.Ignore);
    }

    // --------------------------------------------------------------- crouch
    private void HandleCrouchState()
    {
        if (_wantsToCrouch && !_isCrouching)
        {
            _isCrouching = true;
            _capsule.height = crouchHeight;
            _capsule.center = new Vector3(_standingCenter.x, crouchHeight * 0.5f, _standingCenter.z);
        }
        else if (!_wantsToCrouch && _isCrouching && !CheckForCeiling())
        {
            _isCrouching = false;
            _capsule.height = _standingHeight;
            _capsule.center = _standingCenter;
        }
    }

    private bool CheckForCeiling()
    {
        float standingTop = transform.position.y + _standingHeight - _capsuleRadius;
        Vector3 pos = new Vector3(transform.position.x, standingTop + ceilingCheckDistance, transform.position.z);

        return Physics.CheckSphere(pos, _capsuleRadius * 0.9f, ceilingMask, QueryTriggerInteraction.Ignore);
    }

    // ------------------------------------------------------------- animator
    private void UpdateAnimator()
    {
        if (animator == null) return;

        Vector3 flatVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        bool moving = flatVel.sqrMagnitude > 0.05f && _input.sqrMagnitude > 0.01f;

        float vertical = 0f;
        float horizontal = 0f;

        if (moving)
        {
            vertical = _isRunning ? 2f : 1f;

            // strafe value only meaningful in first person / locked rotation
            if (isFirstPerson)
            {
                horizontal = Mathf.Abs(_input.y) < 0.01f ? _input.x : 0f;
                if (_input.y < -0.01f) vertical = -vertical;
            }
        }

        animator.SetFloat(VerticalHash, vertical, 0.1f, Time.deltaTime);
        animator.SetFloat(HorizontalHash, horizontal, 0.1f, Time.deltaTime);
        animator.SetFloat(SpeedHash, flatVel.magnitude, 0.1f, Time.deltaTime);
        animator.SetBool(GroundedHash, _isGrounded);
        animator.SetBool(CrouchHash, _isCrouching);
    }

    // ------------------------------------------------------------ footsteps
    private IEnumerator FootstepsRoutine()
    {
        while (enabled)
        {
            Vector3 flatVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
            if (_isGrounded && flatVel.sqrMagnitude > 0.5f)
                source.Play();

            yield return new WaitForSeconds(_isRunning ? runDelaySteps : walkDelaySteps);
        }
    }

    // --------------------------------------------------------------- gizmos
    private void OnDrawGizmosSelected()
    {
        var capsule = GetComponent<CapsuleCollider>();
        if (capsule == null) return;

        float radius = capsule.radius;
        Vector3 origin = transform.position + capsule.center
                         + Vector3.down * (capsule.height * 0.5f - radius - 0.01f);
        Vector3 end = origin + Vector3.down * groundCheckDistance;

        Gizmos.color = _isGrounded ? Color.green : Color.red;
        Gizmos.DrawWireSphere(origin, radius * 0.9f);
        Gizmos.DrawWireSphere(end, radius * 0.9f);
        Gizmos.DrawLine(origin, end);

        if (Application.isPlaying && _isCrouching)
        {
            float standingTop = transform.position.y + _standingHeight - radius;
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(
                new Vector3(transform.position.x, standingTop + ceilingCheckDistance, transform.position.z),
                radius * 0.9f);
        }
    }
}
