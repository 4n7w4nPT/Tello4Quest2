using System;
using System.IO;
using UnityEngine;

namespace TelloQuest
{
    /// <summary>
    /// Records the Tello's video feed to disk as a raw H.264 elementary stream
    /// (.h264 file) - the exact compressed access units TelloVideoReceiver already
    /// reassembles from the network, written straight to disk with zero
    /// re-encoding. Cheap (just a file write, no extra CPU/battery cost) and
    /// lossless relative to what the Tello actually sent - re-encoding the
    /// already-decoded RGBA frames would cost far more and lose quality for no
    /// benefit here.
    ///
    /// The resulting file is a raw Annex-B elementary stream, not an .mp4
    /// container - VLC and ffplay open it directly. To get a standard .mp4
    /// afterward: ffmpeg -i recording.h264 -c copy recording.mp4
    ///
    /// On Android, saved into the shared Movies collection via MediaStore (under
    /// Movies/Tello4Quest2) rather than the app's private folder - visible from
    /// the headset's own Files app, MQDH, and USB transfer, matching the photo
    /// save path. Streamed incrementally into an Android OutputStream as frames
    /// arrive, the same way it was streamed into a FileStream before - no change
    /// in write pattern, just where the bytes end up. Falls back to
    /// Application.persistentDataPath in the Editor, where MediaStore doesn't exist.
    /// </summary>
    public class TelloVideoRecorder : MonoBehaviour
    {
        [SerializeField] private TelloVideoReceiver videoReceiver;
        [SerializeField] private string videoSaveFolderName = "TelloRecordings"; // Editor-only fallback folder name

        private FileStream fileStream; // Editor fallback only
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject androidOutputStream;
#endif

        public bool IsRecording { get; private set; }
        public string CurrentFilePath { get; private set; }

        /// <summary>Raised on the main thread when recording starts (true) or stops (false).</summary>
        public event Action<bool> OnRecordingStateChanged;

        private void Awake()
        {
            if (videoReceiver == null) videoReceiver = GetComponent<TelloVideoReceiver>();

#if !UNITY_ANDROID || UNITY_EDITOR
            // Create the folder up front (not lazily on first recording) so it's
            // there to find via adb/MQDH as soon as the app starts. Only relevant
            // for the Editor fallback path - MediaStore handles this on Android.
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

            string fileName = $"tello_{DateTime.Now:yyyyMMdd_HHmmss}.h264";

            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var contentResolver = currentActivity.Call<AndroidJavaObject>("getContentResolver");

                using var contentValues = new AndroidJavaObject("android.content.ContentValues");
                contentValues.Call("put", "_display_name", fileName);
                contentValues.Call("put", "mime_type", "video/avc"); // raw H.264/AVC elementary stream, not a playable container
                contentValues.Call("put", "relative_path", "Movies/Tello4Quest2");

                using var mediaStoreVideo = new AndroidJavaClass("android.provider.MediaStore$Video$Media");
                AndroidJavaObject collectionUri = mediaStoreVideo.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");

                AndroidJavaObject itemUri = contentResolver.Call<AndroidJavaObject>("insert", collectionUri, contentValues);
                if (itemUri == null) throw new Exception("MediaStore insert returned null");

                androidOutputStream = contentResolver.Call<AndroidJavaObject>("openOutputStream", itemUri);
                CurrentFilePath = $"Movies/Tello4Quest2/{fileName}";
#else
                string folder = Path.Combine(Application.persistentDataPath, videoSaveFolderName);
                Directory.CreateDirectory(folder);
                CurrentFilePath = Path.Combine(folder, fileName);
                fileStream = new FileStream(CurrentFilePath, FileMode.Create, FileAccess.Write);
#endif
                IsRecording = true;
                Debug.Log($"[TelloVideoRecorder] Recording started: {CurrentFilePath}");
                OnRecordingStateChanged?.Invoke(true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoRecorder] Could not start recording: {e.Message}");
                fileStream = null;
                IsRecording = false;
            }
        }

        public void StopRecording()
        {
            if (!IsRecording) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                androidOutputStream?.Call("flush");
                androidOutputStream?.Call("close");
            }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoRecorder] Error closing recording stream: {e.Message}"); }
            androidOutputStream?.Dispose();
            androidOutputStream = null;
#else
            try { fileStream?.Flush(); fileStream?.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoRecorder] Error closing recording file: {e.Message}"); }
            fileStream = null;
#endif

            IsRecording = false;
            Debug.Log($"[TelloVideoRecorder] Recording stopped: {CurrentFilePath}");
            OnRecordingStateChanged?.Invoke(false);
        }

        private void HandleFrameReady(byte[] annexBFrame)
        {
            if (!IsRecording) return;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (androidOutputStream == null) return;
                androidOutputStream.Call("write", annexBFrame);
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
    }
}
