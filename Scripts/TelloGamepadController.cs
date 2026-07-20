using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace TelloQuest
{
    /// <summary>
    /// Tello piloting via Bluetooth gamepad (Quest 2 / Unity Input System).
    /// Button names below use Unity's abstracted layout (South/East/West/North),
    /// which map to Cross/Circle/Square/Triangle on a PS4 controller, or
    /// A/B/X/Y on an Xbox controller.
    ///
    /// Single control mode - sticks always pilot the drone (rc):
    ///   South (Cross/A): toggle takeoff/land.
    ///   West (Square/X) and East (Circle/B): take a photo (either button works).
    ///   North (Triangle/Y): toggle video recording to disk (start/stop).
    ///   L1 / R1: speed level up / sensitivity level up.
    ///   L2 / R2: speed level down / sensitivity level down.
    ///   Share/Select: emergency stop - bypasses the command queue entirely.
    ///   L3 / R3: unassigned.
    ///
    /// There used to be a second "Menu mode" (East toggled into it) for
    /// navigating a settings menu with the dpad/stick. It was removed: every
    /// setting is pre-tuned, and the only two things that ever change in
    /// flight - speed and sensitivity - already adjust live via L1/R1/L2/R2
    /// below, with no menu needed. East was freed up and now doubles West as
    /// a second photo-capture button.
    /// </summary>
    public class TelloGamepadController : MonoBehaviour
    {
        [Header("=== REFERENCE ===")]
        [SerializeField] private TelloConnection tello;

        [Header("=== SPEED LEVELS (1-5) ===")]
        [Tooltip("Speed in cm/s for each level (index 0 = level 1)")]
        [SerializeField] private int[] speedLevelsCmPerSec = { 20, 40, 60, 80, 100 };
        [SerializeField] private int defaultSpeedLevel = 3; // 1..5

        [Header("=== SENSITIVITY LEVELS (1-3) ===")]
        [Tooltip("Stick dead zone for each level (index 0 = level 1, softest)")]
        [SerializeField] private float[] deadZones = { 0.15f, 0.10f, 0.05f };
        [Tooltip("Response curve exponent (1 = linear, >1 = finer control on small movements)")]
        [SerializeField] private float[] responseCurves = { 1.5f, 1.2f, 1.0f };
        [SerializeField] private int defaultSensitivityLevel = 2; // 1..3

        [Header("=== SAFETY ===")]
        [Tooltip("If the gamepad is lost longer than this, the drone goes into hover")]
        [SerializeField] private float gamepadTimeoutSeconds = 0.5f;

        [Header("=== STICK CALIBRATION ===")]
        [Tooltip("Capture stick center offsets automatically a moment after the gamepad connects (sticks must be at rest).")]
        [SerializeField] private bool autoCalibrateOnConnect = true;
        [Tooltip("Delay before auto-calibration, to let a just-connected gamepad settle.")]
        [SerializeField] private float autoCalibrateDelay = 0.3f;

        [Header("=== SETTINGS PERSISTENCE ===")]
        [Tooltip("Remember the last speed/sensitivity levels between sessions (PlayerPrefs)")]
        [SerializeField] private bool persistSettings = true;
        private const string PrefsSpeedKey = "TelloQuest_SpeedLevel";
        private const string PrefsSensitivityKey = "TelloQuest_SensitivityLevel";

        [Header("=== HAPTIC FEEDBACK ===")]
        [Tooltip("Vibrate the gamepad on warnings (low battery, proximity...) and calibration")]
        [SerializeField] private bool enableHaptics = true;
        [SerializeField] private float warningHapticDuration = 0.2f;
        [SerializeField] private float warningHapticStrength = 0.6f;

        [Header("=== PHOTO CAPTURE (West / East) ===")]
        [Tooltip("Reference to the video display - it knows how to turn the current frame into a readable Texture2D regardless of decoder output format (direct RGBA or YUV NV12). If assigned, West/East save a PNG snapshot to disk.")]
        [SerializeField] private TelloVideoDisplay videoDisplay;
        [SerializeField] private string photoSaveFolderName = "TelloPhotos";

        [Header("=== VIDEO RECORDING (North) ===")]
        [Tooltip("Reference to the video recorder. If assigned, North button toggles recording the raw stream to disk.")]
        [SerializeField] private TelloVideoRecorder videoRecorder;

        [Header("=== UI EVENTS ===")]
        public UnityEvent<int> onSpeedLevelChanged;       // 1..5
        public UnityEvent<int> onSensitivityLevelChanged; // 1..3
        public UnityEvent onGamepadDisconnected;
        public UnityEvent onTakePhoto;
        public UnityEvent<string> onPhotoSaved; // full file path

        // ---------------------------------------------------------------
        // Internal state
        // ---------------------------------------------------------------
        private int speedLevel;       // 1..5
        private int sensitivityLevel; // 1..3

        private bool wasGamepadConnected;
        private float gamepadLossTimer;

        private Coroutine hapticRoutine;
        private Coroutine autoCalibrateRoutine;
        private Vector2 leftStickOffset;
        private Vector2 rightStickOffset;
        private bool photoCaptureWarningLogged;

        public int SpeedLevel => speedLevel;
        public int SensitivityLevel => sensitivityLevel;
        public bool IsGamepadConnected => wasGamepadConnected;

        private void Awake()
        {
            if (tello == null) tello = TelloConnection.Instance;

            int initialSpeed = defaultSpeedLevel;
            int initialSensitivity = defaultSensitivityLevel;
            if (persistSettings)
            {
                initialSpeed = PlayerPrefs.GetInt(PrefsSpeedKey, defaultSpeedLevel);
                initialSensitivity = PlayerPrefs.GetInt(PrefsSensitivityKey, defaultSensitivityLevel);
            }
            SetSpeedLevel(initialSpeed);
            SetSensitivityLevel(initialSensitivity);

            // Create the folder up front (not lazily on first photo) so it's there
            // to find via adb/MQDH as soon as the app starts.
            try { Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, photoSaveFolderName)); }
            catch (Exception e) { Debug.LogWarning($"[TelloGamepadController] Could not pre-create photos folder: {e.Message}"); }
        }

        private void OnEnable()
        {
            if (tello != null && enableHaptics) tello.OnWarningTriggered += HandleWarningHaptic;
        }

        private void OnDisable()
        {
            if (tello != null && enableHaptics) tello.OnWarningTriggered -= HandleWarningHaptic;
        }

        private void Update()
        {
            Gamepad pad = Gamepad.current;

            if (pad == null)
            {
                HandleGamepadMissing();
                return;
            }

            if (!wasGamepadConnected)
            {
                wasGamepadConnected = true;
                Debug.Log("[TelloGamepadController] Gamepad connected.");
                if (autoCalibrateOnConnect)
                {
                    if (autoCalibrateRoutine != null) StopCoroutine(autoCalibrateRoutine);
                    autoCalibrateRoutine = StartCoroutine(AutoCalibrateAfterDelay(pad));
                }
            }
            gamepadLossTimer = 0f;

            if (tello == null) return;

            // Share on PS4 (Select/Back/View on other pads): emergency stop.
            // Bypasses the command queue entirely - see TelloConnection.Emergency().
            if (pad.selectButton.wasPressedThisFrame)
            {
                tello.Emergency();
                TriggerHaptics(pad, 1f, 0.3f); // strong pulse to confirm the emergency stop
            }

            HandleSticks(pad);

            // South (Cross on PS4 / A on Xbox): single button, toggles takeoff/land
            // based on current state.
            if (pad.buttonSouth.wasPressedThisFrame) ToggleTakeoffLand();

            // West (Square on PS4 / X on Xbox) and East (Circle on PS4 / B on
            // Xbox): both save a snapshot of the current frame - East used to
            // toggle a "Menu mode" that no longer exists, so it was repurposed
            // here instead of being left unused.
            if (pad.buttonWest.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame)
            {
                onTakePhoto?.Invoke();
                CapturePhotoToDisk();
            }

            // North (Triangle on PS4 / Y on Xbox): toggle recording the raw video
            // stream to disk. The stream itself is already always-on once connected
            // (see TelloConnection.ConnectRoutine) - this only starts/stops saving it.
            if (pad.buttonNorth.wasPressedThisFrame) videoRecorder?.ToggleRecording();

            if (pad.dpad.up.wasPressedThisFrame) tello.Flip('f');
            if (pad.dpad.down.wasPressedThisFrame) tello.Flip('b');
            if (pad.dpad.left.wasPressedThisFrame) tello.Flip('l');
            if (pad.dpad.right.wasPressedThisFrame) tello.Flip('r');

            // L1/R1: speed level up / sensitivity level up. L2/R2: speed level down /
            // sensitivity level down.
            if (pad.leftShoulder.wasPressedThisFrame) SetSpeedLevel(speedLevel + 1);
            if (pad.leftTrigger.wasPressedThisFrame) SetSpeedLevel(speedLevel - 1);
            if (pad.rightShoulder.wasPressedThisFrame) SetSensitivityLevel(sensitivityLevel + 1);
            if (pad.rightTrigger.wasPressedThisFrame) SetSensitivityLevel(sensitivityLevel - 1);
        }

        private void ToggleTakeoffLand()
        {
            if (tello.IsFlying) tello.Land();
            else tello.Takeoff();
        }

        private void HandleSticks(Gamepad pad)
        {
            Vector2 left = pad.leftStick.ReadValue() - leftStickOffset;   // X = yaw, Y = throttle
            Vector2 right = pad.rightStick.ReadValue() - rightStickOffset; // X = roll, Y = pitch

            float deadZone = deadZones[sensitivityLevel - 1];
            float curve = responseCurves[sensitivityLevel - 1];

            float yaw = ApplyCurve(ApplyDeadZone(left.x, deadZone), curve);
            float throttle = ApplyCurve(ApplyDeadZone(left.y, deadZone), curve);
            float roll = ApplyCurve(ApplyDeadZone(right.x, deadZone), curve);
            float pitch = ApplyCurve(ApplyDeadZone(right.y, deadZone), curve);

            tello.SetRC(
                Mathf.RoundToInt(roll * 100),
                Mathf.RoundToInt(pitch * 100),
                Mathf.RoundToInt(throttle * 100),
                Mathf.RoundToInt(yaw * 100));
        }

        private static float ApplyDeadZone(float v, float deadZone) => Mathf.Abs(v) < deadZone ? 0f : v;

        private static float ApplyCurve(float value, float exponent)
        {
            if (Mathf.Abs(value) < 0.01f) return 0f;
            return Mathf.Sign(value) * Mathf.Pow(Mathf.Abs(value), exponent);
        }

        // =================================================================
        // SETTINGS (speed/sensitivity - only ever changed via L1/R1/L2/R2)
        // =================================================================
        public void SetSpeedLevel(int level)
        {
            speedLevel = Mathf.Clamp(level, 1, speedLevelsCmPerSec.Length);
            tello?.SetSpeed(speedLevelsCmPerSec[speedLevel - 1]);
            onSpeedLevelChanged?.Invoke(speedLevel);
            if (persistSettings) PlayerPrefs.SetInt(PrefsSpeedKey, speedLevel);
        }

        public void SetSensitivityLevel(int level)
        {
            sensitivityLevel = Mathf.Clamp(level, 1, deadZones.Length);
            onSensitivityLevelChanged?.Invoke(sensitivityLevel);
            if (persistSettings) PlayerPrefs.SetInt(PrefsSensitivityKey, sensitivityLevel);
        }

        // =================================================================
        // STICK CALIBRATION
        // =================================================================
        /// <summary>Captures the current stick position as the "center" offset. Call with sticks at rest.</summary>
        public void CalibrateSticks(Gamepad pad)
        {
            if (pad == null) return;
            leftStickOffset = pad.leftStick.ReadValue();
            rightStickOffset = pad.rightStick.ReadValue();
            Debug.Log($"[TelloGamepadController] Sticks calibrated. Left offset: {leftStickOffset}, right offset: {rightStickOffset}");
            TriggerHaptics(pad, 0.5f, 0.15f);
        }

        private IEnumerator AutoCalibrateAfterDelay(Gamepad pad)
        {
            yield return new WaitForSeconds(autoCalibrateDelay);
            if (Gamepad.current == pad) CalibrateSticks(pad);
            autoCalibrateRoutine = null;
        }

        // =================================================================
        // SAFETY: GAMEPAD DISCONNECTED
        // =================================================================
        private void HandleGamepadMissing()
        {
            if (tello == null) return;

            if (wasGamepadConnected)
            {
                wasGamepadConnected = false;
                gamepadLossTimer = 0f;
                Debug.LogWarning("[TelloGamepadController] Gamepad lost, activating safety hover.");
            }

            gamepadLossTimer += Time.deltaTime;
            if (gamepadLossTimer > gamepadTimeoutSeconds)
            {
                tello.ForceHover();
                if (gamepadLossTimer <= gamepadTimeoutSeconds + Time.deltaTime)
                    onGamepadDisconnected?.Invoke();
            }
        }

        // =================================================================
        // HAPTIC FEEDBACK
        // =================================================================
        private void HandleWarningHaptic(string message)
        {
            if (!enableHaptics || Gamepad.current == null) return;
            TriggerHaptics(Gamepad.current, warningHapticStrength, warningHapticDuration);
        }

        private void TriggerHaptics(Gamepad pad, float strength, float duration)
        {
            if (!enableHaptics || pad == null) return;
            if (hapticRoutine != null) StopCoroutine(hapticRoutine);
            hapticRoutine = StartCoroutine(HapticPulse(pad, strength, duration));
        }

        private IEnumerator HapticPulse(Gamepad pad, float strength, float duration)
        {
            pad.SetMotorSpeeds(strength, strength);
            yield return new WaitForSeconds(duration);
            pad.SetMotorSpeeds(0f, 0f);
            hapticRoutine = null;
        }

        private void OnApplicationQuit()
        {
            Gamepad.current?.SetMotorSpeeds(0f, 0f);
        }

        // =================================================================
        // PHOTO CAPTURE
        // =================================================================
        /// <summary>
        /// Saves the current frame as a PNG via TelloVideoDisplay.CaptureSnapshot(),
        /// which handles both decoder output paths (direct RGBA, and the YUV NV12
        /// path PopH264 actually takes on Quest hardware - see that method's doc
        /// comment). Requires a reference to TelloVideoDisplay, assigned in the
        /// inspector. If none is assigned, or no frame has been decoded yet, this
        /// only logs a one-time warning (onTakePhoto still fires either way).
        /// </summary>
        private void CapturePhotoToDisk()
        {
            Texture2D frame = videoDisplay != null ? videoDisplay.CaptureSnapshot() : null;
            if (frame == null)
            {
                if (!photoCaptureWarningLogged)
                {
                    photoCaptureWarningLogged = true;
                    Debug.LogWarning("[TelloGamepadController] No decoded video frame available yet: onTakePhoto fired, but no PNG was saved. Assign TelloVideoDisplay in the inspector and make sure the stream is on.");
                }
                return;
            }

            try
            {
                byte[] png = frame.EncodeToPNG();

                string folder = Path.Combine(Application.persistentDataPath, photoSaveFolderName);
                Directory.CreateDirectory(folder);
                string fileName = $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string fullPath = Path.Combine(folder, fileName);
                File.WriteAllBytes(fullPath, png);

                Debug.Log($"[TelloGamepadController] Photo saved: {fullPath}");
                onPhotoSaved?.Invoke(fullPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloGamepadController] Photo capture failed: {e.Message}");
            }
        }
    }
}
