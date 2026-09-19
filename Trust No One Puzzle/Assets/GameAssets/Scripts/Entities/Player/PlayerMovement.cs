using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GameAssets.Scripts.Entities.Player
{
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
        [SerializeField] private float maxMoveForce = 800f;

        [Header("Rotation")]
        [SerializeField] private bool isFirstPerson;
        [SerializeField] private float rotationSpeed = 15f;

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
        [SerializeField] private Transform cam;

        private Rigidbody _rb;
        private CapsuleCollider _capsule;
        private AnimatorController _animatorController;

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

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            _animatorController = GetComponentInChildren<AnimatorController>();

            _rb.freezeRotation = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            _rb.maxDepenetrationVelocity = 1f;

            _capsuleRadius = _capsule.radius;
            _standingHeight = _capsule.height;
            _standingCenter = _capsule.center;

            if (cam == null && Camera.main != null)
                cam = Camera.main.transform;
        }

        private void OnEnable()
        {
            if (source != null)
                StartCoroutine(FootstepsRoutine());
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

            if (keyboard.spaceKey.wasPressedThisFrame)
                _jumpPressed = true;

            _wantsToCrouch = keyboard.ctrlKey.isPressed || keyboard.cKey.isPressed;

            bool runKey = keyboard.leftShiftKey.isPressed;
            if (holdToRun)
                _isRunning = runKey;
            else if (runKey && !_runKeyHeldLastFrame)
                _isRunning = !_isRunning;
            _runKeyHeldLastFrame = runKey;

            if (_isCrouching)
                _isRunning = false;
        }

        private Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.zero;
        }

        private Vector3 GetCameraRelativeDirection()
        {
            if (cam == null)
                return Flatten(transform.forward * _input.y + transform.right * _input.x);

            Vector3 camForward = Flatten(cam.forward);
            Vector3 camRight = Flatten(cam.right);
            return (camForward * _input.y + camRight * _input.x).normalized;
        }

        private void ApplyMovement()
        {
            Vector3 targetDirection = GetCameraRelativeDirection();
            float targetSpeed = _isCrouching ? crouchSpeed : (_isRunning ? runSpeed : walkSpeed);
            float control = _isGrounded ? 1f : airControl;

            Vector3 horizontalVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
            Vector3 desiredVel = targetDirection * targetSpeed;
            Vector3 velError = desiredVel - horizontalVel;

            float maxAccel = (targetDirection.sqrMagnitude > 0.01f ? acceleration : deceleration) * control;
            Vector3 accel = Vector3.ClampMagnitude(velError / Time.fixedDeltaTime, maxAccel);

            // F = m a, then cap so a blocked player cannot infinitely shove other bodies
            Vector3 force = accel * _rb.mass;
            float forceCap = maxMoveForce * control;
            if (forceCap > 0f)
                force = Vector3.ClampMagnitude(force, forceCap);

            _rb.AddForce(force, ForceMode.Force);
        }

        private void RotationHandler()
        {
            if (cam == null) return;

            if (isFirstPerson)
            {
                Quaternion target = Quaternion.Euler(0f, cam.eulerAngles.y, 0f);
                _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, target, rotationSpeed * Time.fixedDeltaTime));
            }
            else
            {
                if (_input.sqrMagnitude < 0.01f) return;
                Vector3 moveDir = GetCameraRelativeDirection();
                if (moveDir.sqrMagnitude < 0.001f) return;
                Quaternion target = Quaternion.LookRotation(moveDir);
                _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, target, rotationSpeed * Time.fixedDeltaTime));
            }
        }

        private void ApplyJump()
        {
            if (_jumpPressed && _isGrounded && !_isCrouching)
            {
                Vector3 v = _rb.linearVelocity;
                _rb.linearVelocity = new Vector3(v.x, 0f, v.z);
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
            Vector3 origin = transform.position + _capsule.center
                             + Vector3.down * (_capsule.height * 0.5f - _capsuleRadius - 0.01f);

            _isGrounded = Physics.SphereCast(
                origin,
                _capsuleRadius * 0.9f,
                Vector3.down,
                out _,
                groundCheckDistance,
                groundMask,
                QueryTriggerInteraction.Ignore);
        }

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

        private void UpdateAnimator()
        {
            if (_animatorController == null) return;

            Vector3 horizontalVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);

            if (horizontalVel.sqrMagnitude > 0.05f)
            {
                _animatorController.VerticalValue = _isRunning ? 2 : 1;

                if (_input.x != 0 && _input.y == 0)
                    _animatorController.HorizontalValue = _input.x;
                else if (_input.y != 0)
                    _animatorController.HorizontalValue = 0;
            }
            else
            {
                _animatorController.VerticalValue = 0;
                _animatorController.HorizontalValue = 0;
            }
        }

        private IEnumerator FootstepsRoutine()
        {
            while (enabled)
            {
                Vector3 flatVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
                if (_isGrounded && flatVel.sqrMagnitude > 0.5f && source != null)
                    source.Play();

                yield return new WaitForSeconds(_isRunning ? runDelaySteps : walkDelaySteps);
            }
        }
    }
}
