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
    /// The resulting .h264 file is a raw Annex-B elementary stream, not an .mp4
    /// container - VLC and ffplay open it directly. To get a standard .mp4
    /// afterward: ffmpeg -i recording.h264 -c copy recording.mp4
    /// </summary>
    public class TelloVideoRecorder : MonoBehaviour
    {
        [SerializeField] private TelloVideoReceiver videoReceiver;
        [SerializeField] private string videoSaveFolderName = "TelloRecordings";

        private FileStream fileStream;

        public bool IsRecording { get; private set; }
        public string CurrentFilePath { get; private set; }

        /// <summary>Raised on the main thread when recording starts (true) or stops (false).</summary>
        public event Action<bool> OnRecordingStateChanged;

        private void Awake()
        {
            if (videoReceiver == null) videoReceiver = GetComponent<TelloVideoReceiver>();

            // Create the folder up front (not lazily on first recording) so it's
            // there to find via adb/MQDH as soon as the app starts.
            try { Directory.CreateDirectory(Path.Combine(Application.persistentDataPath, videoSaveFolderName)); }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoRecorder] Could not pre-create recordings folder: {e.Message}"); }
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

            try
            {
                string folder = Path.Combine(Application.persistentDataPath, videoSaveFolderName);
                Directory.CreateDirectory(folder);
                string fileName = $"tello_{DateTime.Now:yyyyMMdd_HHmmss}.h264";
                CurrentFilePath = Path.Combine(folder, fileName);
                fileStream = new FileStream(CurrentFilePath, FileMode.Create, FileAccess.Write);
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

            try { fileStream?.Flush(); fileStream?.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[TelloVideoRecorder] Error closing recording file: {e.Message}"); }

            fileStream = null;
            IsRecording = false;
            Debug.Log($"[TelloVideoRecorder] Recording stopped: {CurrentFilePath}");
            OnRecordingStateChanged?.Invoke(false);
        }

        private void HandleFrameReady(byte[] annexBFrame)
        {
            if (!IsRecording || fileStream == null) return;
            try
            {
                fileStream.Write(annexBFrame, 0, annexBFrame.Length);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoRecorder] Write failed, stopping recording: {e.Message}");
                StopRecording();
            }
        }
    }
}
