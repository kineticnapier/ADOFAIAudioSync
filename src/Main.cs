using System;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Kiner.ADOFAIAudioSync.Runtime;
using Kiner.ADOFAIAudioSync.Timing;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityModManagerNet;

namespace Kiner.ADOFAIAudioSync
{
    public static class Main
    {
        internal static UnityModManager.ModEntry.ModLogger Logger;
        internal static AudioSyncSettings Settings;
        internal static string ModPath;
        internal static bool Enabled;

        private static Harmony harmony;
        private static GameObject host;
        private static UnityModManager.ModEntry currentModEntry;
        private static bool showExperimentalSettings;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            currentModEntry = modEntry;
            Logger = modEntry.Logger;
            ModPath = modEntry.Path;
            Logger.Log("ADOFAI AudioSync v0.9.12 bootstrap started.");

            try
            {
                Logger.Log("[1/5] Loading settings...");
                Settings = LoadSettingsSafely(modEntry);
                NormalizeSettings();
                Enabled = true;

                Logger.Log("[2/5] Initializing runtimes...");
                AudioSyncRuntime.Initialize();
                CheckpointStartHandshakeRuntime.Initialize();
                AudioSyncPrewarmRuntime.Initialize();
                TimingTrackerRuntime.Initialize();
                PlayErrorCorrectionRuntime.Initialize();
                ConductorDriftRuntime.Initialize();
                DspProbeCueRuntime.Initialize();
                AudioSyncLifecycleRuntime.Initialize();

                Logger.Log("[3/5] Creating runtime host...");
                CreateHost();

                Logger.Log("[4/5] Installing Harmony patches...");
                TryInstallPatches(modEntry);

                Logger.Log("[5/5] Registering UMM callbacks...");
                modEntry.OnToggle = OnToggle;
                modEntry.OnGUI = OnGUI;
                modEntry.OnSaveGUI = OnSaveGUI;
                modEntry.OnUnload = OnUnload;

                Logger.Log("ADOFAI AudioSync v0.9.12 loaded.");
                Logger.Log("Selected-floor playback validates a future DSP reservation, then aligns once to the observed AudioSource playhead.");
                Logger.Log("Checkpoint handshake is " + (Settings.EnableCheckpointStartHandshake ? "ON" : "OFF") +
                           " (" + Settings.CheckpointStartStableFrames + " moving frame(s), timeout " +
                           Settings.CheckpointStartTimeoutMs.ToString("0") + " ms).");
                Logger.Log("Selected-floor playback keeps the original checkpoint and uses ADOFAI's stock same-floor angular takeoff.");
                Logger.Log("Checkpoint schedule lead is " + Settings.CheckpointScheduleLeadMs.ToString("0") +
                           " ms. The legacy per-frame visual ScrubToFloorNumber path is disabled.");
                Logger.Log("Starts with an absolute schedule residual above " +
                           Settings.CheckpointMaxInitialAdvanceMs.ToString("0") +
                           " ms are automatically rescheduled.");
                Logger.Log("Rapid restart guard is " + (Settings.EnableRapidRestartGuard ? "ON" : "OFF") +
                           " (minimum stop-to-start interval " + Settings.RapidRestartCooldownMs.ToString("0") + " ms).");
                Logger.Log("Automatic drift correction is " + (Settings.AutoCorrectDrift ? "ON" : "OFF") + ".");
                return true;
            }
            catch (Exception ex)
            {
                Enabled = false;
                LogException("Fatal load failure", ex);
                CleanupAfterFailedLoad(modEntry);
                return false;
            }
        }

        private static AudioSyncSettings LoadSettingsSafely(UnityModManager.ModEntry modEntry)
        {
            try
            {
                return UnityModManager.ModSettings.Load<AudioSyncSettings>(modEntry) ??
                       new AudioSyncSettings();
            }
            catch (Exception ex)
            {
                LogException("Settings.xml could not be loaded; defaults will be used", ex);
                BackupBrokenSettings();
                return new AudioSyncSettings();
            }
        }

        private static void BackupBrokenSettings()
        {
            try
            {
                string path = Path.Combine(ModPath ?? string.Empty, "Settings.xml");
                if (!File.Exists(path)) return;
                string backup = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(path, backup);
                if (Logger != null) Logger.Warning("Broken Settings.xml moved to: " + backup);
            }
            catch (Exception ex)
            {
                if (Logger != null) Logger.Warning("Could not back up Settings.xml: " + ex.Message);
            }
        }

        private static void CreateHost()
        {
            if (host != null) return;
            host = new GameObject("ADOFAI AudioSync + BPM Tap Meter");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<AudioSyncBehaviour>();
        }

        private static void TryInstallPatches(UnityModManager.ModEntry modEntry)
        {
            harmony = new Harmony(modEntry.Info.Id);
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                AudioSyncRuntime.SetGatePatchInstalled(true);
                Logger.Log("Harmony patches installed: editor preparation gate, checkpoint playhead handshake, lifecycle hooks, and hit-timeline diagnostics.");
            }
            catch (Exception ex)
            {
                // Tap measurement and the core drift monitor can still run without Harmony.
                // Start-gate interception and lifecycle hooks are disabled together on failure.
                LogException("Harmony patches failed; tap measurement will continue, but start gate/alignment hooks are disabled", ex);
                try { harmony.UnpatchAll(modEntry.Info.Id); } catch { }
                AudioSyncRuntime.SetGatePatchInstalled(false);
            }
        }

        private static void NormalizeSettings()
        {
            if (Settings == null) Settings = new AudioSyncSettings();
            if (Settings.SettingsRevision < 802)
            {
                // v0.8.0/0.8.1 focused on an in-play drift hypothesis. Existing Settings.xml may
                // therefore have the monitor enabled. v0.8.2 returns to start-only repair.
                Settings.EnableDriftMonitor = false;
                Settings.AutoCorrectDrift = false;
                Settings.EnableDspProbeCue = false;
                Settings.EnableDriftCsvLog = false;
                Settings.SettingsRevision = 802;
            }
            if (Settings.SettingsRevision < 803)
            {
                // Older play-error correction applied the full measured factor and did not restore
                // the original BPM after a valid region. Use a damped first pass in the fixed model.
                Settings.ErrorCorrectionApplyStrengthPercent = 50f;
                Settings.SettingsRevision = 803;
            }
            if (Settings.SettingsRevision < 900)
            {
                // v0.9.0 makes Ctrl+T BPM+phase anchoring the primary workflow. The play-error
                // corrector remains available only as an experimental, opt-in tool.
                Settings.EnablePlayErrorCorrection = false;
                Settings.EnableTapPhaseCorrection = true;
                Settings.TapPhaseIgnoreMs = 2f;
                Settings.TapPhaseMaxCorrectionPercent = 3f;
                Settings.TapPhaseMaxAbsoluteMs = 150f;
                Settings.TapMinimumAnchorSpanSeconds = 0.35f;
                Settings.SettingsRevision = 900;
            }
            if (Settings.SettingsRevision < 901)
            {
                // Stock checkpoint scrubbing replaces the normal buffered PlayScheduled start
                // with SetScheduledStartTime(dspTime), leaving no decode lead. v0.9.1 restores a
                // future DSP start and can lock the chart once to the real started playhead.
                Settings.StartScheduleLeadMs = 500f;
                Settings.AutoLockStartToPlayhead = true;
                Settings.StartAutoLockMaxMs = 250f;
                Settings.SettingsRevision = 901;
            }
            if (Settings.SettingsRevision < 902)
            {
                // v0.9.1 changed the schedule after the checkpoint state had already begun.
                // v0.9.3 builds the final song/chart origin synchronously inside the first
                // ScrubMusicToTime call and never applies a post-start origin lock.
                Settings.StartScheduleLeadMs = 1000f;
                Settings.ReseekCheckpointBeforeUnpause = false;
                Settings.AutoLockStartToPlayhead = false;
                Settings.SettingsRevision = 902;
            }
            if (Settings.SettingsRevision < 903)
            {
                // v0.9.3 replaced the checkpoint-only ScrubMusicToTime route. In practice,
                // starts from a selected floor were substantially less stable than starts from
                // the beginning. v0.9.3 rolls all song/origin intervention back and retains only
                // the proven scnEditor.Play -> scnGame.Play preparation gate.
                Settings.EnableStartAlignment = false;
                Settings.EnableStartCsvLog = false;
                Settings.AutoLockStartToPlayhead = false;
                Settings.ReseekCheckpointBeforeUnpause = false;
                Settings.SettingsRevision = 903;
            }
            if (Settings.SettingsRevision < 904)
            {
                // Returning to the editor and immediately starting again can overlap Unity's
                // end-of-frame AudioSource/coroutine cleanup. Only rapid restarts are delayed;
                // ordinary starts keep their existing timing.
                Settings.EnableRapidRestartGuard = true;
                Settings.RapidRestartCooldownMs = 500f;
                Settings.RapidRestartCleanupFrames = 2;
                Settings.SettingsRevision = 904;
            }
            if (Settings.SettingsRevision < 905)
            {
                // WAV reduced random seek error but did not eliminate rare 300+ ms starts.
                // The remaining failure is the chart clock being released before the real
                // AudioSource playhead starts. Hold checkpoint starts until timeSamples moves.
                Settings.EnableCheckpointStartHandshake = true;
                Settings.CheckpointStartStableFrames = 2;
                Settings.CheckpointStartTimeoutMs = 2000f;
                Settings.SettingsRevision = 905;
            }
            if (Settings.SettingsRevision < 907)
            {
                // v0.9.6 incorrectly moved the actual checkpoint to an earlier floor. Stock
                // Scrub already keeps the selected floor fixed and gives the planets an angular
                // lead-in inside that same floor. Disable the legacy floor-shifting option.
                Settings.EnablePracticePreroll = false;
                Settings.SettingsRevision = 907;
            }
            if (Settings.SettingsRevision < 908)
            {
                // v0.9.7 pinned the chart clock while waiting for the real AudioSource playhead,
                // which kept the planets visually frozen and caused a sudden jump at release.
                // v0.9.8 keeps gameplay time pinned but previews the stock same-floor lead-in
                // visually at real-time speed, then hands off at the actual sample position.
                Settings.EnableCheckpointVisualLeadIn = true;
                Settings.SettingsRevision = 908;
            }
            if (Settings.SettingsRevision < 909)
            {
                // v0.9.8 visually advanced the planets while the chart clock was frozen. If
                // Unity exposed the first audio sample hundreds of milliseconds ahead, Go!
                // began from an already progressed position. v0.9.9 keeps the stock lead-in
                // position fixed, future-schedules the seeked AudioSource, and retries any
                // start whose first stable sample is too far ahead.
                Settings.EnableCheckpointVisualLeadIn = false;
                Settings.CheckpointScheduleLeadMs = 600f;
                Settings.CheckpointMaxInitialAdvanceMs = 50f;
                Settings.CheckpointScheduleRetryCount = 1;
                Settings.CheckpointStartTimeoutMs = 2500f;
                Settings.SettingsRevision = 909;
            }
            if (Settings.SettingsRevision < 910)
            {
                // Keep v0.9.9's future DSP reservation, but restore motion without spending
                // the real countdown. The planets approach the stock lead-in start from a
                // short extra-behind pose, then the normal countdown begins with the audio.
                Settings.EnableCheckpointVisualLeadIn = true;
                Settings.CheckpointVisualPrerollMs = 250f;
                Settings.SettingsRevision = 910;
            }
            if (Settings.SettingsRevision < 911)
            {
                // v0.9.10 measured all samples consumed while waiting for stable frames as
                // start error and repeatedly called ScrubToFloorNumber from LateUpdate.
                // v0.9.11 measures only the residual from the DSP schedule and removes that
                // state-mutating visual path.
                Settings.EnableCheckpointVisualLeadIn = false;
                Settings.SettingsRevision = 911;
            }
            if (Settings.TapPhaseIgnoreMs < 0f) Settings.TapPhaseIgnoreMs = 0f;
            if (Settings.TapPhaseIgnoreMs > 100f) Settings.TapPhaseIgnoreMs = 100f;
            if (Settings.TapPhaseMaxCorrectionPercent < 0.1f) Settings.TapPhaseMaxCorrectionPercent = 0.1f;
            if (Settings.TapPhaseMaxCorrectionPercent > 20f) Settings.TapPhaseMaxCorrectionPercent = 20f;
            if (Settings.TapPhaseMaxAbsoluteMs < 20f) Settings.TapPhaseMaxAbsoluteMs = 20f;
            if (Settings.TapPhaseMaxAbsoluteMs > 2000f) Settings.TapPhaseMaxAbsoluteMs = 2000f;
            if (Settings.TapMinimumAnchorSpanSeconds < 0.1f) Settings.TapMinimumAnchorSpanSeconds = 0.1f;
            if (Settings.TapMinimumAnchorSpanSeconds > 10f) Settings.TapMinimumAnchorSpanSeconds = 10f;
            if (Settings.ExtraPreparationFrames < 0) Settings.ExtraPreparationFrames = 0;
            if (Settings.ExtraPreparationFrames > 3) Settings.ExtraPreparationFrames = 3;
            if (Settings.RapidRestartCooldownMs < 100f) Settings.RapidRestartCooldownMs = 100f;
            if (Settings.RapidRestartCooldownMs > 2000f) Settings.RapidRestartCooldownMs = 2000f;
            if (Settings.RapidRestartCleanupFrames < 1) Settings.RapidRestartCleanupFrames = 1;
            if (Settings.RapidRestartCleanupFrames > 8) Settings.RapidRestartCleanupFrames = 8;
            if (Settings.CheckpointStartStableFrames < 1) Settings.CheckpointStartStableFrames = 1;
            if (Settings.CheckpointStartStableFrames > 6) Settings.CheckpointStartStableFrames = 6;
            if (Settings.CheckpointStartTimeoutMs < 500f) Settings.CheckpointStartTimeoutMs = 500f;
            if (Settings.CheckpointStartTimeoutMs > 5000f) Settings.CheckpointStartTimeoutMs = 5000f;
            if (Settings.CheckpointScheduleLeadMs < 100f) Settings.CheckpointScheduleLeadMs = 100f;
            if (Settings.CheckpointScheduleLeadMs > 2000f) Settings.CheckpointScheduleLeadMs = 2000f;
            if (Settings.CheckpointMaxInitialAdvanceMs < 5f) Settings.CheckpointMaxInitialAdvanceMs = 5f;
            if (Settings.CheckpointMaxInitialAdvanceMs > 250f) Settings.CheckpointMaxInitialAdvanceMs = 250f;
            if (Settings.CheckpointScheduleRetryCount < 0) Settings.CheckpointScheduleRetryCount = 0;
            if (Settings.CheckpointScheduleRetryCount > 3) Settings.CheckpointScheduleRetryCount = 3;
            if (Settings.CheckpointVisualPrerollMs < 50f) Settings.CheckpointVisualPrerollMs = 50f;
            if (Settings.CheckpointVisualPrerollMs > 750f) Settings.CheckpointVisualPrerollMs = 750f;
            if (Settings.ErrorCorrectionMinSamples < 3) Settings.ErrorCorrectionMinSamples = 3;
            if (Settings.ErrorCorrectionMinSamples > 100) Settings.ErrorCorrectionMinSamples = 100;
            if (Settings.ErrorCorrectionMaxPercent < 0.01f) Settings.ErrorCorrectionMaxPercent = 0.01f;
            if (Settings.ErrorCorrectionMaxPercent > 20f) Settings.ErrorCorrectionMaxPercent = 20f;
            if (Settings.ErrorCorrectionWindowBeats < 1f) Settings.ErrorCorrectionWindowBeats = 1f;
            if (Settings.ErrorCorrectionWindowBeats > 64f) Settings.ErrorCorrectionWindowBeats = 64f;
            if (Settings.ErrorCorrectionMergeThresholdPercent < 0.001f) Settings.ErrorCorrectionMergeThresholdPercent = 0.001f;
            if (Settings.ErrorCorrectionMergeThresholdPercent > 5f) Settings.ErrorCorrectionMergeThresholdPercent = 5f;
            if (Settings.ErrorCorrectionMaxRmsMs < 5f) Settings.ErrorCorrectionMaxRmsMs = 5f;
            if (Settings.ErrorCorrectionMaxRmsMs > 500f) Settings.ErrorCorrectionMaxRmsMs = 500f;
            if (Settings.ErrorCorrectionMinChangePercent < 0f) Settings.ErrorCorrectionMinChangePercent = 0f;
            if (Settings.ErrorCorrectionMinChangePercent > 5f) Settings.ErrorCorrectionMinChangePercent = 5f;
            if (Settings.ErrorCorrectionApplyStrengthPercent < 5f) Settings.ErrorCorrectionApplyStrengthPercent = 5f;
            if (Settings.ErrorCorrectionApplyStrengthPercent > 100f) Settings.ErrorCorrectionApplyStrengthPercent = 100f;
            if (Settings.StartVerificationFrames < 4) Settings.StartVerificationFrames = 4;
            if (Settings.StartVerificationFrames > 30) Settings.StartVerificationFrames = 30;
            if (Settings.StartMismatchWarningMs < 10f) Settings.StartMismatchWarningMs = 10f;
            if (Settings.StartMismatchWarningMs > 250f) Settings.StartMismatchWarningMs = 250f;
            if (Settings.StartScheduleLeadMs < 250f) Settings.StartScheduleLeadMs = 250f;
            if (Settings.StartScheduleLeadMs > 2000f) Settings.StartScheduleLeadMs = 2000f;
            if (Settings.StartAutoLockMaxMs < 20f) Settings.StartAutoLockMaxMs = 20f;
            if (Settings.StartAutoLockMaxMs > 1000f) Settings.StartAutoLockMaxMs = 1000f;
            if (Settings.IdlePrewarmVoiceCount < 1) Settings.IdlePrewarmVoiceCount = 1;
            if (Settings.IdlePrewarmVoiceCount > 16) Settings.IdlePrewarmVoiceCount = 16;
            if (Settings.DriftCsvFlushIntervalSeconds < 0.25f) Settings.DriftCsvFlushIntervalSeconds = 0.25f;
            if (Settings.DriftCsvFlushIntervalSeconds > 10f) Settings.DriftCsvFlushIntervalSeconds = 10f;
            if (Settings.DriftThresholdMs < 5f) Settings.DriftThresholdMs = 5f;
            if (Settings.DriftThresholdMs > 500f) Settings.DriftThresholdMs = 500f;
            if (Settings.DriftConsecutiveSamples < 2) Settings.DriftConsecutiveSamples = 2;
            if (Settings.DriftConsecutiveSamples > 30) Settings.DriftConsecutiveSamples = 30;
            if (Settings.DriftSampleIntervalMs < 20f) Settings.DriftSampleIntervalMs = 20f;
            if (Settings.DriftSampleIntervalMs > 1000f) Settings.DriftSampleIntervalMs = 1000f;
            if (Settings.DriftWarmupSeconds < 0.25f) Settings.DriftWarmupSeconds = 0.25f;
            if (Settings.DriftWarmupSeconds > 10f) Settings.DriftWarmupSeconds = 10f;
            if (Settings.DriftMaxCorrectionMs < Settings.DriftThresholdMs) Settings.DriftMaxCorrectionMs = Settings.DriftThresholdMs;
            if (Settings.DriftMaxCorrectionMs > 1000f) Settings.DriftMaxCorrectionMs = 1000f;
            if (Settings.DriftCorrectionCooldownSeconds < 0.5f) Settings.DriftCorrectionCooldownSeconds = 0.5f;
            if (Settings.DriftCorrectionCooldownSeconds > 30f) Settings.DriftCorrectionCooldownSeconds = 30f;
            if (Settings.DspProbeCueVolume < 0f) Settings.DspProbeCueVolume = 0f;
            if (Settings.DspProbeCueVolume > 1f) Settings.DspProbeCueVolume = 1f;
            if (Settings.DspProbeLookAheadSeconds < 0.25f) Settings.DspProbeLookAheadSeconds = 0.25f;
            if (Settings.DspProbeLookAheadSeconds > 10f) Settings.DspProbeLookAheadSeconds = 10f;
            KeyCode tapKey;
            if (string.IsNullOrEmpty(Settings.TapKeyName) ||
                !Enum.TryParse(Settings.TapKeyName, true, out tapKey) || tapKey == KeyCode.None)
                Settings.TapKeyName = KeyCode.F10.ToString();
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            AudioSyncRuntime.Reset(value ? "Mod有効" : "Mod無効");
            CheckpointStartHandshakeRuntime.Reset(value ? "Mod有効" : "Mod無効");
            if (!value)
            {
                TimingTrackerRuntime.CloseWindow();
                PlayErrorCorrectionRuntime.Shutdown();
                AudioSyncLifecycleRuntime.NotifyStop("Mod disabled", false);
            }
            else
            {
                PlayErrorCorrectionRuntime.Initialize();
                AudioSyncPrewarmRuntime.Restart();
                ConductorDriftRuntime.ResetBaseline("Mod enabled");
            }
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            if (Settings == null) Settings = new AudioSyncSettings();

            Settings.EnableStartGate = GUILayout.Toggle(Settings.EnableStartGate,
                "エディター側の全準備後まで scnGame.Play を保留する");
            Settings.ShowOverlay = GUILayout.Toggle(Settings.ShowOverlay,
                "開始ゲートの診断表示を画面左上へ表示する");

            GUILayout.Space(8f);
            GUILayout.Label("途中再生のDSP予約（v0.9.12）");
            GUILayout.Label("選択床とcheckpointを維持したまま、予約時刻と期待サンプルを固定します。");
            GUILayout.Label("開始確認に使ったフレーム時間は誤差判定から除外します。");

            Settings.EnableCheckpointStartHandshake = GUILayout.Toggle(
                Settings.EnableCheckpointStartHandshake,
                "途中再生を未来のDSP時刻へ予約し、正しい開始サンプルから解放する");
            GUILayout.Label("音源の予約先行時間: " + Settings.CheckpointScheduleLeadMs.ToString("0") + "ms");
            Settings.CheckpointScheduleLeadMs = GUILayout.HorizontalSlider(
                Settings.CheckpointScheduleLeadMs, 100f, 1500f);
            GUILayout.Label("許容する予約残差: ±" + Settings.CheckpointMaxInitialAdvanceMs.ToString("0") + "ms");
            Settings.CheckpointMaxInitialAdvanceMs = GUILayout.HorizontalSlider(
                Settings.CheckpointMaxInitialAdvanceMs, 5f, 150f);
            GUILayout.Label("開始失敗時の自動再予約回数: " + Settings.CheckpointScheduleRetryCount);
            Settings.CheckpointScheduleRetryCount = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(Settings.CheckpointScheduleRetryCount, 0f, 3f));
            GUILayout.Label("開始確認に必要な連続移動フレーム: " + Settings.CheckpointStartStableFrames);
            Settings.CheckpointStartStableFrames = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(Settings.CheckpointStartStableFrames, 1f, 5f));
            GUILayout.Label("1回の開始待ちtimeout: " + Settings.CheckpointStartTimeoutMs.ToString("0") + "ms");
            Settings.CheckpointStartTimeoutMs = GUILayout.HorizontalSlider(
                Settings.CheckpointStartTimeoutMs, 1000f, 5000f);
            GUILayout.Label("状態: " + CheckpointStartHandshakeRuntime.Status);
            GUILayout.Label("直近の予約残差: " +
                            CheckpointStartHandshakeRuntime.LastScheduleResidualMs.ToString("+0.0;-0.0;0.0") + "ms" +
                            " / 適用補正 " +
                            CheckpointStartHandshakeRuntime.LastPlayheadCorrectionMs.ToString("+0.0;-0.0;0.0") + "ms" +
                            " / 再予約累計 " + CheckpointStartHandshakeRuntime.RetryCount +
                            " / 完了 " + CheckpointStartHandshakeRuntime.CompletionCount +
                            " / timeout " + CheckpointStartHandshakeRuntime.TimeoutCount);
            GUILayout.Label("最終dspTimeSong確定後に、本体PlayHitTimesを1回だけ構築します。別Modは操作しません。");

            GUILayout.Space(8f);
            GUILayout.Label("高速再開ガード");
            Settings.EnableRapidRestartGuard = GUILayout.Toggle(Settings.EnableRapidRestartGuard,
                "再生終了直後だけAudioSourceの後始末を待ってから開始する");
            GUILayout.Label("停止から開始までの最低間隔: " +
                            Settings.RapidRestartCooldownMs.ToString("0") + "ms");
            Settings.RapidRestartCooldownMs = GUILayout.HorizontalSlider(
                Settings.RapidRestartCooldownMs, 100f, 1500f);
            GUILayout.Label("停止後に必ず通す更新フレーム: " + Settings.RapidRestartCleanupFrames);
            Settings.RapidRestartCleanupFrames = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(Settings.RapidRestartCleanupFrames, 1f, 5f));
            GUILayout.Label("通常再生では待機しません。時間と更新フレームの両方を満たすまでだけ待ちます。");
            GUILayout.Label("この機能は開始ゲートがONのときだけ動作します。");
            GUILayout.Label("状態: " + AudioSyncRuntime.Status +
                            " / 適用回数 " + AudioSyncRuntime.RestartCooldownApplyCount);

            GUILayout.Space(10f);
            GUILayout.Label("BPM＋位相タップアンカー（主機能）");
            Settings.EnableTimingTracker = GUILayout.Toggle(Settings.EnableTimingTracker,
                "Ctrl+TのBPMタップ計測を有効にする");
            Settings.TimingWindowVisible = GUILayout.Toggle(Settings.TimingWindowVisible,
                "BPM＋位相計測ウィンドウを表示する");
            GUILayout.Label("現在のタップキー: " + Settings.TapKeyName + "（ウィンドウ内から変更）");
            Settings.EnableTapPhaseCorrection = GUILayout.Toggle(Settings.EnableTapPhaseCorrection,
                "計測BPMと同時に選択床の位相も補正する");
            GUILayout.Label("無視する位相差: ±" + Settings.TapPhaseIgnoreMs.ToString("0.0") + "ms未満");
            Settings.TapPhaseIgnoreMs = GUILayout.HorizontalSlider(Settings.TapPhaseIgnoreMs, 0f, 30f);
            GUILayout.Label("位相補正の最大速度変化: ±" +
                            Settings.TapPhaseMaxCorrectionPercent.ToString("0.00") + "%");
            Settings.TapPhaseMaxCorrectionPercent = GUILayout.HorizontalSlider(
                Settings.TapPhaseMaxCorrectionPercent, 0.25f, 10f);
            GUILayout.Label("位相差の絶対安全上限: ±" + Settings.TapPhaseMaxAbsoluteMs.ToString("0") + "ms");
            Settings.TapPhaseMaxAbsoluteMs = GUILayout.HorizontalSlider(
                Settings.TapPhaseMaxAbsoluteMs, 30f, 500f);
            GUILayout.Label("位相アンカー区間の最低長: " +
                            Settings.TapMinimumAnchorSpanSeconds.ToString("0.00") + "秒");
            Settings.TapMinimumAnchorSpanSeconds = GUILayout.HorizontalSlider(
                Settings.TapMinimumAnchorSpanSeconds, 0.1f, 2f);
            GUILayout.Label("適用後のBPMは後続へ継続します。後端の復元SetSpeedは追加しません。");

            GUILayout.Space(10f);
            GUILayout.Label("初回再生のプリウォーム");
            Settings.EnableIdlePrewarm = GUILayout.Toggle(Settings.EnableIdlePrewarm,
                "エディター待機中に診断音・反射・ログを事前準備する");
            GUILayout.Label("事前生成AudioSource数: " + Settings.IdlePrewarmVoiceCount);
            Settings.IdlePrewarmVoiceCount = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(Settings.IdlePrewarmVoiceCount, 1f, 8f));
            GUILayout.Label("状態: " + AudioSyncPrewarmRuntime.Status);
            if (GUILayout.Button("プリウォームを再実行")) AudioSyncPrewarmRuntime.Restart();

            GUILayout.Space(10f);
            GUILayout.Label("音源ドリフト監視（診断用・既定OFF）");
            Settings.EnableDriftMonitor = GUILayout.Toggle(Settings.EnableDriftMonitor,
                "途中から発生する音源ドリフトを監視する");
            Settings.AutoCorrectDrift = GUILayout.Toggle(Settings.AutoCorrectDrift,
                "持続ドリフトを自動補正する");
            Settings.EnableDriftCsvLog = GUILayout.Toggle(Settings.EnableDriftCsvLog,
                "基準・異常・補正を Logs/audio-drift-*.csv へ記録する");
            Settings.RebuildHitTimelineAfterDriftCorrection = GUILayout.Toggle(
                Settings.RebuildHitTimelineAfterDriftCorrection,
                "補正後に本体の未来ヒット音タイムラインを再構築する");
            GUILayout.Label("異常判定: ±" + Settings.DriftThresholdMs.ToString("0.0") + "ms以上");
            Settings.DriftThresholdMs = GUILayout.HorizontalSlider(Settings.DriftThresholdMs, 5f, 100f);
            GUILayout.Label("連続サンプル数: " + Settings.DriftConsecutiveSamples);
            Settings.DriftConsecutiveSamples = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(Settings.DriftConsecutiveSamples, 2f, 12f));
            GUILayout.Label("監視間隔: " + Settings.DriftSampleIntervalMs.ToString("0") + "ms");
            Settings.DriftSampleIntervalMs = GUILayout.HorizontalSlider(
                Settings.DriftSampleIntervalMs, 20f, 500f);
            GUILayout.Label("CSVまとめ書き間隔: " + Settings.DriftCsvFlushIntervalSeconds.ToString("0.00") + "秒");
            Settings.DriftCsvFlushIntervalSeconds = GUILayout.HorizontalSlider(
                Settings.DriftCsvFlushIntervalSeconds, 0.25f, 5f);
            GUILayout.Label("開始時の基準学習: " + Settings.DriftWarmupSeconds.ToString("0.00") + "秒");
            Settings.DriftWarmupSeconds = GUILayout.HorizontalSlider(
                Settings.DriftWarmupSeconds, 0.25f, 5f);
            GUILayout.Label("1回の補正安全上限: ±" + Settings.DriftMaxCorrectionMs.ToString("0") + "ms");
            Settings.DriftMaxCorrectionMs = GUILayout.HorizontalSlider(
                Settings.DriftMaxCorrectionMs, 20f, 500f);
            GUILayout.Label("補正クールダウン: " + Settings.DriftCorrectionCooldownSeconds.ToString("0.0") + "秒");
            Settings.DriftCorrectionCooldownSeconds = GUILayout.HorizontalSlider(
                Settings.DriftCorrectionCooldownSeconds, 0.5f, 10f);
            Settings.EnableDspProbeCue = GUILayout.Toggle(Settings.EnableDspProbeCue,
                "DSP基準プローブ音を床時刻へ予約する");
            GUILayout.Label("プローブ音量: " + Mathf.RoundToInt(Settings.DspProbeCueVolume * 100f) + "%");
            Settings.DspProbeCueVolume = GUILayout.HorizontalSlider(Settings.DspProbeCueVolume, 0f, 0.5f);
            GUILayout.Label("プローブ先行予約: " + Settings.DspProbeLookAheadSeconds.ToString("0.0") + "秒");
            Settings.DspProbeLookAheadSeconds = GUILayout.HorizontalSlider(
                Settings.DspProbeLookAheadSeconds, 0.25f, 5f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("ドリフト基準を再学習"))
                ConductorDriftRuntime.ResetBaseline("手動基準再学習");
            if (GUILayout.Button("現在の検出ドリフトを手動補正"))
                ConductorDriftRuntime.TryCorrectNow();
            GUILayout.EndHorizontal();

            GUILayout.Space(10f);
            GUILayout.Label("追加準備フレーム: " + Settings.ExtraPreparationFrames);
            Settings.ExtraPreparationFrames = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(Settings.ExtraPreparationFrames, 0f, 3f));
            if (GUILayout.Button("開始ゲート状態をリセット"))
                AudioSyncRuntime.Reset("手動リセット");
            if (GUILayout.Button("BPM計測状態をリセット"))
                TimingTrackerRuntime.ReloadForCurrentLevel();

            GUILayout.Space(14f);
            if (GUILayout.Button((showExperimentalSettings ? "▼" : "▶") +
                                 " 実験的機能：プレイ誤差からSetSpeedを一括補正"))
                showExperimentalSettings = !showExperimentalSettings;
            if (showExperimentalSettings)
            {
                Settings.EnablePlayErrorCorrection = GUILayout.Toggle(Settings.EnablePlayErrorCorrection,
                    "プレイ誤差補正を有効にする（通常はOFF推奨）");
                GUILayout.Label("仮分割の長さ: " + Settings.ErrorCorrectionWindowBeats.ToString("0.0") + "拍");
                Settings.ErrorCorrectionWindowBeats = GUILayout.HorizontalSlider(
                    Settings.ErrorCorrectionWindowBeats, 2f, 32f);
                GUILayout.Label("区間ごとの最低ヒット数: " + Settings.ErrorCorrectionMinSamples);
                Settings.ErrorCorrectionMinSamples = Mathf.RoundToInt(
                    GUILayout.HorizontalSlider(Settings.ErrorCorrectionMinSamples, 3f, 30f));
                GUILayout.Label("隣接区間を結合するBPM差: " +
                                Settings.ErrorCorrectionMergeThresholdPercent.ToString("0.000") + "%以下");
                Settings.ErrorCorrectionMergeThresholdPercent = GUILayout.HorizontalSlider(
                    Settings.ErrorCorrectionMergeThresholdPercent, 0.005f, 0.5f);
                GUILayout.Label("許容RMS: " + Settings.ErrorCorrectionMaxRmsMs.ToString("0.0") + "ms");
                Settings.ErrorCorrectionMaxRmsMs = GUILayout.HorizontalSlider(
                    Settings.ErrorCorrectionMaxRmsMs, 10f, 150f);
                GUILayout.Label("無視する最小補正量: " +
                                Settings.ErrorCorrectionMinChangePercent.ToString("0.000") + "%未満");
                Settings.ErrorCorrectionMinChangePercent = GUILayout.HorizontalSlider(
                    Settings.ErrorCorrectionMinChangePercent, 0f, 0.2f);
                GUILayout.Label("1区間当たりの最大補正率: ±" +
                                Settings.ErrorCorrectionMaxPercent.ToString("0.00") + "%");
                Settings.ErrorCorrectionMaxPercent = GUILayout.HorizontalSlider(
                    Settings.ErrorCorrectionMaxPercent, 0.05f, 5f);
                GUILayout.Label("1回の適用強度: " +
                                Settings.ErrorCorrectionApplyStrengthPercent.ToString("0") + "%");
                Settings.ErrorCorrectionApplyStrengthPercent = GUILayout.HorizontalSlider(
                    Settings.ErrorCorrectionApplyStrengthPercent, 10f, 100f);
                GUILayout.Label("この実験機能だけは、安全のため各補正区間末尾で元BPMへ復元します。");
            }
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            try
            {
                NormalizeSettings();
                Settings.Save(modEntry);
            }
            catch (Exception ex)
            {
                LogException("Saving settings failed", ex);
            }
        }

        internal static void SaveSettingsNow()
        {
            if (currentModEntry == null || Settings == null) return;
            try
            {
                NormalizeSettings();
                Settings.Save(currentModEntry);
            }
            catch (Exception ex)
            {
                LogException("Saving settings failed", ex);
            }
        }

        private static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            Enabled = false;
            try { AudioSyncPrewarmRuntime.Shutdown(); } catch { }
            try { TimingTrackerRuntime.Shutdown(); } catch { }
            try { PlayErrorCorrectionRuntime.Shutdown(); } catch { }
            try { ConductorDriftRuntime.Shutdown(); } catch { }
            try { DspProbeCueRuntime.Shutdown(); } catch { }
            try { AudioSyncLifecycleRuntime.Shutdown(); } catch { }
            try { CheckpointStartHandshakeRuntime.Shutdown(); } catch { }
            try { AudioSyncRuntime.Shutdown(); } catch { }
            try { if (harmony != null) harmony.UnpatchAll(modEntry.Info.Id); } catch { }
            if (host != null)
            {
                Object.Destroy(host);
                host = null;
            }
            currentModEntry = null;
            return true;
        }

        private static void CleanupAfterFailedLoad(UnityModManager.ModEntry modEntry)
        {
            try { AudioSyncPrewarmRuntime.Shutdown(); } catch { }
            try { AudioSyncLifecycleRuntime.Shutdown(); } catch { }
            try { CheckpointStartHandshakeRuntime.Shutdown(); } catch { }
            try { ConductorDriftRuntime.Shutdown(); } catch { }
            try { DspProbeCueRuntime.Shutdown(); } catch { }
            try { if (harmony != null) harmony.UnpatchAll(modEntry.Info.Id); } catch { }
            try
            {
                if (host != null)
                {
                    Object.Destroy(host);
                    host = null;
                }
            }
            catch { }
        }

        private static void LogException(string context, Exception ex)
        {
            if (Logger == null) return;
            Logger.Error(context + Environment.NewLine + FormatException(ex));
        }

        private static string FormatException(Exception ex)
        {
            StringBuilder builder = new StringBuilder();
            int depth = 0;
            while (ex != null && depth < 12)
            {
                builder.Append(' ', depth * 2)
                    .Append(ex.GetType().FullName)
                    .Append(": ")
                    .AppendLine(ex.Message);
                if (!string.IsNullOrEmpty(ex.StackTrace))
                    builder.AppendLine(ex.StackTrace);
                ex = ex.InnerException;
                depth++;
                if (ex != null) builder.AppendLine("Inner exception:");
            }
            return builder.ToString();
        }
    }
}
