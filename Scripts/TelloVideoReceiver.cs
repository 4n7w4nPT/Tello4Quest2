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
    /// container - just Annex-B access units sent back to back.
    ///
    /// Frame boundary quirk (confirme par captures reseau de la communaute Tello,
    /// non documente dans le SDK) : chaque datagramme d'une access unit fait
    /// exactement 1460 octets, SAUF le dernier. On concatene donc jusqu'a
    /// recevoir un paquet plus court.
    ///
    /// ------------------------------------------------------------------
    /// CE QUI A CHANGE :
    ///
    /// 1. TAILLE DU BUFFER DE RECEPTION. Le buffer OS par defaut (~200 Ko) deborde
    ///    des qu'un hitch du main thread depasse ~30 ms a 3 Mbit/s. Chaque paquet
    ///    perdu = une access unit incomplete = des macroblocs. On monte a 4 Mo.
    ///    C'est le meilleur ratio effort/artefacts du fichier.
    ///
    /// 2. VALIDATION DES ACCESS UNITS. On poussait au decodeur tout ce qui etait
    ///    reassemble, y compris des AU corrompues par une perte de paquet. Une AU
    ///    corrompue coute plusieurs frames au decodeur pour s'en remettre : la
    ///    jeter coute moins cher que l'afficher.
    ///
    /// 3. FERMETURE. CloseSocket faisait Join(500) AVANT Close(). Le thread etant
    ///    bloque dans Receive(), le Join expirait systematiquement ses 500 ms, et
    ///    seul le Close qui suivait le debloquait -> hitch garanti a la sortie.
    ///    Ordre inverse + ReceiveTimeout pour une sortie propre.
    ///
    /// 4. COMPTEURS THREAD-SAFE. FramesReceivedTotal / FramesDroppedTotal etaient
    ///    incrementes depuis le thread reseau sans Interlocked.
    ///
    /// 5. PAUSE/REPRISE. Au retour de veille du casque, le buffer OS contient un
    ///    backlog de plusieurs secondes : on le purge au lieu de le decoder.
    ///
    /// 6. LOGS. Le [DIAG] permanent passe derriere verboseDiagnostics.
    /// ------------------------------------------------------------------
    /// </summary>
    public class TelloVideoReceiver : MonoBehaviour
    {
        [Header("=== NETWORK ===")]
        [SerializeField] private TelloConnection tello;
        [SerializeField] private int videoPort = 11111;
        [Tooltip("The Tello's fixed UDP payload size for every packet but the last one of a frame.")]
        [SerializeField] private int telloPacketSize = 1460;
        [Tooltip("Taille du buffer de reception UDP. Trop petit = paquets perdus des le moindre hitch = artefacts.")]
        [SerializeField] private int receiveBufferBytes = 4 * 1024 * 1024;

        [Header("=== SAFETY / LATENCY ===")]
        [Tooltip("Safety cap: si une frame depasse cette taille sans paquet court de fin, on la jette.")]
        [SerializeField] private int maxFrameSizeBytes = 2 * 1024 * 1024;
        [Tooltip("Frames completes bufferisees avant de jeter la plus ancienne.")]
        [SerializeField] private int maxQueuedFrames = 3;
        [Tooltip("Jette les access units qui ne commencent pas par un start code Annex-B (signe d'une perte de paquet).")]
        [SerializeField] private bool validateAccessUnits = true;

        [Header("=== DIAGNOSTICS ===")]
        [Tooltip("Log une ligne de compteurs par seconde. A laisser decoche en vol.")]
        [SerializeField] private bool verboseDiagnostics = false;

        private UdpClient client;
        private Thread receiveThread;
        private volatile bool isRunning;

        // Etat exclusif au thread reseau
        private byte[] frameBuffer = new byte[262144];
        private int frameLength;

        private readonly ConcurrentQueue<byte[]> completedFrames = new ConcurrentQueue<byte[]>();

        private long framesReceivedTotal;
        private long framesDroppedTotal;
        private long malformedFramesTotal;
        private long packetCount;

        public int QueuedFrameCount => completedFrames.Count;
        public long FramesReceivedTotal => Interlocked.Read(ref framesReceivedTotal);
        public long FramesDroppedTotal => Interlocked.Read(ref framesDroppedTotal);
        public long MalformedFramesTotal => Interlocked.Read(ref malformedFramesTotal);
        public float LastFrameReceivedTime { get; private set; }

        /// <summary>Raised on the MAIN thread (from Update), one full Annex-B access unit per call.</summary>
        public event Action<byte[]> OnFrameReady;

        private void Start()
        {
            if (tello == null) tello = TelloConnection.Instance;
            OpenSocket();
        }

        private void OnDestroy() => CloseSocket();

        /// <summary>Au retour de veille, le buffer OS a accumule plusieurs secondes
        /// de flux. Les decoder produirait un rattrapage en accelere puis une
        /// latence permanente : on jette et on repart du direct.</summary>
        private void OnApplicationPause(bool isPaused)
        {
            if (isPaused) return;
            while (completedFrames.TryDequeue(out _)) { }
            frameLength = 0;
        }

        private void OpenSocket()
        {
            try
            {
                client = new UdpClient(videoPort);

                // LE reglage qui compte le plus contre les artefacts : sans lui,
                // le moindre pic de charge sur le main thread fait deborder le
                // buffer noyau et perdre des paquets au milieu d'une frame.
                try { client.Client.ReceiveBufferSize = receiveBufferBytes; }
                catch (Exception e) { Debug.LogWarning($"[TelloVideoReceiver] Could not raise UDP receive buffer: {e.Message}"); }

                // Permet a ReceiveLoop de reprendre la main regulierement pour
                // tester isRunning, au lieu de dependre d'une exception au Close.
                client.Client.ReceiveTimeout = 500;

                isRunning = true;
                receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "TelloVideoReceive",
                    Priority = System.Threading.ThreadPriority.AboveNormal
                };
                receiveThread.Start();
                Debug.Log($"[TelloVideoReceiver] Listening for video on UDP :{videoPort} (rx buffer {client.Client.ReceiveBufferSize / 1024} KB)");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoReceiver] Could not open video socket: {e.Message}");
            }
        }

        private void CloseSocket()
        {
            isRunning = false;
            // Close AVANT Join : le thread est bloque dans Receive(), c'est le
            // Close qui le debloque. L'ordre inverse garantissait un hitch.
            try { client?.Close(); } catch { /* deja ferme */ }
            client = null;
            try { receiveThread?.Join(600); } catch { /* attendu si deja arrete */ }
            receiveThread = null;
        }

        private void ReceiveLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, videoPort);
            // Reference locale : CloseSocket() met le champ a null, et le thread
            // pourrait sinon le dereferencer entre deux tours de boucle.
            UdpClient localClient = client;
            while (isRunning && localClient != null)
            {
                try
                {
                    byte[] packet = localClient.Receive(ref remote);
                    AppendPacket(packet);
                }
                catch (SocketException se)
                {
                    // TimedOut est normal : c'est le tour de boucle qui permet de
                    // retester isRunning. Le reste signifie socket ferme.
                    if (se.SocketErrorCode != SocketError.TimedOut && isRunning) return;
                }
                catch (ObjectDisposedException)
                {
                    return; // socket ferme pendant l'attente
                }
                catch (Exception e)
                {
                    if (isRunning) Debug.LogWarning($"[TelloVideoReceiver] Receive error: {e.Message}");
                }
            }
        }

        // Thread reseau - garder les allocations au minimum ici.
        private void AppendPacket(byte[] packet)
        {
            Interlocked.Increment(ref packetCount);

            int newLength = frameLength + packet.Length;
            if (newLength > maxFrameSizeBytes)
            {
                frameLength = 0;
                Interlocked.Increment(ref framesDroppedTotal);
                return;
            }
            if (newLength > frameBuffer.Length)
                Array.Resize(ref frameBuffer, Mathf.NextPowerOfTwo(newLength));

            Buffer.BlockCopy(packet, 0, frameBuffer, frameLength, packet.Length);
            frameLength = newLength;

            if (packet.Length >= telloPacketSize) return;

            // Paquet court = derniere partie de l'access unit. Frame complete.
            int length = frameLength;
            frameLength = 0;

            if (validateAccessUnits && !StartsWithStartCode(frameBuffer, length))
            {
                // Une AU qui ne commence pas par 00 00 01 signifie qu'on a rate le
                // debut (paquet perdu). La pousser ferait travailler le decodeur
                // sur des donnees fausses pendant plusieurs frames.
                Interlocked.Increment(ref malformedFramesTotal);
                Interlocked.Increment(ref framesDroppedTotal);
                return;
            }

            byte[] frame = new byte[length];
            Buffer.BlockCopy(frameBuffer, 0, frame, 0, length);

            if (completedFrames.Count >= maxQueuedFrames)
            {
                completedFrames.TryDequeue(out _); // fraicheur > completude
                Interlocked.Increment(ref framesDroppedTotal);
            }
            completedFrames.Enqueue(frame);
            Interlocked.Increment(ref framesReceivedTotal);
        }

        private static bool StartsWithStartCode(byte[] buffer, int length)
        {
            if (length < 4) return false;
            if (buffer[0] != 0 || buffer[1] != 0) return false;
            if (buffer[2] == 1) return true;                       // 00 00 01
            return buffer[2] == 0 && buffer[3] == 1;               // 00 00 00 01
        }

        private float diagnosticLogTimer;

        private void Update()
        {
            while (completedFrames.TryDequeue(out byte[] frame))
            {
                LastFrameReceivedTime = Time.time;
                OnFrameReady?.Invoke(frame);
            }

            if (!verboseDiagnostics) return;
            diagnosticLogTimer += Time.deltaTime;
            if (diagnosticLogTimer >= 1f)
            {
                diagnosticLogTimer = 0f;
                Debug.Log($"[TelloVideoReceiver][DIAG] packets={Interlocked.Read(ref packetCount)} " +
                          $"received={FramesReceivedTotal} dropped={FramesDroppedTotal} " +
                          $"malformed={MalformedFramesTotal} queued={QueuedFrameCount}");
            }
        }
    }
}
