using System;
using System.Collections.Generic;
using UnityEngine;

namespace TelloQuest
{
    /// <summary>
    /// Wraps PopH264's real decode API (see the installed PopH264.cs -
    /// PopH264.Decoder lives in the global namespace, NOT "Pop.H264" as
    /// originally assumed) to hardware-decode the H.264 access units from
    /// TelloVideoReceiver into a live Texture2D.
    ///
    /// On Android/Quest, PopH264 drives the platform's hardware decoder
    /// (MediaCodec), so this costs very little CPU/battery. Depending on how
    /// that decoder surfaces its output, PopH264 hands back either a single
    /// RGBA/BGRA texture (easy case, handled directly below) or several raw
    /// YUV planes (needs an extra conversion shader - see the warning this
    /// script logs if that happens on your device).
    /// </summary>
    public class TelloVideoDecoder : MonoBehaviour
    {
        [SerializeField] private TelloVideoReceiver videoReceiver;
        [Tooltip("Let PopH264 push frames on its own background thread, decoupled from Unity's Update rate.")]
        [SerializeField] private bool threadedDecoding = true;

        private PopH264.Decoder decoder;
        private int nextFrameNumber;

        private List<Texture2D> planes;
        private List<PopH264.PixelFormat> pixelFormats;

        public Texture2D VideoTexture { get; private set; }
        public bool IsYuvNv12 { get; private set; }
        public Texture2D YPlane { get; private set; }
        public Texture2D UVPlane { get; private set; }
        public bool UvChannelsSwapped { get; private set; }
        public long FramesDecodedTotal { get; private set; }
        public long FramesPushFailedTotal { get; private set; }
        public float LastFrameDecodedTime { get; private set; }

        /// <summary>Raised on the main thread whenever VideoTexture has fresh pixel data.</summary>
        public event Action OnTextureUpdated;

        private bool loggedFormatWarning;

        // =================================================================
        // TEMPORARY DIAGNOSTIC LOGGING - remove once video decode is confirmed working.
        // =================================================================
        private bool loggedFirstPush;
        private long diagnosticGetNextFrameCalls;
        private long diagnosticFrameNumberHits;      // GetNextFrame returned a non-null frame number
        private long diagnosticEmptyPlanesHits;      // ...but planes was null/empty when it did
        private float diagnosticLogTimer;

        private void Awake()
        {
            if (videoReceiver == null) videoReceiver = GetComponent<TelloVideoReceiver>();
        }

        private void OnEnable()
        {
            if (videoReceiver != null) videoReceiver.OnFrameReady += HandleFrameReady;

            try
            {
                decoder = new PopH264.Decoder(null, threadedDecoding); // null = default DecoderParams (best available hardware decoder)
                Debug.Log($"[TelloVideoDecoder][DIAG] Decoder created OK (threadedDecoding={threadedDecoding}).");
            }
            catch (Exception e)
            {
                // Most likely cause on Android: the native libPopH264.so failed to
                // load (e.g. the 16KB page-size alignment warning from the build
                // console). Without this catch, that failure would otherwise be a
                // silent black screen with no clear log line pointing at PopH264.
                Debug.LogError($"[TelloVideoDecoder] Failed to create PopH264 decoder - the native plugin likely failed to load: {e.Message}");
                decoder = null;
            }
        }

        private void OnDisable()
        {
            if (videoReceiver != null) videoReceiver.OnFrameReady -= HandleFrameReady;
            decoder?.Dispose();
            decoder = null;
        }

        private bool sawFirstSps;
        private long diagnosticDiscardedBeforeSps;

        private void HandleFrameReady(byte[] annexBFrame)
        {
            if (decoder == null) return;

            // PopH264 (like any H.264 decoder) can't produce anything from P/B-slices
            // alone - it needs to have seen SPS AND PPS at least once first. If our
            // socket started listening after the Tello's initial SPS/PPS/IDR burst
            // already went by, every access unit we get is just ongoing P-slices:
            // PushFrameData happily accepts them (returns true) but GetNextFrame()
            // will silently never produce a frame, forever. So: drop everything until
            // we've actually seen BOTH a NAL type 7 (SPS) AND type 8 (PPS) in the same
            // access unit, then start feeding from there. Requiring both (not just SPS
            // alone) matters: a diagnostic session showed a case where an SPS-only
            // access unit was found and accepted as the bootstrap point, yet the
            // decoder still never produced a single frame afterward - almost certainly
            // because that SPS wasn't paired with its PPS (a fragmentary/isolated NAL,
            // not the real parameter-set burst), so the decoder still didn't have
            // everything it needed despite the old, looser check being satisfied.
            if (!sawFirstSps)
            {
                if (!ContainsNalType(annexBFrame, 7) || !ContainsNalType(annexBFrame, 8))
                {
                    diagnosticDiscardedBeforeSps++;
                    return;
                }
                sawFirstSps = true;
                Debug.Log($"[TelloVideoDecoder][DIAG] First SPS+PPS pair seen after discarding {diagnosticDiscardedBeforeSps} pre-bootstrap access unit(s) - starting to feed the decoder now.");
            }

            bool ok = decoder.PushFrameData(annexBFrame, nextFrameNumber++);
            if (!ok) FramesPushFailedTotal++;

            // TEMPORARY DIAGNOSTIC: log full detail on the very first push only (avoid
            // flooding the log every frame), including a hex dump of the first bytes so
            // we can confirm this is a valid Annex-B NAL (should start 00 00 00 01 or
            // 00 00 01, followed by a NAL header byte - SPS is type 7, so the byte after
            // the start code is typically 0x67 for the very first NAL of a stream).
            if (!loggedFirstPush)
            {
                loggedFirstPush = true;
                int dumpLen = Mathf.Min(16, annexBFrame.Length);
                string hex = BitConverter.ToString(annexBFrame, 0, dumpLen);
                Debug.Log($"[TelloVideoDecoder][DIAG] First PushFrameData: ok={ok}, length={annexBFrame.Length}, first {dumpLen} bytes = {hex}");
            }
        }

        /// <summary>Scans an Annex-B access unit for a NAL of the given type (7 = SPS, 8 = PPS, 5 = IDR slice, 1 = non-IDR slice).</summary>
        private static bool ContainsNalType(byte[] data, int nalType)
        {
            for (int i = 0; i < data.Length - 3; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                {
                    int nalStart = i + 3;
                    if (nalStart < data.Length && (data[nalStart] & 0x1F) == nalType) return true;
                }
            }
            return false;
        }

        private void Update()
        {
            if (decoder == null) return;

            diagnosticGetNextFrameCalls++;

            int? frameNumber = decoder.GetNextFrame(ref planes, ref pixelFormats);

            if (frameNumber.HasValue)
            {
                diagnosticFrameNumberHits++;
                if (planes == null || planes.Count == 0) diagnosticEmptyPlanesHits++;
            }

            RunDiagnosticLog();

            if (!frameNumber.HasValue || planes == null || planes.Count == 0) return;

            bool isDirectRgba = planes.Count == 1 &&
                (pixelFormats[0] == PopH264.PixelFormat.RGBA || pixelFormats[0] == PopH264.PixelFormat.BGRA);

            bool isNv12Yuv = planes.Count == 2 &&
                pixelFormats[0] == PopH264.PixelFormat.Greyscale &&
                (pixelFormats[1] == PopH264.PixelFormat.ChromaUV_88 || pixelFormats[1] == PopH264.PixelFormat.ChromaVU_88);

            if (isDirectRgba)
            {
                IsYuvNv12 = false;
                VideoTexture = planes[0];
                FramesDecodedTotal++;
                LastFrameDecodedTime = Time.time;
                OnTextureUpdated?.Invoke();

                if (pixelFormats[0] == PopH264.PixelFormat.BGRA && !loggedFormatWarning)
                {
                    loggedFormatWarning = true;
                    Debug.LogWarning("[TelloVideoDecoder] Decoder outputs BGRA - a plain Unlit/Texture material will show red/blue swapped. Swap the R/B channels in TelloVideoDisplay's material shader if you see this.");
                }
                return;
            }

            if (isNv12Yuv)
            {
                IsYuvNv12 = true;
                UvChannelsSwapped = pixelFormats[1] == PopH264.PixelFormat.ChromaVU_88;
                YPlane = planes[0];
                UVPlane = planes[1];
                FramesDecodedTotal++;
                LastFrameDecodedTime = Time.time;
                OnTextureUpdated?.Invoke();
                return;
            }

            if (!loggedFormatWarning)
            {
                loggedFormatWarning = true;
                Debug.LogWarning($"[TelloVideoDecoder] Decoder returned {planes.Count} plane(s), first in {pixelFormats[0]} format - an unhandled combination (not direct RGBA/BGRA, not 2-plane NV12 YUV). TelloVideoDisplay won't know how to show this; extend the format handling above to match what this platform's decoder actually returns.");
            }
        }

        // =================================================================
        // TEMPORARY DIAGNOSTIC LOGGING - remove once video decode is confirmed working.
        // Logs once a second: how many frames were pushed in vs how many times
        // GetNextFrame() actually returned something. If pushed keeps climbing but
        // "gnfHits" stays at 0, PopH264 is receiving valid pushes but never
        // finishing a decode (bad SPS/PPS, unsupported profile, codec init failure,
        // etc. - all invisible from the C# side, hence needing this counter).
        // =================================================================
        private void RunDiagnosticLog()
        {
            diagnosticLogTimer += Time.deltaTime;
            if (diagnosticLogTimer < 1f) return;
            diagnosticLogTimer = 0f;

            Debug.Log($"[TelloVideoDecoder][DIAG] pushed={nextFrameNumber} pushFailed={FramesPushFailedTotal} discardedBeforeSps={diagnosticDiscardedBeforeSps} " +
                      $"updateCalls={diagnosticGetNextFrameCalls} gnfHits={diagnosticFrameNumberHits} " +
                      $"emptyPlanesHits={diagnosticEmptyPlanesHits} decoded={FramesDecodedTotal}");
        }
    }
}
