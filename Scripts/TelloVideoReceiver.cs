using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace TelloQuest
{
    /// <summary>
    /// Receives the Tello's raw H.264 video stream: UDP port 11111, no RTP, no
    /// container - just H.264 access units (Annex-B, start-code delimited NALs)
    /// sent back to back.
    ///
    /// Frame boundary quirk (confirmed by packet captures from the Tello
    /// community, not documented in the official SDK PDF): every UDP datagram
    /// that is part of a video access unit is exactly 1460 bytes, EXCEPT the
    /// last datagram of that access unit, which is shorter. So the reliable way
    /// to know "this is a complete, decodable frame" is simply: keep
    /// concatenating packets until one arrives that is NOT 1460 bytes long.
    /// No NAL start-code scanning is needed for framing.
    /// </summary>
    public class TelloVideoReceiver : MonoBehaviour
    {
        [Header("=== NETWORK ===")]
        [SerializeField] private TelloConnection tello;
        [SerializeField] private int videoPort = 11111;
        [Tooltip("The Tello's fixed UDP payload size for every packet but the last one of a frame.")]
        [SerializeField] private int telloPacketSize = 1460;

        [Header("=== SAFETY / LATENCY ===")]
        [Tooltip("Safety cap: if a frame grows past this without a short (end-of-frame) packet - e.g. that packet was lost - discard it instead of growing forever or feeding garbage to the decoder.")]
        [SerializeField] private int maxFrameSizeBytes = 2 * 1024 * 1024;
        [Tooltip("Max complete frames buffered before we start dropping the oldest. Keeps latency low instead of building a backlog if decoding can't keep up for a moment.")]
        [SerializeField] private int maxQueuedFrames = 3;

        private UdpClient client;
        private Thread receiveThread;
        private volatile bool isRunning;

        // Background-thread-only state (never touched from Update)
        private byte[] frameBuffer = new byte[262144]; // 256KB, grows if needed
        private int frameLength;

        private readonly ConcurrentQueue<byte[]> completedFrames = new ConcurrentQueue<byte[]>();

        public int QueuedFrameCount => completedFrames.Count;
        public long FramesReceivedTotal { get; private set; }
        public long FramesDroppedTotal { get; private set; }
        public float LastFrameReceivedTime { get; private set; }

        /// <summary>Raised on the MAIN thread (from Update), one full Annex-B access unit per call.</summary>
        public event Action<byte[]> OnFrameReady;

        private void Start()
        {
            if (tello == null) tello = TelloConnection.Instance;
            OpenSocket();
        }

        private void OnDestroy() => CloseSocket();

        private void OpenSocket()
        {
            try
            {
                client = new UdpClient(videoPort);
                isRunning = true;
                receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                receiveThread.Start();
                Debug.Log($"[TelloVideoReceiver] Listening for video on UDP :{videoPort}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoReceiver] Could not open video socket: {e.Message}");
            }
        }

        private void CloseSocket()
        {
            isRunning = false;
            try { receiveThread?.Join(500); } catch { /* expected if already stopped */ }
            client?.Close();
            client = null;
        }

        private void ReceiveLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, videoPort);
            while (isRunning)
            {
                try
                {
                    byte[] packet = client.Receive(ref remote);
                    AppendPacket(packet);
                }
                catch (SocketException)
                {
                    // expected when the socket closes (component disabled/destroyed)
                }
                catch (Exception e)
                {
                    if (isRunning) Debug.LogWarning($"[TelloVideoReceiver] Receive error: {e.Message}");
                }
            }
        }

        // Runs on the background thread - keep allocations minimal here.
        private void AppendPacket(byte[] packet)
        {
            System.Threading.Interlocked.Increment(ref diagnosticPacketCount); // TEMPORARY DIAGNOSTIC - see Update()

            int newLength = frameLength + packet.Length;
            if (newLength > maxFrameSizeBytes)
            {
                frameLength = 0;
                FramesDroppedTotal++;
                return;
            }
            if (newLength > frameBuffer.Length)
            {
                int newCapacity = Mathf.NextPowerOfTwo(newLength);
                Array.Resize(ref frameBuffer, newCapacity);
            }
            Buffer.BlockCopy(packet, 0, frameBuffer, frameLength, packet.Length);
            frameLength = newLength;

            if (packet.Length < telloPacketSize)
            {
                // Short packet = last packet of this access unit. Frame complete.
                byte[] frame = new byte[frameLength];
                Buffer.BlockCopy(frameBuffer, 0, frame, 0, frameLength);
                frameLength = 0;

                if (completedFrames.Count >= maxQueuedFrames)
                {
                    completedFrames.TryDequeue(out _); // drop the oldest: prioritize freshness over completeness
                    FramesDroppedTotal++;
                }
                completedFrames.Enqueue(frame);
                FramesReceivedTotal++;
            }
        }

        // =================================================================
        // TEMPORARY DIAGNOSTIC LOGGING - remove once video reception is confirmed working.
        // Logs once a second so it's easy to grep in MQDH/logcat without flooding it
        // per-packet. If "packets" never leaves 0, no UDP video data is reaching this
        // socket at all (network/routing issue). If "packets" grows but "frames" stays
        // at 0, packets arrive but never form a complete access unit (framing issue).
        // =================================================================
        private long diagnosticPacketCount;
        private float diagnosticLogTimer;

        private void Update()
        {
            while (completedFrames.TryDequeue(out byte[] frame))
            {
                LastFrameReceivedTime = Time.time;
                OnFrameReady?.Invoke(frame);
            }

            diagnosticLogTimer += Time.deltaTime;
            if (diagnosticLogTimer >= 1f)
            {
                diagnosticLogTimer = 0f;
                Debug.Log($"[TelloVideoReceiver][DIAG] packets={diagnosticPacketCount} framesReceived={FramesReceivedTotal} framesDropped={FramesDroppedTotal} queued={QueuedFrameCount}");
            }
        }
    }
}
