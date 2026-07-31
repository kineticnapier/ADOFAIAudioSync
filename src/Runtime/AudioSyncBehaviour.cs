using System;
using Kiner.ADOFAIAudioSync.Timing;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    [DefaultExecutionOrder(10000)]
    public sealed class AudioSyncBehaviour : MonoBehaviour
    {
        private float largestHitchMs;
        private GUIStyle labelStyle;
        private GUIStyle boxStyle;

        private void Update()
        {
            if (Main.Settings == null) return;

            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (control && Input.GetKeyDown(KeyCode.F8))
            {
                Main.Settings.EnableStartGate = !Main.Settings.EnableStartGate;
                AudioSyncRuntime.Reset(Main.Settings.EnableStartGate ? "開始ゲートON" : "開始ゲートOFF");
                Main.Logger.Log("Deferred editor start gate: " + Main.Settings.EnableStartGate);
            }
            if (control && Input.GetKeyDown(KeyCode.F9))
            {
                Main.Settings.OverlayMode =
                    (Math.Max(0, Math.Min(2, Main.Settings.OverlayMode)) + 1) % 3;
                Main.Logger.Log(
                    "Diagnostic overlay: " +
                    (Main.Settings.OverlayMode == 0
                        ? "OFF"
                        : Main.Settings.OverlayMode == 1 ? "compact" : "detailed"));
                Main.SaveSettingsNow();
            }

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (control && shift && Input.GetKeyDown(KeyCode.D))
            {
                Main.Settings.AutoCorrectDrift = !Main.Settings.AutoCorrectDrift;
                Main.Logger.Log("Automatic drift correction: " + Main.Settings.AutoCorrectDrift);
                Main.SaveSettingsNow();
            }
            if (control && shift && Input.GetKeyDown(KeyCode.P))
            {
                Main.Settings.EnableDspProbeCue = !Main.Settings.EnableDspProbeCue;
                if (!Main.Settings.EnableDspProbeCue)
                    DspProbeCueRuntime.StopAll("probe hotkey off", false);
                Main.Logger.Log("DSP probe cue: " + Main.Settings.EnableDspProbeCue);
                Main.SaveSettingsNow();
            }

            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (frameMs > largestHitchMs) largestHitchMs = frameMs;

            OggAudioCacheRuntime.Update();
            AudioSyncPrewarmRuntime.Update();
            AudioSyncRuntime.UpdateFrame();
            ConductorDriftRuntime.Update();
            DspProbeCueRuntime.Update();
            TimingTrackerRuntime.Update();
            PlayErrorCorrectionRuntime.Update();
        }

        private void OnGUI()
        {
            if (!Main.Enabled || Main.Settings == null) return;

            int overlayMode = Math.Max(0, Math.Min(2, Main.Settings.OverlayMode));
            if (overlayMode == 1)
            {
                DrawCompactOverlay();
            }
            else if (overlayMode == 2)
            {
                DrawDetailedOverlay();
            }

            TimingTrackerRuntime.DrawWindow();
        }

        private void DrawCompactOverlay()
        {
            EnsureStyles();
            double netSyncMs =
                CheckpointStartHandshakeRuntime.LastScheduleResidualMs +
                CheckpointStartHandshakeRuntime.LastPlayheadCorrectionMs;
            string text =
                "AudioSync v0.9.17  " + (Main.Enabled ? "ON" : "OFF") +
                "  OGG:" + OggAudioCacheRuntime.LastLookupResult + Environment.NewLine +
                "Sync " + netSyncMs.ToString("+0.0;-0.0;0.0") +
                "ms | Drift " +
                ConductorDriftRuntime.FilteredDriftMs.ToString("+0.0;-0.0;0.0") +
                "ms | Start " +
                (CheckpointStartHandshakeRuntime.LastStartDelayMs / 1000d).ToString("0.00") +
                "s";

            GUI.Box(new Rect(12f, 12f, 480f, 66f), GUIContent.none, boxStyle);
            GUI.Label(new Rect(24f, 20f, 456f, 48f), text, labelStyle);
        }

        private void DrawDetailedOverlay()
        {
            EnsureStyles();
            string error = string.IsNullOrEmpty(AudioSyncRuntime.LastError)
                ? "-"
                : AudioSyncRuntime.LastError;
            string text =
                "ADOFAI AudioSync v0.9.17  [Ctrl+F8 Gate / Ctrl+F9: 詳細]" + Environment.NewLine +
                "開始: " + AudioSyncRuntime.Status + Environment.NewLine +
                "Gate: " + (Main.Settings.EnableStartGate ? "ON" : "OFF") +
                " / 選択床 " + AudioSyncRuntime.PlaybackStartFloor +
                " / 本体checkpoint " + AudioSyncRuntime.ActualPlaybackStartFloor +
                " / patch " + (AudioSyncRuntime.GatePatchInstalled ? "OK" : "NG") + Environment.NewLine +
                "同一床助走: " + AudioSyncRuntime.SameFloorTakeoffStatus + Environment.NewLine +
                "準備 " + AudioSyncRuntime.EditorPreparationMs.ToString("0.0") + "ms / scnGame.Play " +
                AudioSyncRuntime.GamePlayCallMs.ToString("0.0") + "ms" + Environment.NewLine +
                "高速再開guard: " + (Main.Settings.EnableRapidRestartGuard ? "ON" : "OFF") +
                " / wait " + AudioSyncRuntime.RemainingRestartCooldownMs.ToString("0") +
                "ms/" + AudioSyncRuntime.RemainingRestartCleanupFrames +
                "f / last " + AudioSyncRuntime.LastAppliedRestartCooldownMs.ToString("0") +
                "ms/" + AudioSyncRuntime.LastAppliedRestartCleanupFrames +
                "f / count " + AudioSyncRuntime.RestartCooldownApplyCount + Environment.NewLine +
                "直前停止: " + AudioSyncRuntime.LastPlaybackStopReason + Environment.NewLine +
                "途中再生handshake: " + CheckpointStartHandshakeRuntime.Status + Environment.NewLine +
                "予約残差 " +
                CheckpointStartHandshakeRuntime.LastScheduleResidualMs.ToString("+0.0;-0.0;0.0") + "ms" +
                " / playhead補正 " +
                CheckpointStartHandshakeRuntime.LastPlayheadCorrectionMs.ToString("+0.0;-0.0;0.0") + "ms" +
                " / retries " + CheckpointStartHandshakeRuntime.RetryCount + Environment.NewLine +
                "start wait " + CheckpointStartHandshakeRuntime.LastStartDelayMs.ToString("0.0") +
                "ms / DSP resume " +
                CheckpointStartHandshakeRuntime.LastDspResumeWaitMs.ToString("0.0") +
                "ms / sample actual " + CheckpointStartHandshakeRuntime.CurrentSample +
                " / expected " + CheckpointStartHandshakeRuntime.ExpectedSample +
                " / seek " + CheckpointStartHandshakeRuntime.RequestedSample +
                " / stable " + CheckpointStartHandshakeRuntime.ConsecutiveMovingFrames +
                "/" + Main.Settings.CheckpointStartStableFrames + Environment.NewLine +
                "Countdown: " + CheckpointCountdownRuntime.Status +
                " / 約" + CheckpointCountdownRuntime.CountdownSeconds.ToString("0.00") + "s" +
                Environment.NewLine +
                "OGG cache: " + OggAudioCacheRuntime.Status +
                " / " + OggAudioCacheRuntime.EntryCount + "件 " +
                OggAudioCacheRuntime.EstimatedMegabytes.ToString("0.0") + "MB" +
                " / hit " + OggAudioCacheRuntime.HitCount +
                " miss " + OggAudioCacheRuntime.MissCount + Environment.NewLine +
                "Drift: " + ConductorDriftRuntime.Status +
                " / 自動 " + (Main.Settings.AutoCorrectDrift ? "ON" : "OFF") + Environment.NewLine +
                "raw " + ConductorDriftRuntime.CurrentRawOffsetMs.ToString("+0.0;-0.0;0.0") +
                "ms / baseline " + ConductorDriftRuntime.BaselineOffsetMs.ToString("+0.0;-0.0;0.0") +
                "ms / drift " + ConductorDriftRuntime.FilteredDriftMs.ToString("+0.0;-0.0;0.0") + "ms" + Environment.NewLine +
                "AudioSource " + ConductorDriftRuntime.ActualSourceSeconds.ToString("0.000") +
                "s / predicted " + ConductorDriftRuntime.PredictedSourceSeconds.ToString("0.000") + "s" + Environment.NewLine +
                "次床 " + ConductorDriftRuntime.NextFloorId + " due " +
                ConductorDriftRuntime.NextFloorDueDeltaMs.ToString("+0.0;-0.0;0.0") +
                "ms / hit timeline " + ConductorDriftRuntime.CapturedHitSoundCount + "件" + Environment.NewLine +
                "補正回数 " + ConductorDriftRuntime.CorrectionCount + " / 最終 " +
                ConductorDriftRuntime.LastCorrection + Environment.NewLine +
                "DSP probe: " + DspProbeCueRuntime.Status +
                " / prewarm: " + AudioSyncPrewarmRuntime.Status + Environment.NewLine +
                "lifecycle: " + AudioSyncLifecycleRuntime.LastReason + Environment.NewLine +
                "frame " + (Time.unscaledDeltaTime * 1000f).ToString("0.0") +
                "ms / max " + largestHitchMs.ToString("0.0") + "ms" + Environment.NewLine +
                "エラー: " + error;

            GUI.Box(new Rect(12f, 12f, 760f, 550f), GUIContent.none, boxStyle);
            GUI.Label(new Rect(24f, 22f, 736f, 530f), text, labelStyle);
        }

        private void EnsureStyles()
        {
            if (labelStyle != null) return;
            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 15;
            labelStyle.normal.textColor = Color.white;
            labelStyle.richText = false;
            boxStyle = new GUIStyle(GUI.skin.box);
        }
    }
}
