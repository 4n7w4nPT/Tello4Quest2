using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TelloQuest
{
    /// <summary>
    /// Connection manager for the consumer Tello drone (SDK 1.3 only - no EDU-only
    /// commands are used: no mission pads, no "stop", no "ap"/swarm mode).
    ///
    /// Design principles:
    /// - A single listener thread (state port 8890); everything else runs in Update()
    ///   (no stacking coroutines: lighter and easier to follow).
    /// - All telemetry comes from the drone's broadcast state stream (~10Hz):
    ///   we NEVER actively poll battery?/height?/time? in a loop, which avoids a
    ///   classic bug where the response to a one-off query gets confused with the
    ///   continuous state stream. The only exceptions are single, one-off
    ///   diagnostic queries (e.g. right after connecting, or once when the state
    ///   stream itself goes quiet) - never a repeating poll.
    /// - "One-shot" commands (takeoff, land, flip...) go through a queue processed
    ///   one at a time, with acknowledgement ("ok"/error) or timeout, as expected by
    ///   the Tello firmware. The "rc" command (real-time piloting) does NOT go
    ///   through this queue: it is sent continuously at a fixed rate, independently
    ///   of every other command.
    /// - SAFETY: "emergency" bypasses the one-shot queue entirely and is sent
    ///   immediately, so it can never be stuck waiting behind a stale/timed-out
    ///   command (see SendPriorityCommand). The consumer Tello has no "stop"
    ///   command (EDU/SDK 2.0 only) - the equivalent here is forcing rc 0 0 0 0,
    ///   which is always available (see ForceHover).
    /// </summary>
    public class TelloConnection : MonoBehaviour
    {
        public enum ConnectionState { Disconnected, Connecting, Connected, Error }

        public static TelloConnection Instance { get; private set; }

        [Header("=== NETWORK ===")]
        [SerializeField] private string telloIp = "192.168.10.1";
        [SerializeField] private int commandPort = 8889;
        [SerializeField] private int statePort = 8890;

        [Header("=== TIMING ===")]
        [Tooltip("Rate at which the 'rc' command is sent (SDK recommends 15-20 Hz)")]
        [SerializeField] private float rcSendRate = 18f;
        [Tooltip("Max time to wait for the response to a one-shot command")]
        [SerializeField] private float commandTimeout = 5f;
        [Tooltip("Beyond this delay without a state packet, the signal is considered lost")]
        [SerializeField] private float stateTimeoutThreshold = 1.5f;

        [Header("=== RECONNECTION ===")]
        [Tooltip("Max number of queued one-shot commands. Prevents unbounded growth if the link is down.")]
        [SerializeField] private int maxQueuedCommands = 10;

        [Header("=== HEALTH THRESHOLDS ===")]
        [SerializeField] private int batteryLowThreshold = 20;
        [SerializeField] private int batteryCriticalThreshold = 10;
        [SerializeField] private float temperatureWarningThreshold = 80f;
        [SerializeField] private float temperatureCriticalThreshold = 90f;
        [SerializeField] private int proximityWarningCm = 50;
        [SerializeField] private int proximityCriticalCm = 20;

        [Header("=== AUTO-SAFETY ===")]
        [Tooltip("If true, automatically triggers Land() once when battery hits the critical threshold while flying.")]
        [SerializeField] private bool autoLandOnCriticalBattery = true;

        [Header("=== SOFT ALTITUDE CEILING ===")]
        [Tooltip("Client-side geofence: the Tello has no GPS/hard ceiling, so this is enforced by clamping the throttle we send.")]
        [SerializeField] private bool enableAltitudeCeiling = false;
        [SerializeField] private float maxHeightCm = 300f;
        [Tooltip("Distance (cm) below the ceiling where throttle starts being progressively reduced, instead of cutting abruptly.")]
        [SerializeField] private float altitudeCeilingSoftMarginCm = 50f;

        [Header("=== CRASH DETECTION ===")]
        [Tooltip("Flags a probable crash/hard impact from a sudden acceleration spike (agx/agy/agz from the state stream).")]
        [SerializeField] private bool enableCrashDetection = true;
        [Tooltip("SDK acceleration units (~1000 = 1g at rest on one axis). Tune from GetTelemetryDebugString() in normal flight before relying on this.")]
        [SerializeField] private float crashAccelerationThreshold = 3500f;
        [SerializeField] private bool autoLandOnCrashSuspected = false;

        [Header("=== DEAD RECKONING (approximate, no GPS) ===")]
        [Tooltip("Integrates onboard velocity + yaw to estimate a rough return vector to the takeoff point. Drifts over time - indicative only, never precise.")]
        [SerializeField] private bool enableDeadReckoning = true;

        [Header("=== FLIGHT LOG (CSV) ===")]
        [SerializeField] private bool enableFlightLog = false;
#if !UNITY_ANDROID || UNITY_EDITOR
        [SerializeField] private string flightLogFolderName = "TelloFlightLogs"; // Editor-only fallback folder name - Android saves via MediaStore instead, see OpenFlightLog
#endif

        // ---------------------------------------------------------------
        // Network
        // ---------------------------------------------------------------
        private UdpClient commandClient;
        private UdpClient stateClient;
        private IPEndPoint telloEndPoint;
        private Thread stateThread;
        private volatile bool isRunning;

        private readonly ConcurrentQueue<string> incomingStateLines = new ConcurrentQueue<string>();

        // ---------------------------------------------------------------
        // One-shot command queue (sequential, one at a time)
        // ---------------------------------------------------------------
        private readonly Queue<string> pendingCommands = new Queue<string>();
        private bool waitingForResponse;
        private string commandInFlight = "";
        private float commandSentTime;

        // ---------------------------------------------------------------
        // Real-time RC (outside the queue, sent continuously)
        // ---------------------------------------------------------------
        private int rcRoll, rcPitch, rcThrottle, rcYaw;
        private float rcSendTimer;

        // ---------------------------------------------------------------
        // Connection state
        // ---------------------------------------------------------------
        private ConnectionState currentState = ConnectionState.Disconnected;
        private float lastStatePacketTime;
        private bool isSignalLost;
        private bool autoLandTriggered;
        private bool diagnosticQuerySentForThisSignalLoss;

        // ---------------------------------------------------------------
        // Telemetry (all SDK 1.3 fields, received via the state stream)
        // ---------------------------------------------------------------
        private readonly Dictionary<string, string> rawState = new Dictionary<string, string>();
        private float pitch, roll, yaw;
        private float velocityX, velocityY, velocityZ;
        private float accelerationX, accelerationY, accelerationZ;
        private float height;                // cm
        private float barometricAltitude;    // m
        private float timeOfFlightDistance;  // cm to the ground
        private float temperatureLow, temperatureHigh; // °C
        private int battery;                 // %
        // Battery drain-rate tracking (for EstimatedRemainingFlightTimeSeconds below) -
        // smoothed %/second, recomputed each time the battery percent ticks down.
        private float estimatedDrainPerSecond;
        private int lastBatterySamplePercent = -1;
        private float lastBatterySampleTime;
        private float flightTime;            // s (motor time)

        private string lastCommandSent = "";
        private string lastCommandResponse = "";
        private bool lastCommandSuccess;
        private float lastCommandLatencyMs;
        private int commandedSpeed = 50;

        // ---------------------------------------------------------------
        // Read-command diagnostics (battery?, sdk?, etc.)
        // ---------------------------------------------------------------
        private string sdkVersion = "";
        private bool crashLatched;

        // ---------------------------------------------------------------
        // Drone-voice alerts (one-shot per episode, not repeated every frame
        // the underlying condition stays true - see CheckHealthWarnings and
        // the Check*Alert methods near it) - short, first-person, matches the
        // "drone chat" tone rather than a technical readout.
        // ---------------------------------------------------------------
        [Header("=== ALERT THRESHOLDS (one-shot, drone-voice messages) ===")]
        [Tooltip("Descent rate (cm/s) past which a drop is considered abnormal rather than a normal controlled descent.")]
        [SerializeField] private float fastDescentThresholdCmPerSec = 150f;
        [Tooltip("How much the temperature can climb within one ~2s sample window before it counts as 'rising fast'.")]
        [SerializeField] private float temperatureFastRiseThresholdC = 3f;
        [Tooltip("How close to the critical temperature threshold counts as 'near critical' on its own, even without a fast rise.")]
        [SerializeField] private float temperatureNearCriticalMarginC = 5f;
        [Tooltip("Actual ground speed (cm/s) that, combined with near-neutral stick input, counts as wind pushing the drone rather than the pilot flying it.")]
        [SerializeField] private float windDriftSpeedThresholdCmPerSec = 40f;
        [Tooltip("Stick input magnitude (0-100 RC scale) below which the pilot is considered 'not really commanding movement' for the wind check.")]
        [SerializeField] private float windDriftMaxCommandedInput = 10f;

        private float lastLandCommandTime = -999f; // suppresses the fast-descent alert during a voluntary landing
        private float lastHeightSampleTime;
        private float heightAtLastSample;
        private bool fastDescentAlertActive;

        private float lastTemperatureSampleTime;
        private float temperatureAtLastSample;
        private bool temperatureAlertActive;

        private bool windAlertActive;
        private bool signalDegradingAlertActive;
        private bool batteryLowAlertActive;
        private bool batteryCriticalAlertActive;

        // ---------------------------------------------------------------
        // Dead reckoning (position estimate relative to takeoff)
        // ---------------------------------------------------------------
        private Vector2 estimatedPositionCm; // (x = east-ish, y = north-ish, in the drone's own start frame)
        private float lastReckoningTime;

        // ---------------------------------------------------------------
        // Flight log
        // ---------------------------------------------------------------
        private FileStream flightLogFileStream; // Editor fallback only
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject flightLogOutputStream;
#endif

        // ---------------------------------------------------------------
        // Public properties
        // ---------------------------------------------------------------
        public ConnectionState CurrentConnectionState => currentState;
        public bool IsConnected => currentState == ConnectionState.Connected;
        public bool IsFlying { get; private set; }
        private const float FlyingHeightThresholdCm = 8f; // small buffer above 0 to avoid noise right at ground level
        public bool IsStreaming { get; private set; }
        public bool IsSignalLost => isSignalLost;

        public float Pitch => pitch;
        public float Roll => roll;
        public float Yaw => yaw;

        public float VelocityX => velocityX;
        public float VelocityY => velocityY;
        public float VelocityZ => velocityZ;
        public float VelocityMagnitude => Mathf.Sqrt(velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ);

        public float AccelerationX => accelerationX;
        public float AccelerationY => accelerationY;
        public float AccelerationZ => accelerationZ;
        public float AccelerationMagnitude => Mathf.Sqrt(accelerationX * accelerationX + accelerationY * accelerationY + accelerationZ * accelerationZ);

        public float Height => height;
        public float HeightM => height / 100f;
        public float BarometricAltitude => barometricAltitude;
        public float TimeOfFlightDistance => timeOfFlightDistance;

        public float TemperatureLow => temperatureLow;
        public float TemperatureHigh => temperatureHigh;
        public float TemperatureAverage => (temperatureLow + temperatureHigh) / 2f;

        public int Battery => battery;

        // =================================================================
        // RUNTIME-ADJUSTABLE SETTINGS (Settings screen) - plain get/set
        // properties over the serialized fields above, so TelloSettingsScreen
        // can read the current value and write a new one without needing
        // dedicated methods per field. Persisted via PlayerPrefs - see
        // LoadPersistedSettings() in Awake() and SavePersistedSettings(),
        // called by TelloSettingsScreen on save.
        // =================================================================
        public int BatteryLowThreshold { get => batteryLowThreshold; set => batteryLowThreshold = value; }
        public int BatteryCriticalThreshold { get => batteryCriticalThreshold; set => batteryCriticalThreshold = value; }
        public float TemperatureWarningThreshold { get => temperatureWarningThreshold; set => temperatureWarningThreshold = value; }
        public float TemperatureCriticalThreshold { get => temperatureCriticalThreshold; set => temperatureCriticalThreshold = value; }
        public int ProximityWarningCm { get => proximityWarningCm; set => proximityWarningCm = value; }
        public int ProximityCriticalCm { get => proximityCriticalCm; set => proximityCriticalCm = value; }
        public bool AutoLandOnCriticalBattery { get => autoLandOnCriticalBattery; set => autoLandOnCriticalBattery = value; }
        public bool EnableAltitudeCeiling { get => enableAltitudeCeiling; set => enableAltitudeCeiling = value; }
        public float MaxHeightCm { get => maxHeightCm; set => maxHeightCm = value; }
        public float AltitudeCeilingSoftMarginCm { get => altitudeCeilingSoftMarginCm; set => altitudeCeilingSoftMarginCm = value; }
        public bool EnableCrashDetection { get => enableCrashDetection; set => enableCrashDetection = value; }
        public float CrashAccelerationThreshold { get => crashAccelerationThreshold; set => crashAccelerationThreshold = value; }
        public bool AutoLandOnCrashSuspected { get => autoLandOnCrashSuspected; set => autoLandOnCrashSuspected = value; }
        public bool EnableDeadReckoning { get => enableDeadReckoning; set => enableDeadReckoning = value; }
        public bool EnableFlightLog { get => enableFlightLog; set => enableFlightLog = value; }

        private const string PrefsPrefix = "TelloQuest_Settings_";

        private void LoadPersistedSettings()
        {
            batteryLowThreshold = PlayerPrefs.GetInt(PrefsPrefix + "BatteryLow", batteryLowThreshold);
            batteryCriticalThreshold = PlayerPrefs.GetInt(PrefsPrefix + "BatteryCritical", batteryCriticalThreshold);
            temperatureWarningThreshold = PlayerPrefs.GetFloat(PrefsPrefix + "TempWarning", temperatureWarningThreshold);
            temperatureCriticalThreshold = PlayerPrefs.GetFloat(PrefsPrefix + "TempCritical", temperatureCriticalThreshold);
            proximityWarningCm = PlayerPrefs.GetInt(PrefsPrefix + "ProxWarning", proximityWarningCm);
            proximityCriticalCm = PlayerPrefs.GetInt(PrefsPrefix + "ProxCritical", proximityCriticalCm);
            autoLandOnCriticalBattery = PlayerPrefs.GetInt(PrefsPrefix + "AutoLandBattery", autoLandOnCriticalBattery ? 1 : 0) == 1;
            enableAltitudeCeiling = PlayerPrefs.GetInt(PrefsPrefix + "AltCeilingOn", enableAltitudeCeiling ? 1 : 0) == 1;
            maxHeightCm = PlayerPrefs.GetFloat(PrefsPrefix + "MaxHeight", maxHeightCm);
            altitudeCeilingSoftMarginCm = PlayerPrefs.GetFloat(PrefsPrefix + "AltSoftMargin", altitudeCeilingSoftMarginCm);
            enableCrashDetection = PlayerPrefs.GetInt(PrefsPrefix + "CrashDetectOn", enableCrashDetection ? 1 : 0) == 1;
            crashAccelerationThreshold = PlayerPrefs.GetFloat(PrefsPrefix + "CrashAccelThreshold", crashAccelerationThreshold);
            autoLandOnCrashSuspected = PlayerPrefs.GetInt(PrefsPrefix + "AutoLandCrash", autoLandOnCrashSuspected ? 1 : 0) == 1;
            enableDeadReckoning = PlayerPrefs.GetInt(PrefsPrefix + "DeadReckoningOn", enableDeadReckoning ? 1 : 0) == 1;
            enableFlightLog = PlayerPrefs.GetInt(PrefsPrefix + "FlightLogOn", enableFlightLog ? 1 : 0) == 1;
        }

        /// <summary>Called by TelloSettingsScreen after writing new values via the properties above, to persist them for next launch.</summary>
        public void SavePersistedSettings()
        {
            PlayerPrefs.SetInt(PrefsPrefix + "BatteryLow", batteryLowThreshold);
            PlayerPrefs.SetInt(PrefsPrefix + "BatteryCritical", batteryCriticalThreshold);
            PlayerPrefs.SetFloat(PrefsPrefix + "TempWarning", temperatureWarningThreshold);
            PlayerPrefs.SetFloat(PrefsPrefix + "TempCritical", temperatureCriticalThreshold);
            PlayerPrefs.SetInt(PrefsPrefix + "ProxWarning", proximityWarningCm);
            PlayerPrefs.SetInt(PrefsPrefix + "ProxCritical", proximityCriticalCm);
            PlayerPrefs.SetInt(PrefsPrefix + "AutoLandBattery", autoLandOnCriticalBattery ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "AltCeilingOn", enableAltitudeCeiling ? 1 : 0);
            PlayerPrefs.SetFloat(PrefsPrefix + "MaxHeight", maxHeightCm);
            PlayerPrefs.SetFloat(PrefsPrefix + "AltSoftMargin", altitudeCeilingSoftMarginCm);
            PlayerPrefs.SetInt(PrefsPrefix + "CrashDetectOn", enableCrashDetection ? 1 : 0);
            PlayerPrefs.SetFloat(PrefsPrefix + "CrashAccelThreshold", crashAccelerationThreshold);
            PlayerPrefs.SetInt(PrefsPrefix + "AutoLandCrash", autoLandOnCrashSuspected ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "DeadReckoningOn", enableDeadReckoning ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "FlightLogOn", enableFlightLog ? 1 : 0);
        }
        public float FlightTime => flightTime;
        public string FlightTimeFormatted => FormatTime(flightTime);

        /// <summary>Estimated seconds of flight remaining at the current smoothed battery
        /// drain rate. -1 until at least one battery percent drop has been observed (no
        /// estimate possible yet - e.g. right after connecting).</summary>
        public float EstimatedRemainingFlightTimeSeconds => estimatedDrainPerSecond > 0f ? battery / estimatedDrainPerSecond : -1f;
        public string EstimatedRemainingFlightTimeFormatted => EstimatedRemainingFlightTimeSeconds >= 0f ? FormatTime(EstimatedRemainingFlightTimeSeconds) : "--:--";

        public string LastCommandSent => lastCommandSent;
        public string LastCommandResponse => lastCommandResponse;
        public bool LastCommandSuccess => lastCommandSuccess;
        public float LastCommandLatencyMs => lastCommandLatencyMs;
        public int CommandedSpeed => commandedSpeed;
        public Dictionary<string, string> RawState => new Dictionary<string, string>(rawState);

        /// <summary>Firmware SDK version string, populated after a "sdk?" query (sent once automatically on connect).</summary>
        public string SdkVersion => sdkVersion;

        /// <summary>Rough estimated position (cm) relative to the takeoff point. Dead reckoning: drifts over time, indicative only.</summary>
        public Vector2 EstimatedPositionCm => estimatedPositionCm;
        public float DistanceFromHomeCm => estimatedPositionCm.magnitude;
        /// <summary>Bearing (degrees, 0-360) to point back towards the takeoff point, in the drone's own start-heading frame.</summary>
        public float BearingToHomeDegrees => (Mathf.Atan2(-estimatedPositionCm.x, -estimatedPositionCm.y) * Mathf.Rad2Deg + 360f) % 360f;

        public bool IsBatteryLow => battery > 0 && battery <= batteryLowThreshold;
        public bool IsBatteryCritical => battery > 0 && battery <= batteryCriticalThreshold;
        public bool IsTemperatureWarning => temperatureHigh >= temperatureWarningThreshold;
        public bool IsTemperatureCritical => temperatureHigh >= temperatureCriticalThreshold;
        public bool IsProximityWarning => timeOfFlightDistance > 0 && timeOfFlightDistance <= proximityWarningCm;
        public bool IsProximityCritical => timeOfFlightDistance > 0 && timeOfFlightDistance <= proximityCriticalCm;

        // ---------------------------------------------------------------
        // Events (for the Quest 2 UI)
        // ---------------------------------------------------------------
        public event Action<ConnectionState> OnConnectionStateChanged;
        public event Action OnTelemetryUpdated;
        public event Action<string> OnWarningTriggered;
        public event Action<string, string, bool> OnCommandResponseReceived; // (command, response, success)

        /// <summary>Fired the instant Takeoff()/Land() is called, before the drone confirms
        /// anything - lets the UI show an immediate "sending" acknowledgment separate from
        /// OnCommandResponseReceived's later "confirmed" signal. Scoped to just these two
        /// commands for now, not every command sent.</summary>
        public event Action<string> OnFlightCommandSent;
        public event Action OnCrashSuspected;

        // =================================================================
        // LIFECYCLE
        // =================================================================
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            LoadPersistedSettings();

            // Internal bookkeeping hooked onto the public event: keeps IsFlying and
            // SdkVersion accurate based on what the drone actually acknowledged.
            OnCommandResponseReceived += HandleCommandSideEffects;
        }

        private void Start() => Connect();

        private void OnDestroy()
        {
            OnCommandResponseReceived -= HandleCommandSideEffects;
            Disconnect();
        }

        // Connect() tries once per call and gives up on failure (state = Error) -
        // repetition is owned entirely by the periodic auto-retry timer below
        // plus this explicit nudge on focus regain, so there's exactly one place
        // that decides how often to retry, not two competing loops.
        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) TryReconnectIfNeeded();
        }

        private void OnApplicationPause(bool isPaused)
        {
            if (!isPaused) TryReconnectIfNeeded();
        }

        private void TryReconnectIfNeeded()
        {
            if (currentState == ConnectionState.Disconnected || currentState == ConnectionState.Error)
            {
                Debug.Log("[TelloConnection] Retrying connection.");
                Connect();
            }
        }

        [Header("=== AUTO-RETRY WHILE DISCONNECTED ===")]
        [Tooltip("How often to automatically retry the connection while in Disconnected/Error state - deliberately not too aggressive, a real handshake attempt is heavier than just checking a status flag.")]
        [SerializeField] private float autoRetryIntervalSeconds = 2f;
        private float autoRetryTimer;

        private void Update()
        {
            autoRetryTimer += Time.deltaTime;
            if (autoRetryTimer >= autoRetryIntervalSeconds)
            {
                autoRetryTimer = 0f;
                TryReconnectIfNeeded();
            }

            // 1) Incoming telemetry (state thread -> main thread)
            while (incomingStateLines.TryDequeue(out string line))
            {
                ParseState(line);
                UpdateDeadReckoning();
                lastStatePacketTime = Time.time;
                if (isSignalLost)
                {
                    isSignalLost = false;
                    Debug.Log("[TelloConnection] Signal restored.");
                }
                CheckHealthWarnings();
                CheckCrashDetection();
                WriteFlightLogLine();
                OnTelemetryUpdated?.Invoke();
            }

            if (!isRunning) return;

            // 2) Command responses (non-blocking read on the command socket)
            try
            {
                while (commandClient != null && commandClient.Available > 0)
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = commandClient.Receive(ref remote);
                    string response = Encoding.ASCII.GetString(data).Trim();
                    Debug.Log($"[Tello <<] {response}");
                    HandleCommandResponse(response);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloConnection] Error receiving response: {e.Message}");
            }

            // 3) Timeout on a pending command
            if (waitingForResponse && Time.time - commandSentTime > commandTimeout)
            {
                Debug.LogWarning($"[TelloConnection] Timeout on command '{commandInFlight}'.");
                HandleCommandResponse("timeout");
            }

            // 4) Send the next command if the queue isn't empty
            if (!waitingForResponse && pendingCommands.Count > 0)
            {
                string next = pendingCommands.Dequeue();
                SendRaw(next);
                commandInFlight = next;
                commandSentTime = Time.time;
                waitingForResponse = true;
            }

            // 5) RC sent at a fixed rate (independent of the command queue)
            if (currentState == ConnectionState.Connected)
            {
                rcSendTimer += Time.deltaTime;
                float interval = 1f / rcSendRate;
                if (rcSendTimer >= interval)
                {
                    rcSendTimer = 0f;
                    SendRaw($"rc {rcRoll} {rcPitch} {rcThrottle} {rcYaw}");
                }
            }

            // 6) Signal loss detection. On the transition we fire a single, one-off
            //    diagnostic query (battery?) - not a loop - to tell apart "the state
            //    broadcast died but the command link is still alive" from "the whole
            //    link is down" (the response, or lack of one, shows up in
            //    LastCommandResponse / OnCommandResponseReceived).
            if (Time.time - lastStatePacketTime > stateTimeoutThreshold)
            {
                if (!isSignalLost)
                {
                    isSignalLost = true;
                    diagnosticQuerySentForThisSignalLoss = false;
                    Debug.LogWarning("[TelloConnection] Signal lost: no more telemetry received.");
                    OnWarningTriggered?.Invoke("Signal lost: no telemetry received");
                }
                else if (!diagnosticQuerySentForThisSignalLoss)
                {
                    diagnosticQuerySentForThisSignalLoss = true;
                    SendCommand("battery?");
                }
            }
            else
            {
                // Earlier warning tier: state packets are arriving late (more than half
                // the timeout window since the last one) but haven't fully timed out
                // yet - a heads-up before the connection actually drops, not just an
                // announcement after the fact.
                float sinceLastState = Time.time - lastStatePacketTime;
                if (sinceLastState > stateTimeoutThreshold * 0.5f)
                {
                    if (!signalDegradingAlertActive)
                    {
                        signalDegradingAlertActive = true;
                        OnWarningTriggered?.Invoke("Signal's getting shaky, I might not make it back on my own.");
                    }
                }
                else if (sinceLastState < stateTimeoutThreshold * 0.25f)
                {
                    signalDegradingAlertActive = false; // clearly healthy again - allow it to fire again next time
                }
            }
        }

        // =================================================================
        // CONNECTION
        // =================================================================
        public void Connect()
        {
            if (currentState == ConnectionState.Connecting || currentState == ConnectionState.Connected)
            {
                Debug.LogWarning("[TelloConnection] Already connected or connecting.");
                return;
            }
            SetState(ConnectionState.Connecting);
            StartCoroutine(ConnectRoutine());
        }

        private IEnumerator ConnectRoutine()
        {
            // Single attempt per call - TryReconnectIfNeeded() (in Update, every
            // autoRetryIntervalSeconds) is what owns retrying repeatedly. Nesting
            // another multi-attempt loop in here on top of that just stacked
            // delays and made recovery feel slow for no reliability benefit.
            bool openOk = TryOpenSockets();
            if (!openOk)
            {
                Debug.LogWarning("[TelloConnection] Failed to open sockets.");
                SetState(ConnectionState.Error);
                yield break;
            }

            // Handshake: "command" must reply "ok" to validate the connection
            bool ackReceived = false;
            bool ackSuccess = false;
            Action<string, string, bool> handler = (cmd, resp, ok) =>
            {
                if (cmd == "command") { ackReceived = true; ackSuccess = ok; }
            };
            OnCommandResponseReceived += handler;

            pendingCommands.Clear();
            waitingForResponse = false;
            pendingCommands.Enqueue("command");

            float startTime = Time.time;
            while (!ackReceived && Time.time - startTime < commandTimeout)
                yield return null;

            OnCommandResponseReceived -= handler;

            if (ackReceived && ackSuccess)
            {
                SetState(ConnectionState.Connected);
                Debug.Log("[TelloConnection] Connected to the Tello.");
                OpenFlightLog();
                SendCommand("sdk?"); // one-off diagnostic query, not a loop
                StreamOff(); // force a clean encoder restart (see StreamOn's doc comment for why)
                StreamOn();
                yield break;
            }

            Debug.LogWarning("[TelloConnection] Handshake failed.");
            CleanupConnection();
            SetState(ConnectionState.Error);
        }

        private bool TryOpenSockets()
        {
            try
            {
                telloEndPoint = new IPEndPoint(IPAddress.Parse(telloIp), commandPort);
                commandClient = new UdpClient(commandPort);
                stateClient = new UdpClient(statePort);

                isRunning = true;
                lastStatePacketTime = Time.time;
                isSignalLost = false;
                autoLandTriggered = false;
                crashLatched = false;

                stateThread = new Thread(StateListenLoop) { IsBackground = true };
                stateThread.Start();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloConnection] Could not open sockets: {e.Message}");
                CleanupConnection();
                return false;
            }
        }

        public void Disconnect()
        {
            isRunning = false;
            StopAllCoroutines();
            CleanupConnection();
            CloseFlightLog();
            SetState(ConnectionState.Disconnected);
        }

        private void CleanupConnection()
        {
            // MUST happen before Join/Close: without this, the state thread's
            // while(isRunning) loop never exits, and once stateClient is disposed
            // below it spins in a tight catch-and-retry loop forever - burns a
            // CPU core continuously and starves everything else (including video
            // decode/render) of performance. This was a real bug, not a wiring issue.
            isRunning = false;
            try { stateThread?.Join(500); } catch { /* expected if already stopped */ }
            commandClient?.Close();
            stateClient?.Close();
            commandClient = null;
            stateClient = null;
            pendingCommands.Clear();
            waitingForResponse = false;
        }

        private void SetState(ConnectionState newState)
        {
            if (currentState == newState) return;
            currentState = newState;
            Debug.Log($"[TelloConnection] Connection state: {newState}");
            OnConnectionStateChanged?.Invoke(newState);
        }

        // =================================================================
        // SENDING COMMANDS
        // =================================================================

        /// <summary>Queues a one-shot command (processed sequentially, with acknowledgement).</summary>
        public void SendCommand(string cmd)
        {
            if (!isRunning) return;

            // Safety valve: never let the queue grow unbounded (e.g. spammed while
            // disconnected or while a slow command is in flight).
            if (pendingCommands.Count >= maxQueuedCommands)
            {
                Debug.LogWarning($"[TelloConnection] Command queue full, dropping '{cmd}'.");
                return;
            }
            pendingCommands.Enqueue(cmd);
        }

        /// <summary>
        /// Sends a safety-critical command immediately, bypassing the one-shot queue.
        /// Use ONLY for commands that must never wait behind another command's
        /// timeout (e.g. "emergency"). Whatever was queued/in-flight is dropped.
        /// </summary>
        private void SendPriorityCommand(string cmd)
        {
            if (!isRunning) return;
            pendingCommands.Clear();
            waitingForResponse = false;
            SendRaw(cmd);
            commandInFlight = cmd;
            commandSentTime = Time.time;
            waitingForResponse = true;
        }

        private void SendRaw(string cmd)
        {
            if (commandClient == null) return;
            try
            {
                byte[] data = Encoding.ASCII.GetBytes(cmd);
                commandClient.Send(data, data.Length, telloEndPoint);
                lastCommandSent = cmd;
                Debug.Log($"[Tello >>] {cmd}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloConnection] Failed to send '{cmd}': {e.Message}");
            }
        }

        private void HandleCommandResponse(string response)
        {
            waitingForResponse = false;
            lastCommandResponse = response;

            // Read-commands ("sdk?", "battery?"...) never reply "ok" - they reply
            // with the actual value queried - so judging them by the "ok" check
            // below (meant for action commands like "takeoff"/"land") would mark
            // every successful read as a failure. Judge reads by the absence of
            // an error/timeout instead.
            bool isReadCommand = commandInFlight.EndsWith("?", StringComparison.Ordinal);
            lastCommandSuccess = isReadCommand
                ? !response.Equals("error", StringComparison.OrdinalIgnoreCase) && !response.Equals("timeout", StringComparison.OrdinalIgnoreCase)
                : response.Equals("ok", StringComparison.OrdinalIgnoreCase);

            lastCommandLatencyMs = (Time.time - commandSentTime) * 1000f;

            OnCommandResponseReceived?.Invoke(commandInFlight, response, lastCommandSuccess);
            commandInFlight = "";
        }

        /// <summary>
        /// Internal bookkeeping driven by command responses: keeps IsFlying consistent
        /// with what the drone actually acknowledged, and captures read-command values
        /// (currently just "sdk?") that aren't "ok"/"error" but a raw value.
        /// </summary>
        private void HandleCommandSideEffects(string cmd, string response, bool success)
        {
            // Only an explicit "error" is a real signal the drone rejected the
            // command - a timeout just means the acknowledgment packet was lost
            // over UDP, which says nothing about whether the command itself was
            // received and executed. Treating a timeout the same as "error" here
            // used to flip IsFlying back to its previous state on every lost ack,
            // which could cause Land()/Takeoff() to fire again and again in a
            // loop (each call flips IsFlying optimistically, then a timeout on
            // THAT command's own ack would immediately flip it right back).
            // Leaving a timed-out command's guess alone and letting the
            // height-based inference further down correct it if it's ever
            // actually wrong is far more robust.
            if (!success && response.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                if (cmd == "takeoff") IsFlying = false; // takeoff refused, we're still on the ground
                else if (cmd == "land") IsFlying = true; // land refused, we're still airborne
            }

            if (cmd == "sdk?" && !response.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                sdkVersion = response;
                Debug.Log($"[TelloConnection] Tello SDK version: {sdkVersion}");
            }
        }

        // ---------------------------------------------------------------
        // High-level commands
        // ---------------------------------------------------------------
        public void Takeoff()
        {
            SendCommand("takeoff");
            OnFlightCommandSent?.Invoke("takeoff");
            IsFlying = true;
            estimatedPositionCm = Vector2.zero;
            lastReckoningTime = Time.time;
        }

        public void Land()
        {
            SendCommand("land");
            OnFlightCommandSent?.Invoke("land");
            IsFlying = false;
            lastLandCommandTime = Time.time;
        }

        /// <summary>Cuts the motors immediately. Bypasses the command queue for safety.</summary>
        public void Emergency() { SendPriorityCommand("emergency"); IsFlying = false; }

        /// <summary>
        /// Client-side equivalent of "stop" (which only exists on the Tello EDU / SDK 2.0):
        /// forces the RC channels to zero, right away, without going through the command
        /// queue. The drone hovers on its own as soon as it stops receiving stick input.
        /// </summary>
        public void ForceHover() => SetRC(0, 0, 0, 0);

        public void Flip(char direction)
        {
            string command = $"flip {direction}";
            SendCommand(command);
            OnFlightCommandSent?.Invoke(command);
        }

        /// <summary>Straight-line flight to a relative point (cm, -500..500, excluding -20..20) at a given speed (cm/s). Supported on the consumer Tello (SDK 1.3).</summary>
        public void GoTo(int x, int y, int z, int speedCmPerSec) =>
            SendCommand($"go {x} {y} {z} {Mathf.Clamp(speedCmPerSec, 10, 100)}");

        /// <summary>Curved flight through two relative waypoints (cm) at a given speed (cm/s). Supported on the consumer Tello (SDK 1.3).</summary>
        public void Curve(int x1, int y1, int z1, int x2, int y2, int z2, int speedCmPerSec) =>
            SendCommand($"curve {x1} {y1} {z1} {x2} {y2} {z2} {Mathf.Clamp(speedCmPerSec, 10, 60)}");

        public void SetSpeed(int speedCmPerSec)
        {
            int clamped = Mathf.Clamp(speedCmPerSec, 10, 100);
            SendCommand($"speed {clamped}");
            commandedSpeed = clamped;
        }

        public void StreamOn() { SendCommand("streamon"); IsStreaming = true; }
        /// <summary>
        /// On connect, this is sent immediately before "streamon" (not on its own) to force
        /// the Tello's video encoder to restart. Without this, "streamon" on a drone whose
        /// encoder was already running (e.g. a previous session's stream still active) can
        /// silently NOT re-send the SPS/PPS/IDR burst PopH264 needs to start decoding -
        /// our receiver would then only ever see ongoing P-slices and never produce a
        /// single frame, even though packets are flowing perfectly normally.
        /// </summary>
        public void StreamOff() { SendCommand("streamoff"); IsStreaming = false; }

        /// <summary>
        /// Real-time piloting (-100 to 100). Sent continuously, outside the command queue.
        /// Applies the soft altitude ceiling (if enabled) before the value reaches the drone.
        /// </summary>
        public void SetRC(int roll, int pitch, int throttle, int yaw)
        {
            rcRoll = Mathf.Clamp(roll, -100, 100);
            rcPitch = Mathf.Clamp(pitch, -100, 100);
            rcYaw = Mathf.Clamp(yaw, -100, 100);
            rcThrottle = Mathf.Clamp(ApplyAltitudeCeiling(throttle), -100, 100);
        }

        private int ApplyAltitudeCeiling(int throttle)
        {
            if (!enableAltitudeCeiling || throttle <= 0 || height <= 0) return throttle;

            float remaining = maxHeightCm - height;
            if (remaining <= 0f) return 0; // at/above the ceiling: no more climbing, descending is still allowed
            if (remaining >= altitudeCeilingSoftMarginCm) return throttle;

            // Inside the soft margin: taper the climb rate down to zero as we approach the ceiling.
            float factor = Mathf.Clamp01(remaining / altitudeCeilingSoftMarginCm);
            return Mathf.RoundToInt(throttle * factor);
        }

        // =================================================================
        // STATE RECEPTION (background thread)
        // =================================================================
        private void StateListenLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, statePort);
            while (isRunning)
            {
                try
                {
                    byte[] data = stateClient.Receive(ref remote);
                    incomingStateLines.Enqueue(Encoding.ASCII.GetString(data));
                }
                catch (SocketException)
                {
                    // expected when the socket closes (Disconnect)
                }
                catch (Exception e)
                {
                    if (isRunning) Debug.LogWarning($"[TelloConnection] State reception error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Updates the smoothed battery drain-rate estimate. The Tello only reports
        /// battery in whole percent steps, so a naive frame-to-frame slope would be
        /// extremely jumpy - this only samples on an actual percent change, and
        /// smooths across samples (rather than trusting a single interval) so the
        /// remaining-time estimate doesn't jump around.
        /// </summary>
        private void UpdateBatteryDrainEstimate(int newBattery)
        {
            if (lastBatterySamplePercent >= 0 && newBattery < lastBatterySamplePercent)
            {
                float elapsed = Time.time - lastBatterySampleTime;
                if (elapsed > 0.5f) // guards against duplicate/near-simultaneous state packets
                {
                    float instantRate = (lastBatterySamplePercent - newBattery) / elapsed; // %/s
                    estimatedDrainPerSecond = estimatedDrainPerSecond <= 0f
                        ? instantRate
                        : Mathf.Lerp(estimatedDrainPerSecond, instantRate, 0.3f);
                }
            }

            if (newBattery != lastBatterySamplePercent)
            {
                lastBatterySamplePercent = newBattery;
                lastBatterySampleTime = Time.time;
            }
        }

        private void ParseState(string line)
        {
            // Format: "pitch:0;roll:0;yaw:0;vgx:0;...;agz:-999.00;"
            string[] pairs = line.Trim(';', '\r', '\n', ' ').Split(';');
            foreach (string pair in pairs)
            {
                int idx = pair.IndexOf(':');
                if (idx <= 0) continue;

                string key = pair.Substring(0, idx);
                string value = pair.Substring(idx + 1);
                rawState[key] = value;

                switch (key)
                {
                    case "pitch": float.TryParse(value, out pitch); break;
                    case "roll": float.TryParse(value, out roll); break;
                    case "yaw": float.TryParse(value, out yaw); break;
                    case "vgx": float.TryParse(value, out velocityX); break;
                    case "vgy": float.TryParse(value, out velocityY); break;
                    case "vgz": float.TryParse(value, out velocityZ); break;
                    case "agx": float.TryParse(value, out accelerationX); break;
                    case "agy": float.TryParse(value, out accelerationY); break;
                    case "agz": float.TryParse(value, out accelerationZ); break;
                    case "h": float.TryParse(value, out height); break;
                    case "baro": float.TryParse(value, out barometricAltitude); break;
                    case "tof": float.TryParse(value, out timeOfFlightDistance); break;
                    case "templ": float.TryParse(value, out temperatureLow); break;
                    case "temph": float.TryParse(value, out temperatureHigh); break;
                    case "bat":
                        if (int.TryParse(value, out int newBattery))
                        {
                            UpdateBatteryDrainEstimate(newBattery);
                            battery = newBattery;
                        }
                        break;
                    case "time": float.TryParse(value, out flightTime); break;
                }
            }

            // Command ACKs over UDP can be lost even when the Tello executed the
            // command fine (e.g. takeoff succeeds, the "ok" reply gets dropped on
            // the way back) - if we only trusted that ACK, IsFlying would get
            // stuck wrong and a takeoff/land toggle button would keep sending the
            // wrong command forever. Telemetry height is a second, independent
            // signal that self-corrects every ~100ms regardless of what happened
            // to any single command's response.
            if (height > FlyingHeightThresholdCm) IsFlying = true;
            else if (height <= 0f) IsFlying = false;
        }

        // =================================================================
        // DEAD RECKONING (approximate return-to-home vector, no GPS on the Tello)
        // =================================================================
        private void UpdateDeadReckoning()
        {
            if (!enableDeadReckoning || !IsFlying)
            {
                lastReckoningTime = Time.time;
                return;
            }

            float dt = Time.time - lastReckoningTime;
            lastReckoningTime = Time.time;
            if (dt <= 0f || dt > 0.5f) return; // skip degenerate/huge gaps (e.g. right after a stall)

            // vgx/vgy are body-frame speeds (cm/s). Rotate into the drone's start-heading
            // frame using yaw so the accumulated vector stays meaningful over the flight.
            float yawRad = yaw * Mathf.Deg2Rad;
            float worldVx = velocityX * Mathf.Cos(yawRad) - velocityY * Mathf.Sin(yawRad);
            float worldVy = velocityX * Mathf.Sin(yawRad) + velocityY * Mathf.Cos(yawRad);

            estimatedPositionCm += new Vector2(worldVx, worldVy) * dt;
        }

        // =================================================================
        // CRASH DETECTION
        // =================================================================
        private void CheckCrashDetection()
        {
            if (!enableCrashDetection || !IsFlying) return;

            if (AccelerationMagnitude >= crashAccelerationThreshold)
            {
                if (!crashLatched)
                {
                    crashLatched = true;
                    Debug.LogWarning($"[TelloConnection] Crash suspected (accel magnitude {AccelerationMagnitude:F0}).");
                    OnWarningTriggered?.Invoke("Crash suspected: hard impact detected");
                    OnCrashSuspected?.Invoke();
                    if (autoLandOnCrashSuspected) Land();
                }
            }
            else
            {
                crashLatched = false;
            }
        }

        // =================================================================
        // HEALTH WARNINGS
        // All of these fire ONCE per episode (edge-triggered with hysteresis),
        // not every time CheckHealthWarnings() runs (which is once per incoming
        // telemetry packet, ~10x/second) - the previous version fired every
        // single call for as long as a threshold stayed crossed, which is what
        // produced a constant stream of near-duplicate warnings.
        // =================================================================
        private void CheckHealthWarnings()
        {
            if (IsBatteryCritical)
            {
                if (!batteryCriticalAlertActive)
                {
                    batteryCriticalAlertActive = true;
                    OnWarningTriggered?.Invoke("Battery's critical — I need to land, now.");
                }

                if (autoLandOnCriticalBattery && IsFlying && !autoLandTriggered)
                {
                    autoLandTriggered = true;
                    Debug.LogWarning("[TelloConnection] Critical battery: triggering automatic landing.");
                    Land();
                }
            }
            else
            {
                batteryCriticalAlertActive = false;

                if (IsBatteryLow)
                {
                    if (!batteryLowAlertActive)
                    {
                        batteryLowAlertActive = true;
                        OnWarningTriggered?.Invoke($"Battery's getting low ({battery}%) — might want to head back soon.");
                    }
                }
                else
                {
                    batteryLowAlertActive = false;
                }
            }

            CheckTemperatureAlert();
            CheckDescentAlert();
            CheckWindAlert();
        }

        /// <summary>Fires once when the temperature is either climbing fast (a short-window
        /// delta past the threshold) or already close to the critical line, whichever
        /// comes first - rearms only once it's clearly cooled back down, so a value
        /// hovering right at the edge doesn't retrigger on every small wobble.</summary>
        private void CheckTemperatureAlert()
        {
            if (Time.time - lastTemperatureSampleTime < 2f) return;

            float delta = temperatureHigh - temperatureAtLastSample;
            lastTemperatureSampleTime = Time.time;
            temperatureAtLastSample = temperatureHigh;

            bool risingFast = delta > temperatureFastRiseThresholdC;
            bool nearCritical = temperatureHigh >= temperatureCriticalThreshold - temperatureNearCriticalMarginC;

            if ((risingFast || nearCritical) && !temperatureAlertActive)
            {
                temperatureAlertActive = true;
                OnWarningTriggered?.Invoke("Getting warm up here, keep an eye on me.");
            }
            else if (!nearCritical && temperatureHigh < temperatureCriticalThreshold - temperatureNearCriticalMarginC - 5f)
            {
                temperatureAlertActive = false; // clearly back to normal - allow it to fire again next time
            }
        }

        /// <summary>Fires once when height drops faster than a normal controlled descent,
        /// but only while actually flying and not within a few seconds of a voluntary
        /// Land() - so a real landing never gets flagged as an abnormal fall.</summary>
        private void CheckDescentAlert()
        {
            if (Time.time - lastHeightSampleTime < 0.5f) return;

            float dt = Time.time - lastHeightSampleTime;
            float descentRateCmPerSec = (heightAtLastSample - height) / dt; // positive = descending
            lastHeightSampleTime = Time.time;
            heightAtLastSample = height;

            bool descendingFast = descentRateCmPerSec > fastDescentThresholdCmPerSec;
            bool recentVoluntaryLand = Time.time - lastLandCommandTime < 3f;

            if (descendingFast && IsFlying && !recentVoluntaryLand)
            {
                if (!fastDescentAlertActive)
                {
                    fastDescentAlertActive = true;
                    OnWarningTriggered?.Invoke("Descending fast \u2014 that wasn't me.");
                }
            }
            else
            {
                fastDescentAlertActive = false; // rearms once the fast-descent episode ends
            }
        }

        /// <summary>Fires once when the drone is moving significantly despite near-neutral
        /// stick input - the Tello has no real wind sensor, so this is an indirect
        /// estimate (commanded rc vs actual velocity), not a calibrated reading.</summary>
        private void CheckWindAlert()
        {
            if (!IsFlying)
            {
                windAlertActive = false;
                return;
            }

            float commandedMagnitude = new Vector2(rcRoll, rcPitch).magnitude;
            float actualSpeed = Mathf.Sqrt(velocityX * velocityX + velocityY * velocityY);

            bool windDetected = commandedMagnitude < windDriftMaxCommandedInput && actualSpeed > windDriftSpeedThresholdCmPerSec;

            if (windDetected && !windAlertActive)
            {
                windAlertActive = true;
                OnWarningTriggered?.Invoke("Something's pushing me around \u2014 might be wind.");
            }
            else if (!windDetected)
            {
                windAlertActive = false;
            }
        }

        // =================================================================
        // FLIGHT LOG (CSV)
        // On Android, saved into the shared Downloads collection via MediaStore
        // (under Download/Tello4Quest2) instead of the app's private storage -
        // MQDH's File Manager only lists a handful of curated "On device"
        // categories (Videos/Images/Recorded metrics/Apps) and doesn't expose
        // arbitrary private app folders, so the log was previously invisible
        // there. "Recorded metrics" specifically is Meta's own Performance
        // Analyzer output folder - not something a third-party app can write
        // into. Download/ is the correct MediaStore collection for a
        // non-media file like a CSV. Falls back to Application.persistentDataPath
        // in the Editor, where MediaStore doesn't exist.
        // =================================================================
        private void OpenFlightLog()
        {
            if (!enableFlightLog) return;

            string fileName = $"flight_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string header = "time,battery,height_cm,baro_m,tof_cm,templ,temph,vgx,vgy,vgz,agx,agy,agz,pitch,roll,yaw,set_speed,est_x_cm,est_y_cm\n";

            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var contentResolver = currentActivity.Call<AndroidJavaObject>("getContentResolver");

                using var contentValues = new AndroidJavaObject("android.content.ContentValues");
                contentValues.Call("put", "_display_name", fileName);
                contentValues.Call("put", "mime_type", "text/csv");
                contentValues.Call("put", "relative_path", "Download/Tello4Quest2");

                using var mediaStoreDownloads = new AndroidJavaClass("android.provider.MediaStore$Downloads");
                AndroidJavaObject collectionUri = mediaStoreDownloads.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");

                AndroidJavaObject itemUri = contentResolver.Call<AndroidJavaObject>("insert", collectionUri, contentValues);
                if (itemUri == null) throw new Exception("MediaStore insert returned null");

                flightLogOutputStream = contentResolver.Call<AndroidJavaObject>("openOutputStream", itemUri);
                WriteRawFlightLogBytes(Encoding.UTF8.GetBytes(header));
                Debug.Log($"[TelloConnection] Flight log: Download/Tello4Quest2/{fileName}");
#else
                string folder = Path.Combine(Application.persistentDataPath, flightLogFolderName);
                Directory.CreateDirectory(folder);
                flightLogFileStream = new FileStream(Path.Combine(folder, fileName), FileMode.Create, FileAccess.Write);
                WriteRawFlightLogBytes(Encoding.UTF8.GetBytes(header));
                Debug.Log($"[TelloConnection] (Editor) Flight log: {Path.Combine(folder, fileName)}");
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloConnection] Could not open flight log: {e.Message}");
#if UNITY_ANDROID && !UNITY_EDITOR
                flightLogOutputStream = null;
#else
                flightLogFileStream = null;
#endif
            }
        }

        private void WriteRawFlightLogBytes(byte[] bytes)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            flightLogOutputStream?.Call("write", bytes);
#else
            flightLogFileStream?.Write(bytes, 0, bytes.Length);
#endif
        }

        private void WriteFlightLogLine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (flightLogOutputStream == null) return;
#else
            if (flightLogFileStream == null) return;
#endif
            try
            {
                string line = string.Join(",", new[]
                {
                    Time.time.ToString("F2", CultureInfo.InvariantCulture),
                    battery.ToString(CultureInfo.InvariantCulture),
                    height.ToString(CultureInfo.InvariantCulture),
                    barometricAltitude.ToString(CultureInfo.InvariantCulture),
                    timeOfFlightDistance.ToString(CultureInfo.InvariantCulture),
                    temperatureLow.ToString(CultureInfo.InvariantCulture),
                    temperatureHigh.ToString(CultureInfo.InvariantCulture),
                    velocityX.ToString(CultureInfo.InvariantCulture),
                    velocityY.ToString(CultureInfo.InvariantCulture),
                    velocityZ.ToString(CultureInfo.InvariantCulture),
                    accelerationX.ToString(CultureInfo.InvariantCulture),
                    accelerationY.ToString(CultureInfo.InvariantCulture),
                    accelerationZ.ToString(CultureInfo.InvariantCulture),
                    pitch.ToString(CultureInfo.InvariantCulture),
                    roll.ToString(CultureInfo.InvariantCulture),
                    yaw.ToString(CultureInfo.InvariantCulture),
                    commandedSpeed.ToString(CultureInfo.InvariantCulture),
                    estimatedPositionCm.x.ToString("F1", CultureInfo.InvariantCulture),
                    estimatedPositionCm.y.ToString("F1", CultureInfo.InvariantCulture),
                }) + "\n";
                WriteRawFlightLogBytes(Encoding.UTF8.GetBytes(line));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloConnection] Flight log write failed, disabling: {e.Message}");
                CloseFlightLog();
            }
        }

        private void CloseFlightLog()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { flightLogOutputStream?.Call("flush"); flightLogOutputStream?.Call("close"); }
            catch (Exception e) { Debug.LogWarning($"[TelloConnection] Error closing flight log stream: {e.Message}"); }
            flightLogOutputStream?.Dispose();
            flightLogOutputStream = null;
#else
            try { flightLogFileStream?.Flush(); flightLogFileStream?.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[TelloConnection] Error closing flight log file: {e.Message}"); }
            flightLogFileStream = null;
#endif
        }

        // =================================================================
        // UTILITIES
        // =================================================================
        private string FormatTime(float seconds)
        {
            int total = (int)seconds;
            int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
            return h > 0 ? $"{h:00}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        }

        public string GetTelemetryDebugString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"State: {CurrentConnectionState} | Signal lost: {IsSignalLost} | SDK: {SdkVersion}");
            sb.AppendLine($"Battery: {Battery}% | Height: {HeightM:F2}m | Flight time: {FlightTimeFormatted}");
            sb.AppendLine($"Attitude P:{Pitch:F1} R:{Roll:F1} Y:{Yaw:F1}");
            sb.AppendLine($"Speed: {VelocityMagnitude:F1} cm/s | Set speed: {CommandedSpeed} | Accel: {AccelerationMagnitude:F0}");
            sb.AppendLine($"Est. distance from home: {DistanceFromHomeCm:F0}cm, bearing {BearingToHomeDegrees:F0}°");
            sb.AppendLine($"Last command: {LastCommandSent} -> {LastCommandResponse} ({LastCommandLatencyMs:F0}ms)");
            return sb.ToString();
        }
    }
}
