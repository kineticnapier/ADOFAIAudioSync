using UnityModManagerNet;

namespace Kiner.ADOFAIAudioSync
{
    public sealed class AudioSyncSettings : UnityModManager.ModSettings
    {
        public int SettingsRevision = 0;
        public bool EnableStartGate = true;
        public bool ShowOverlay = false;
        public int ExtraPreparationFrames = 0;
        public bool EnableRapidRestartGuard = true;
        public float RapidRestartCooldownMs = 500.0f;
        public int RapidRestartCleanupFrames = 2;

        public bool EnableCheckpointStartHandshake = true;
        public int CheckpointStartStableFrames = 2;
        public float CheckpointStartTimeoutMs = 2000.0f;
        // Legacy v0.9.8-v0.9.10 fields retained for Settings.xml compatibility.
        // The current handshake does not call ScrubToFloorNumber.
        public bool EnableCheckpointVisualLeadIn = false;
        public float CheckpointVisualPrerollMs = 250.0f;
        public float CheckpointScheduleLeadMs = 600.0f;
        // Kept under its old serialized name. This is the maximum absolute
        // residual between the observed sample and the sample expected from the DSP schedule.
        public float CheckpointMaxInitialAdvanceMs = 50.0f;
        public int CheckpointScheduleRetryCount = 1;
        public bool EnableCheckpointCountdownFold = true;
        public float CheckpointCountdownMaxBpm = 240.0f;

        public bool EnableOggMemoryCache = true;
        public int OggCacheMaxMegabytes = 512;

        // Legacy v0.9.6 fields retained only so old Settings.xml files deserialize cleanly.
        // The current handshake never changes the checkpoint floor and does not use these values.
        public bool EnablePracticePreroll = false;
        public float PracticePrerollBeats = 4.0f;

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

        public bool EnableStartAlignment = true;
        public bool ReseekCheckpointBeforeUnpause = false;
        public bool AlignSecondarySong = true;
        public bool EnableStartCsvLog = true;
        public int StartVerificationFrames = 12;
        public float StartMismatchWarningMs = 35.0f;
        public float StartScheduleLeadMs = 1000.0f;
        public bool AutoLockStartToPlayhead = false;
        public float StartAutoLockMaxMs = 250.0f;

        public bool EnableIdlePrewarm = true;
        public int IdlePrewarmVoiceCount = 4;

        public bool EnableDriftMonitor = false;
        public bool AutoCorrectDrift = false;
        public float DriftThresholdMs = 20.0f;
        public int DriftConsecutiveSamples = 4;
        public float DriftSampleIntervalMs = 100.0f;
        public float DriftWarmupSeconds = 1.5f;
        public float DriftMaxCorrectionMs = 150.0f;
        public float DriftCorrectionCooldownSeconds = 3.0f;
        public bool RebuildHitTimelineAfterDriftCorrection = true;
        public bool EnableDriftCsvLog = false;
        public float DriftCsvFlushIntervalSeconds = 1.0f;

        public bool EnableDspProbeCue = false;
        public float DspProbeCueVolume = 0.15f;
        public float DspProbeLookAheadSeconds = 1.5f;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
