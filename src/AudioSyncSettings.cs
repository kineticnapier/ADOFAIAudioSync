using UnityModManagerNet;

namespace Kiner.ADOFAIAudioSync
{
    public sealed class AudioSyncSettings : UnityModManager.ModSettings
    {
        public int SettingsRevision = 0;
        public bool EnableStartGate = true;
        // Legacy v0.9.16 field retained so existing Settings.xml files migrate cleanly.
        public bool ShowOverlay = false;
        // 0 = off, 1 = compact, 2 = detailed diagnostics.
        public int OverlayMode = 0;

        public bool EnableCheckpointStartHandshake = true;
        public int CheckpointStartStableFrames = 2;
        public float CheckpointStartTimeoutMs = 2000.0f;
        public float CheckpointScheduleLeadMs = 600.0f;
        // Kept under its old serialized name. This is the maximum absolute
        // residual between the observed sample and the sample expected from the DSP schedule.
        public float CheckpointMaxInitialAdvanceMs = 50.0f;
        public int CheckpointScheduleRetryCount = 1;
        public bool EnableCheckpointCountdownFold = true;
        public float CheckpointCountdownMaxBpm = 240.0f;
        public bool ApplyCountdownFoldToWaitBeats = false;

        public bool EnableOggMemoryCache = true;
        public int OggCacheMaxMegabytes = 512;

        public bool EnableTimingTracker = true;
        public bool TimingWindowVisible = false;
        public string TapKeyName = "F10";

        public bool EnableTapPhaseCorrection = true;
        public float TapPhaseIgnoreMs = 2.0f;
        public float TapPhaseMaxCorrectionPercent = 3.0f;
        public float TapPhaseMaxAbsoluteMs = 150.0f;
        public float TapMinimumAnchorSpanSeconds = 0.35f;

        public bool EnablePlayErrorCorrection = false;
        public int ErrorCorrectionMinSamples = 6;
        public float ErrorCorrectionMaxPercent = 2.0f;
        public float ErrorCorrectionWindowBeats = 8.0f;
        public float ErrorCorrectionMergeThresholdPercent = 0.05f;
        public float ErrorCorrectionMaxRmsMs = 45.0f;
        public float ErrorCorrectionMinChangePercent = 0.01f;
        public float ErrorCorrectionApplyStrengthPercent = 50.0f;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
