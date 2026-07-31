using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Compares the chart clock (dspTimeSong) with the real AudioSource playhead.
    /// A baseline is learned at the beginning of each playback so fixed driver/buffer
    /// latency is ignored. Only a change from that baseline is treated as drift.
    ///
    /// Design references:
    /// - ADOFAI Access: floor DSP due time = dspTimeSongPosZero + entryTimePitchAdj
    /// - Quartz: explicit audio lifecycle cleanup and hit-timeline capture boundaries
    /// This implementation is independent and does not copy either project's source.
    /// </summary>
    internal static class ConductorDriftRuntime
    {
        private enum MonitorState
        {
            Disabled,
            Waiting,
            Warmup,
            Monitoring,
            Suspected,
            Correcting,
            Cooldown
        }

        private static readonly FieldInfo DspTimeSongField =
            AccessTools.Field(typeof(scrConductor), "dspTimeSong");
        private static readonly FieldInfo HitSoundsDataField =
            AccessTools.Field(typeof(scrConductor), "hitSoundsData");
        private static readonly Type HitSoundsDataType =
            typeof(scrConductor).GetNestedType("HitSoundsData", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo HitSoundTimeField =
            HitSoundsDataType == null ? null : AccessTools.Field(HitSoundsDataType, "time");

        private static readonly List<double> warmupOffsets = new List<double>();
        private static readonly List<double> recentDrifts = new List<double>();

        private static MonitorState state;
        private static scrConductor currentConductor;
        private static AudioSource currentSource;
        private static AudioClip currentClip;
        private static bool sessionActive;
        private static double sessionStartedDsp;
        private static double nextSampleDsp;
        private static double baselineOffsetSeconds;
        private static double currentRawOffsetSeconds;
        private static double currentDriftSeconds;
        private static double filteredDriftSeconds;
        private static double maxObservedDriftSeconds;
        private static double lastActualSeconds = -1d;
        private static double lastPredictedSeconds;
        private static float lastPitch = -1f;
        private static int consecutiveOutOfRange;
        private static int correctionCount;
        private static double lastCorrectionDsp = -1000d;
        private static int lastSuspectedSequence = -1;
        private static string status = "待機中";
        private static string lastCorrection = "-";
        private static string csvPath = string.Empty;
        private static bool internalTimelineRefresh;
        private static bool suspended;

        private static readonly object csvLock = new object();
        private static readonly StringBuilder csvBuffer = new StringBuilder(2048);
        private static double nextCsvFlushDsp;
        private static bool csvHeaderPrepared;

        private static int nextFloorId = -1;
        private static double nextFloorDueDsp;
        private static double nextFloorDueDeltaMs;
        private static int capturedHitSoundCount;
        private static double firstCapturedHitDsp;
        private static double lastCapturedHitDsp;

        internal static string Status { get { return status; } }
        internal static bool SessionActive { get { return sessionActive; } }
        internal static double BaselineOffsetMs { get { return baselineOffsetSeconds * 1000d; } }
        internal static double CurrentRawOffsetMs { get { return currentRawOffsetSeconds * 1000d; } }
        internal static double CurrentDriftMs { get { return currentDriftSeconds * 1000d; } }
        internal static double FilteredDriftMs { get { return filteredDriftSeconds * 1000d; } }
        internal static double MaxObservedDriftMs { get { return maxObservedDriftSeconds * 1000d; } }
        internal static double ActualSourceSeconds { get { return lastActualSeconds; } }
        internal static double PredictedSourceSeconds { get { return lastPredictedSeconds; } }
        internal static int ConsecutiveOutOfRange { get { return consecutiveOutOfRange; } }
        internal static int CorrectionCount { get { return correctionCount; } }
        internal static string LastCorrection { get { return lastCorrection; } }
        internal static int NextFloorId { get { return nextFloorId; } }
        internal static double NextFloorDueDsp { get { return nextFloorDueDsp; } }
        internal static double NextFloorDueDeltaMs { get { return nextFloorDueDeltaMs; } }
        internal static int CapturedHitSoundCount { get { return capturedHitSoundCount; } }
        internal static double FirstCapturedHitDsp { get { return firstCapturedHitDsp; } }
        internal static double LastCapturedHitDsp { get { return lastCapturedHitDsp; } }
        internal static bool IsInternalTimelineRefresh { get { return internalTimelineRefresh; } }

        internal static void Initialize()
        {
            ResetAll("初期化");
            if (DspTimeSongField == null && Main.Logger != null)
            {
                Main.Logger.Warning("dspTimeSongフィールドを取得できません。ドリフト監視は診断表示のみ利用不可です。");
            }
        }

        internal static void Prewarm()
        {
            // Accessing the cached metadata here forces the type/reflection path before playback.
            bool reflectionReady = DspTimeSongField != null;
            if (HitSoundsDataField != null && HitSoundTimeField != null) reflectionReady = true;
            if (Main.Settings != null && Main.Settings.EnableDriftCsvLog) PrepareCsvLog();
            if (!reflectionReady && Main.Logger != null)
                Main.Logger.Warning("AudioSync prewarm: conductor reflection metadata is incomplete.");
        }

        internal static void Update()
        {
            FlushCsvIfDue(false);
            AudioSyncSettings settings = Main.Settings;
            if (!Main.Enabled || settings == null || !settings.EnableDriftMonitor)
            {
                if (sessionActive) EndSession("監視OFF");
                state = MonitorState.Disabled;
                status = "監視OFF";
                return;
            }

            scrConductor conductor = scrConductor.instance;
            AudioSource source = conductor == null ? null : conductor.song;
            AudioClip clip = source == null ? null : source.clip;
            if (conductor == null || source == null || clip == null || clip.frequency <= 0 || DspTimeSongField == null)
            {
                if (sessionActive) EndSession("音源待ち");
                state = MonitorState.Waiting;
                status = "再生音源待ち";
                return;
            }

            bool paused = AudioListener.pause || !Application.isFocused;
            scrController controller = scrController.instance;
            if (controller != null && controller.paused) paused = true;
            if (paused)
            {
                suspended = true;
                status = "一時停止中（監視保留）";
                return;
            }

            if (suspended)
            {
                suspended = false;
                if (source.isPlaying)
                {
                    BeginSession(conductor, source, clip, "一時停止/フォーカス復帰");
                }
            }

            if (!source.isPlaying)
            {
                if (sessionActive) EndSession("再生停止");
                state = MonitorState.Waiting;
                status = "再生待ち";
                return;
            }

            if (!sessionActive || conductor != currentConductor || source != currentSource || clip != currentClip)
            {
                BeginSession(conductor, source, clip, "再生開始");
            }

            float pitch = source.pitch;
            if (pitch <= 0.0001f)
            {
                status = "pitchが0のため監視保留";
                return;
            }
            if (lastPitch > 0f && Math.Abs(pitch - lastPitch) > 0.0001f)
            {
                BeginSession(conductor, source, clip, "pitch変更 " + lastPitch.ToString("0.###", CultureInfo.InvariantCulture) +
                    "→" + pitch.ToString("0.###", CultureInfo.InvariantCulture));
            }
            lastPitch = pitch;

            double nowDsp = conductor.dspTime;
            double interval = Math.Max(0.02d, Math.Min(1d, settings.DriftSampleIntervalMs / 1000d));
            if (nowDsp < nextSampleDsp) return;
            nextSampleDsp = nowDsp + interval;
            UpdateNextFloorDue(conductor);

            double actual = ReadSourceSeconds(source, clip);
            if (!IsFinite(actual) || actual < 0d) return;
            if (lastActualSeconds >= 0d && actual < lastActualSeconds - 0.25d)
            {
                BeginSession(conductor, source, clip, "音源シーク検出");
                return;
            }

            double origin = ReadDspTimeSong(conductor);
            if (!IsFinite(origin)) return;
            double predicted = (nowDsp - origin) * pitch;
            if (!IsFinite(predicted)) return;

            lastActualSeconds = actual;
            lastPredictedSeconds = predicted;
            currentRawOffsetSeconds = actual - predicted;

            double warmupSeconds = Math.Max(0.25d, Math.Min(10d, settings.DriftWarmupSeconds));
            if (state == MonitorState.Warmup || warmupOffsets.Count < 6)
            {
                if (actual > 0.02d)
                {
                    warmupOffsets.Add(currentRawOffsetSeconds);
                    if (warmupOffsets.Count > 64) warmupOffsets.RemoveAt(0);
                }

                if (nowDsp - sessionStartedDsp >= warmupSeconds && warmupOffsets.Count >= 6)
                {
                    baselineOffsetSeconds = Median(warmupOffsets);
                    recentDrifts.Clear();
                    consecutiveOutOfRange = 0;
                    state = MonitorState.Monitoring;
                    status = "監視中（基準 " + (baselineOffsetSeconds * 1000d).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "ms）";
                    LogCsv("baseline", conductor, actual, predicted, 0d, 0d);
                }
                else
                {
                    status = "基準学習中 " + warmupOffsets.Count + "サンプル";
                }
                return;
            }

            currentDriftSeconds = currentRawOffsetSeconds - baselineOffsetSeconds;
            recentDrifts.Add(currentDriftSeconds);
            if (recentDrifts.Count > 5) recentDrifts.RemoveAt(0);
            filteredDriftSeconds = Median(recentDrifts);
            maxObservedDriftSeconds = Math.Max(maxObservedDriftSeconds, Math.Abs(filteredDriftSeconds));

            double threshold = Math.Max(0.005d, Math.Min(0.5d, settings.DriftThresholdMs / 1000d));
            if (Math.Abs(filteredDriftSeconds) >= threshold)
            {
                consecutiveOutOfRange++;
                state = MonitorState.Suspected;
                status = "ドリフト疑い " + (filteredDriftSeconds * 1000d).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) +
                    "ms ×" + consecutiveOutOfRange;

                int sequence = ResolveCurrentSequence();
                if (consecutiveOutOfRange == 1 || sequence != lastSuspectedSequence)
                {
                    lastSuspectedSequence = sequence;
                    LogCsv("suspected", conductor, actual, predicted, currentDriftSeconds, filteredDriftSeconds);
                }

                int needed = Math.Max(2, Math.Min(30, settings.DriftConsecutiveSamples));
                double cooldown = Math.Max(0.5d, Math.Min(30d, settings.DriftCorrectionCooldownSeconds));
                double maxCorrection = Math.Max(threshold, Math.Min(1d, settings.DriftMaxCorrectionMs / 1000d));
                if (settings.AutoCorrectDrift && consecutiveOutOfRange >= needed &&
                    nowDsp - lastCorrectionDsp >= cooldown && Math.Abs(filteredDriftSeconds) <= maxCorrection)
                {
                    Correct(conductor, filteredDriftSeconds, "自動");
                }
            }
            else
            {
                consecutiveOutOfRange = 0;
                lastSuspectedSequence = -1;
                state = nowDsp - lastCorrectionDsp < Math.Max(0.5d, settings.DriftCorrectionCooldownSeconds)
                    ? MonitorState.Cooldown
                    : MonitorState.Monitoring;
                status = state == MonitorState.Cooldown
                    ? "補正後クールダウン"
                    : "監視中 " + (filteredDriftSeconds * 1000d).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "ms";
            }
        }

        internal static bool TryCorrectNow()
        {
            if (!sessionActive || currentConductor == null || Math.Abs(filteredDriftSeconds) < 0.001d)
            {
                status = "手動補正できるドリフトがありません";
                return false;
            }

            AudioSyncSettings settings = Main.Settings;
            double maxCorrection = settings == null ? 0.15d :
                Math.Max(0.005d, Math.Min(1d, settings.DriftMaxCorrectionMs / 1000d));
            if (Math.Abs(filteredDriftSeconds) > maxCorrection)
            {
                status = "安全上限を超えるため手動補正を拒否: " +
                    (filteredDriftSeconds * 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "ms";
                return false;
            }

            Correct(currentConductor, filteredDriftSeconds, "手動");
            return true;
        }

        internal static void NotifyHitTimelineRebuilt(scrConductor conductor)
        {
            capturedHitSoundCount = 0;
            firstCapturedHitDsp = 0d;
            lastCapturedHitDsp = 0d;
            if (conductor == null || HitSoundsDataField == null) return;

            try
            {
                IList list = HitSoundsDataField.GetValue(conductor) as IList;
                if (list == null || list.Count == 0) return;
                capturedHitSoundCount = list.Count;
                if (HitSoundTimeField != null)
                {
                    object first = list[0];
                    object last = list[list.Count - 1];
                    if (first != null) firstCapturedHitDsp = Convert.ToDouble(HitSoundTimeField.GetValue(first), CultureInfo.InvariantCulture);
                    if (last != null) lastCapturedHitDsp = Convert.ToDouble(HitSoundTimeField.GetValue(last), CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                if (Main.Logger != null) Main.Logger.Warning("ヒット音タイムライン診断の取得に失敗: " + ex.Message);
            }
        }

        internal static void NotifyLifecycleStop(string reason)
        {
            EndSession(reason ?? "停止");
        }

        internal static void ResetBaseline(string reason)
        {
            if (currentConductor != null && currentSource != null && currentClip != null && currentSource.isPlaying)
                BeginSession(currentConductor, currentSource, currentClip, reason ?? "基準再学習");
            else
                EndSession(reason ?? "基準リセット");
        }

        internal static void ResetAll(string reason)
        {
            EndSession(reason ?? "リセット");
            correctionCount = 0;
            lastCorrection = "-";
            maxObservedDriftSeconds = 0d;
            FlushCsvIfDue(true);
            csvPath = string.Empty;
            csvHeaderPrepared = false;
            lock (csvLock) csvBuffer.Length = 0;
            capturedHitSoundCount = 0;
            firstCapturedHitDsp = 0d;
            lastCapturedHitDsp = 0d;
        }

        internal static void Shutdown()
        {
            EndSession("終了");
            FlushCsvIfDue(true);
        }

        private static void BeginSession(scrConductor conductor, AudioSource source, AudioClip clip, string reason)
        {
            currentConductor = conductor;
            currentSource = source;
            currentClip = clip;
            sessionActive = true;
            sessionStartedDsp = conductor == null ? AudioSettings.dspTime : conductor.dspTime;
            nextSampleDsp = sessionStartedDsp;
            baselineOffsetSeconds = 0d;
            currentRawOffsetSeconds = 0d;
            currentDriftSeconds = 0d;
            filteredDriftSeconds = 0d;
            lastActualSeconds = -1d;
            lastPredictedSeconds = 0d;
            lastPitch = source == null ? -1f : source.pitch;
            consecutiveOutOfRange = 0;
            lastSuspectedSequence = -1;
            warmupOffsets.Clear();
            recentDrifts.Clear();
            suspended = false;
            state = MonitorState.Warmup;
            status = (reason ?? "再生開始") + "：基準学習中";
            DspProbeCueRuntime.ResetSchedule("new playback");
        }

        private static void EndSession(string reason)
        {
            sessionActive = false;
            suspended = false;
            currentConductor = null;
            currentSource = null;
            currentClip = null;
            warmupOffsets.Clear();
            recentDrifts.Clear();
            consecutiveOutOfRange = 0;
            lastActualSeconds = -1d;
            lastPredictedSeconds = 0d;
            nextFloorId = -1;
            nextFloorDueDsp = 0d;
            nextFloorDueDeltaMs = 0d;
            state = MonitorState.Waiting;
            status = reason ?? "待機中";
        }

        private static void Correct(scrConductor conductor, double driftSeconds, string mode)
        {
            if (conductor == null || conductor.song == null || conductor.song.pitch <= 0.0001f || DspTimeSongField == null)
                return;

            state = MonitorState.Correcting;
            double oldOrigin = ReadDspTimeSong(conductor);
            double shift = -driftSeconds / conductor.song.pitch;
            double newOrigin = oldOrigin + shift;

            try
            {
                DspTimeSongField.SetValue(conductor, newOrigin);
                correctionCount++;
                lastCorrectionDsp = conductor.dspTime;
                lastCorrection = mode + " " + (driftSeconds * 1000d).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) +
                    "ms / origin " + (shift * 1000d).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "ms";
                status = "補正実行: " + lastCorrection;
                LogCsv("correct-" + mode, conductor, lastActualSeconds, lastPredictedSeconds,
                    currentDriftSeconds, filteredDriftSeconds);

                DspProbeCueRuntime.ResetSchedule("clock corrected");
                if (Main.Settings != null && Main.Settings.RebuildHitTimelineAfterDriftCorrection)
                {
                    try
                    {
                        internalTimelineRefresh = true;
                        conductor.PlayHitTimes();
                    }
                    catch (Exception ex)
                    {
                        if (Main.Logger != null) Main.Logger.Warning("補正後のヒット音タイムライン再構築に失敗: " + ex.Message);
                    }
                    finally
                    {
                        internalTimelineRefresh = false;
                    }
                }

                currentDriftSeconds = 0d;
                filteredDriftSeconds = 0d;
                recentDrifts.Clear();
                consecutiveOutOfRange = 0;
                state = MonitorState.Cooldown;
            }
            catch (Exception ex)
            {
                status = "ドリフト補正失敗: " + ex.Message;
                if (Main.Logger != null) Main.Logger.Error(ex.ToString());
            }
        }

        private static double ReadDspTimeSong(scrConductor conductor)
        {
            try
            {
                object value = DspTimeSongField.GetValue(conductor);
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return double.NaN;
            }
        }

        private static double ReadSourceSeconds(AudioSource source, AudioClip clip)
        {
            try
            {
                return source.timeSamples / (double)clip.frequency;
            }
            catch
            {
                return source.time;
            }
        }

        private static void UpdateNextFloorDue(scrConductor conductor)
        {
            nextFloorId = -1;
            nextFloorDueDsp = 0d;
            nextFloorDueDeltaMs = 0d;
            try
            {
                if (ADOBase.lm == null || ADOBase.lm.listFloors == null || ADOBase.lm.listFloors.Count == 0)
                    return;
                scrController controller = scrController.instance;
                int current = controller == null ? 0 : controller.currentSeqID;
                int candidate = Math.Max(1, Math.Min(ADOBase.lm.listFloors.Count - 1, current + 1));
                scrFloor floor = ADOBase.lm.listFloors[candidate];
                if (floor == null) return;
                nextFloorId = candidate;
                nextFloorDueDsp = conductor.dspTimeSongPosZero + floor.entryTimePitchAdj;
                nextFloorDueDeltaMs = (nextFloorDueDsp - conductor.dspTime) * 1000d;
            }
            catch
            {
                nextFloorId = -1;
            }
        }

        private static int ResolveCurrentSequence()
        {
            try
            {
                scrController controller = scrController.instance;
                return controller == null ? -1 : controller.currentSeqID;
            }
            catch
            {
                return -1;
            }
        }

        private static void LogCsv(string action, scrConductor conductor, double actual, double predicted,
            double drift, double filtered)
        {
            AudioSyncSettings settings = Main.Settings;
            if (settings == null || !settings.EnableDriftCsvLog || string.IsNullOrEmpty(Main.ModPath)) return;

            try
            {
                PrepareCsvLog();
                if (string.IsNullOrEmpty(csvPath)) return;

                int floor = ResolveCurrentSequence();
                float pitch = conductor != null && conductor.song != null ? conductor.song.pitch : 0f;
                string line = string.Join(",", new string[]
                {
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    EscapeCsv(action),
                    floor.ToString(CultureInfo.InvariantCulture),
                    (conductor == null ? AudioSettings.dspTime : conductor.dspTime).ToString("R", CultureInfo.InvariantCulture),
                    pitch.ToString("R", CultureInfo.InvariantCulture),
                    actual.ToString("R", CultureInfo.InvariantCulture),
                    predicted.ToString("R", CultureInfo.InvariantCulture),
                    (baselineOffsetSeconds * 1000d).ToString("R", CultureInfo.InvariantCulture),
                    (drift * 1000d).ToString("R", CultureInfo.InvariantCulture),
                    (filtered * 1000d).ToString("R", CultureInfo.InvariantCulture),
                    nextFloorId.ToString(CultureInfo.InvariantCulture),
                    nextFloorDueDeltaMs.ToString("R", CultureInfo.InvariantCulture)
                });
                lock (csvLock)
                {
                    csvBuffer.Append(line).Append("\r\n");
                }
            }
            catch (Exception ex)
            {
                if (Main.Logger != null) Main.Logger.Warning("ドリフトCSVバッファ追加失敗: " + ex.Message);
            }
        }

        private static void PrepareCsvLog()
        {
            AudioSyncSettings settings = Main.Settings;
            if (settings == null || !settings.EnableDriftCsvLog || string.IsNullOrEmpty(Main.ModPath)) return;
            if (csvHeaderPrepared && !string.IsNullOrEmpty(csvPath)) return;

            try
            {
                string directory = Path.Combine(Main.ModPath, "Logs");
                Directory.CreateDirectory(directory);
                if (string.IsNullOrEmpty(csvPath))
                    csvPath = Path.Combine(directory, "audio-drift-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
                if (!File.Exists(csvPath) || new FileInfo(csvPath).Length == 0)
                {
                    File.WriteAllText(csvPath,
                        "utc,action,floor,dsp,pitch,actual_source_s,predicted_source_s,baseline_ms,drift_ms,filtered_ms,next_floor,next_floor_due_delta_ms\r\n");
                }
                csvHeaderPrepared = true;
                nextCsvFlushDsp = AudioSettings.dspTime + GetCsvFlushInterval();
            }
            catch (Exception ex)
            {
                csvHeaderPrepared = false;
                if (Main.Logger != null) Main.Logger.Warning("ドリフトCSV事前準備失敗: " + ex.Message);
            }
        }

        private static void FlushCsvIfDue(bool force)
        {
            AudioSyncSettings settings = Main.Settings;
            if (!force && (settings == null || !settings.EnableDriftCsvLog)) return;
            if (!force && AudioSettings.dspTime < nextCsvFlushDsp) return;

            string pending;
            lock (csvLock)
            {
                if (csvBuffer.Length == 0)
                {
                    nextCsvFlushDsp = AudioSettings.dspTime + GetCsvFlushInterval();
                    return;
                }
                pending = csvBuffer.ToString();
                csvBuffer.Length = 0;
            }

            try
            {
                if (settings != null && settings.EnableDriftCsvLog) PrepareCsvLog();
                if (!string.IsNullOrEmpty(csvPath)) File.AppendAllText(csvPath, pending);
            }
            catch (Exception ex)
            {
                lock (csvLock) csvBuffer.Insert(0, pending);
                if (Main.Logger != null) Main.Logger.Warning("ドリフトCSV書き込み失敗: " + ex.Message);
            }
            finally
            {
                nextCsvFlushDsp = AudioSettings.dspTime + GetCsvFlushInterval();
            }
        }

        private static double GetCsvFlushInterval()
        {
            AudioSyncSettings settings = Main.Settings;
            float seconds = settings == null ? 1f : settings.DriftCsvFlushIntervalSeconds;
            return Math.Max(0.25d, Math.Min(10d, seconds));
        }

        private static string EscapeCsv(string value)
        {
            if (value == null) return string.Empty;
            if (value.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static double Median(List<double> values)
        {
            if (values == null || values.Count == 0) return 0d;
            double[] copy = values.ToArray();
            Array.Sort(copy);
            int middle = copy.Length / 2;
            return copy.Length % 2 == 0 ? (copy[middle - 1] + copy[middle]) * 0.5d : copy[middle];
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
