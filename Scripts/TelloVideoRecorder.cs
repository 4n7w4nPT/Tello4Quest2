using System;
using System.IO;
using UnityEngine;

namespace TelloQuest
{
    /// <summary>
    /// Records the Tello's video feed to a real, standard .mp4 file, playable
    /// directly from the headset's own Files app / Quest gallery - no external
    /// tool (ffmpeg) needed to make it watchable, unlike the raw .h264
    /// elementary stream this used to write.
    ///
    /// Uses Android's MediaMuxer to wrap the exact same H.264 access units
    /// TelloVideoReceiver already reassembles - zero re-encoding, same as
    /// before, just repackaged into a proper container as it's written. The
    /// SPS/PPS codec-config data MediaMuxer needs up front comes from
    /// TelloVideoDecoder.CapturedSps/CapturedPps - the REAL bytes captured live
    /// from the stream (see that class), not hardcoded values. Recording can't
    /// start until both have been captured, which in practice has always
    /// already happened by the time a pilot presses the record button (video
    /// has to already be decoding for there to be anything worth recording).
    ///
    /// On Android, saved into the shared Movies collection via MediaStore
    /// (under Movies/Tello4Quest2), same as before - MediaMuxer needs a
    /// FileDescriptor rather than an OutputStream, so the MediaStore hookup is
    /// slightly different from the plain-file-write approach used for photos/
    /// flight logs, but the destination (shared storage, no special manifest
    /// permission needed) is the same idea.
    ///
    /// Falls back to a raw .h264 write to Application.persistentDataPath in
    /// the Editor, where MediaMuxer/MediaStore don't exist.
    /// </summary>
    public class TelloVideoRecorder : MonoBehaviour
    {
        [SerializeField] private TelloVideoReceiver videoReceiver;
        [Tooltip("Where the SPS/PPS codec-config bytes come from - must be the same TelloVideoDecoder actually decoding this stream.")]
        [SerializeField] private TelloVideoDecoder videoDecoder;
        [Tooltip("Must match the Tello's actual encoder resolution - confirmed via decoded SPS analysis (Profile Main, Level 4.0). Only affects the file's declared dimensions, not decoding correctness, so a mismatch here would show as a stretched video rather than a broken one.")]
        // CS0414 disabled for these two: they ARE used, but only inside the
        // Android-only MediaMuxer branch below (#if UNITY_ANDROID && !UNITY_EDITOR).
        // The Editor's own compile pass always has UNITY_EDITOR defined - regardless
        // of which platform is set active - so from its point of view these two
        // fields are never read, hence the (false-positive) warning. Guarding the
        // field declarations themselves with the same #if would silence it, but
        // would also hide them from the Inspector entirely (Inspector always runs
        // in-Editor), which is worse than a suppressed warning.
#pragma warning disable 0414
        [SerializeField] private int videoWidthPx = 960;
        [SerializeField] private int videoHeightPx = 720;
#pragma warning restore 0414

#if !UNITY_ANDROID || UNITY_EDITOR
        [SerializeField] private string videoSaveFolderName = "TelloRecordings"; // Editor-only fallback folder name
        private FileStream fileStream; // Editor fallback only
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject androidMuxer;
        private AndroidJavaObject androidParcelFileDescriptor;
        private int androidVideoTrackIndex = -1;
        private bool androidMuxerStarted;
        private float recordingStartRealtime;
        private long lastPresentationTimeUs = -1;
        private int androidKeyFrameFlag = -1; // MediaCodec.BUFFER_FLAG_KEY_FRAME, fetched once and cached

        // Objets JNI mis en cache. AVANT, HandleFrameReady creait a CHAQUE frame un
        // AndroidJavaClass("java.nio.ByteBuffer") et un AndroidJavaObject BufferInfo,
        // et ByteBuffer.wrap() recopiait tout le tableau managé côté Java. A 30 fps
        // c'etait une tempete d'allocations JNI qui bloquait le main thread - donc
        // faisait deborder le buffer UDP - donc degradait la video EN DIRECT pendant
        // l'enregistrement. Tout ce qui peut etre reutilise l'est desormais.
        private AndroidJavaClass androidByteBufferClass;
        private AndroidJavaObject androidBufferInfo;

        // MediaMuxer refuse un fichier qui ne commence pas par une image cle : sans
        // ce garde-fou, la premiere seconde du .mp4 etait une bouillie sur la plupart
        // des lecteurs, parce qu'on ecrivait la premiere AU qui passait (souvent une
        // P-frame).
        private bool androidWaitingForKeyFrame;
        private long androidFramesSkippedBeforeKeyFrame;
#endif

        public bool IsRecording { get; private set; }
        public string CurrentFilePath { get; private set; }

        /// <summary>Raised on the main thread when recording starts (true) or stops (false).</summary>
        public event Action<bool> OnRecordingStateChanged;

        private void Awake()
        {
            if (videoReceiver == null) videoReceiver = GetComponent<TelloVideoReceiver>();
            if (videoDecoder == null) videoDecoder = GetComponent<TelloVideoDecoder>();

#if !UNITY_ANDROID || UNITY_EDITOR
            // Create the folder up front (not lazily on first recording) so it's
            // there to find as soon as the app starts. Only relevant for the Editor
            // fallback path - MediaStore handles this on Android.
            try { Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, videoSaveFolderName)); }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoRecorder] Could not pre-create recordings folder: {e.Message}"); }
#endif
        }

        private void OnEnable()
        {
            if (videoReceiver != null) videoReceiver.OnFrameReady += HandleFrameReady;
        }

        private void OnDisable()
        {
            if (videoReceiver != null) videoReceiver.OnFrameReady -= HandleFrameReady;
            StopRecording();
        }

        public void ToggleRecording()
        {
            if (IsRecording) StopRecording();
            else StartRecording();
        }

        public void StartRecording()
        {
            if (IsRecording) return;

            string fileName = $"tello_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";

            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (videoDecoder == null || videoDecoder.CapturedSps == null || videoDecoder.CapturedPps == null)
                {
                    Debug.LogWarning("[TelloVideoRecorder] Can't start recording yet - SPS/PPS haven't been captured from the live stream. This should already have happened by the time video is visibly decoding; if you're seeing this, something is off with the decode pipeline, not the recorder.");
                    return;
                }

                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var contentResolver = currentActivity.Call<AndroidJavaObject>("getContentResolver");

                using var contentValues = new AndroidJavaObject("android.content.ContentValues");
                contentValues.Call("put", "_display_name", fileName);
                contentValues.Call("put", "mime_type", "video/mp4");
                contentValues.Call("put", "relative_path", "Movies/Tello4Quest2");

                using var mediaStoreVideo = new AndroidJavaClass("android.provider.MediaStore$Video$Media");
                AndroidJavaObject collectionUri = mediaStoreVideo.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");

                AndroidJavaObject itemUri = contentResolver.Call<AndroidJavaObject>("insert", collectionUri, contentValues);
                if (itemUri == null) throw new Exception("MediaStore insert returned null");

                // MediaMuxer needs a FileDescriptor, not an OutputStream (unlike the
                // photo/flight-log paths) - "w" here is the standard Android
                // ParcelFileDescriptor mode string for write-only.
                androidParcelFileDescriptor = contentResolver.Call<AndroidJavaObject>("openFileDescriptor", itemUri, "w");
                AndroidJavaObject fileDescriptor = androidParcelFileDescriptor.Call<AndroidJavaObject>("getFileDescriptor");

                using var muxerOutputFormatClass = new AndroidJavaClass("android.media.MediaMuxer$OutputFormat");
                int mp4Format = muxerOutputFormatClass.GetStatic<int>("MUXER_OUTPUT_MPEG_4");
                androidMuxer = new AndroidJavaObject("android.media.MediaMuxer", fileDescriptor, mp4Format);

                // La resolution vient maintenant du SPS reellement decode, et ne
                // retombe sur les champs de l'Inspector que si le parsing a echoue.
                // Le Tello EDU en "setresolution high" sort du 1280x720, donc coder
                // 960x720 en dur produisait un fichier aux dimensions fausses.
                int widthPx = (videoDecoder != null && videoDecoder.VideoWidth > 0) ? videoDecoder.VideoWidth : videoWidthPx;
                int heightPx = (videoDecoder != null && videoDecoder.VideoHeight > 0) ? videoDecoder.VideoHeight : videoHeightPx;

                using var mediaFormatClass = new AndroidJavaClass("android.media.MediaFormat");
                using var videoFormat = mediaFormatClass.CallStatic<AndroidJavaObject>("createVideoFormat", "video/avc", widthPx, heightPx);

                using var byteBufferClass = new AndroidJavaClass("java.nio.ByteBuffer");
                using var spsBuffer = byteBufferClass.CallStatic<AndroidJavaObject>("wrap", NormalizeStartCode(videoDecoder.CapturedSps));
                using var ppsBuffer = byteBufferClass.CallStatic<AndroidJavaObject>("wrap", NormalizeStartCode(videoDecoder.CapturedPps));
                videoFormat.Call("setByteBuffer", "csd-0", spsBuffer);
                videoFormat.Call("setByteBuffer", "csd-1", ppsBuffer);

                androidVideoTrackIndex = androidMuxer.Call<int>("addTrack", videoFormat);
                androidMuxer.Call("start");
                androidMuxerStarted = true;

                using var mediaCodecClass = new AndroidJavaClass("android.media.MediaCodec");
                androidKeyFrameFlag = mediaCodecClass.GetStatic<int>("BUFFER_FLAG_KEY_FRAME");

                // Crees une seule fois par enregistrement, reutilises a chaque frame.
                androidByteBufferClass = new AndroidJavaClass("java.nio.ByteBuffer");
                androidBufferInfo = new AndroidJavaObject("android.media.MediaCodec$BufferInfo");

                androidWaitingForKeyFrame = true;
                androidFramesSkippedBeforeKeyFrame = 0;

                recordingStartRealtime = Time.realtimeSinceStartup;
                lastPresentationTimeUs = -1;

                CurrentFilePath = $"Movies/Tello4Quest2/{fileName}";
                Debug.Log($"[TelloVideoRecorder] Recording started (MediaMuxer -> mp4): {CurrentFilePath}");
#else
                string folder = Path.Combine(Application.persistentDataPath, videoSaveFolderName);
                Directory.CreateDirectory(folder);
                string editorFileName = $"tello_{DateTime.Now:yyyyMMdd_HHmmss}.h264";
                CurrentFilePath = Path.Combine(folder, editorFileName);
                fileStream = new FileStream(CurrentFilePath, FileMode.Create, FileAccess.Write);
                Debug.Log($"[TelloVideoRecorder] (Editor) Recording started (raw .h264, no MediaMuxer here): {CurrentFilePath}");
#endif
                IsRecording = true;
                OnRecordingStateChanged?.Invoke(true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoRecorder] Could not start recording: {e.Message}");
#if UNITY_ANDROID && !UNITY_EDITOR
                CleanUpAndroidMuxer();
#else
                fileStream = null;
#endif
                IsRecording = false;
            }
        }

        public void StopRecording()
        {
            if (!IsRecording) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (androidMuxerStarted) androidMuxer?.Call("stop");
            }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoRecorder] Error stopping MediaMuxer (file may still be usable): {e.Message}"); }
            CleanUpAndroidMuxer();
#else
            try { fileStream?.Flush(); fileStream?.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoRecorder] Error closing recording file: {e.Message}"); }
            fileStream = null;
#endif

            IsRecording = false;
            Debug.Log($"[TelloVideoRecorder] Recording stopped: {CurrentFilePath}");
            OnRecordingStateChanged?.Invoke(false);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void CleanUpAndroidMuxer()
        {
            try { androidMuxer?.Call("release"); } catch { /* already released or never fully started - fine either way */ }
            androidMuxer?.Dispose();
            androidMuxer = null;

            try { androidParcelFileDescriptor?.Call("close"); } catch { /* ignore */ }
            androidParcelFileDescriptor?.Dispose();
            androidParcelFileDescriptor = null;

            androidByteBufferClass?.Dispose();
            androidByteBufferClass = null;
            androidBufferInfo?.Dispose();
            androidBufferInfo = null;

            androidVideoTrackIndex = -1;
            androidMuxerStarted = false;
            androidWaitingForKeyFrame = false;
        }

        /// <summary>MediaMuxer's docs require csd-0/csd-1 to start with the 4-byte
        /// Annex-B start code specifically (\x00\x00\x00\x01) - our captured NAL
        /// might only have the 3-byte variant depending on how it appeared in the
        /// stream, so pad it out to be safe rather than assume.</summary>
        private static byte[] NormalizeStartCode(byte[] nal)
        {
            if (nal.Length >= 4 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) return nal;
            if (nal.Length >= 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1)
            {
                byte[] padded = new byte[nal.Length + 1];
                padded[0] = 0;
                Array.Copy(nal, 0, padded, 1, nal.Length);
                return padded;
            }
            return nal; // unexpected shape - pass through rather than guess further
        }
#endif

        private void HandleFrameReady(byte[] annexBFrame)
        {
            if (!IsRecording) return;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!androidMuxerStarted || androidVideoTrackIndex < 0) return;

                bool isKeyFrame = ContainsNalType(annexBFrame, 5); // IDR

                // On n'ecrit rien avant la premiere image cle : un mp4 qui commence
                // sur une P-frame est illisible au debut.
                if (androidWaitingForKeyFrame)
                {
                    if (!isKeyFrame)
                    {
                        androidFramesSkippedBeforeKeyFrame++;
                        return;
                    }
                    androidWaitingForKeyFrame = false;
                    // La base de temps demarre a la premiere image reellement ecrite,
                    // pour que la piste commence a pts = 0 et non avec un decalage.
                    recordingStartRealtime = Time.realtimeSinceStartup;
                    lastPresentationTimeUs = -1;
                    if (androidFramesSkippedBeforeKeyFrame > 0)
                        Debug.Log($"[TelloVideoRecorder] Skipped {androidFramesSkippedBeforeKeyFrame} access unit(s) waiting for the first keyframe.");
                }

                long ptsUs = (long)((Time.realtimeSinceStartup - recordingStartRealtime) * 1_000_000.0);
                if (ptsUs <= lastPresentationTimeUs) ptsUs = lastPresentationTimeUs + 1; // MediaMuxer requires strictly increasing timestamps
                lastPresentationTimeUs = ptsUs;

                using var sampleBuffer = androidByteBufferClass.CallStatic<AndroidJavaObject>("wrap", annexBFrame);

                androidBufferInfo.Set("offset", 0);
                androidBufferInfo.Set("size", annexBFrame.Length);
                androidBufferInfo.Set("presentationTimeUs", ptsUs);
                androidBufferInfo.Set("flags", isKeyFrame ? androidKeyFrameFlag : 0);

                androidMuxer.Call("writeSampleData", androidVideoTrackIndex, sampleBuffer, androidBufferInfo);
#else
                if (fileStream == null) return;
                fileStream.Write(annexBFrame, 0, annexBFrame.Length);
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoRecorder] Write failed, stopping recording: {e.Message}");
                StopRecording();
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
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
#endif
    }
}
