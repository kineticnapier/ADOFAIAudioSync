using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Keeps selected-floor playback on the exact checkpoint chosen by the editor.
    ///
    /// Stock ADOFAI seeks the music and schedules it for "now". On some audio backends
    /// the first observable timeSamples value can jump hundreds of milliseconds forward.
    /// Earlier AudioSync builds either released the planets at that advanced position or
    /// visually advanced them while the chart clock was frozen.
    ///
    /// v0.9.11 schedules the already-seeked AudioSource a short time in the future, then
    /// compares the observed playhead with the sample expected at that exact DSP time.
    /// Normal frame time spent confirming playback is therefore not mistaken for a start
    /// offset. The final chart origin is derived from the known reservation instead of a
    /// frame-dependent AudioSettings.dspTime/timeSamples snapshot.
    /// </summary>
    internal static class CheckpointStartHandshakeRuntime
    {
        private static readonly FieldInfo DspTimeSongField =
            AccessTools.Field(typeof(scrConductor), "dspTimeSong");

        private enum HandshakeState
        {
            Idle,
            Priming,
            WaitingForScheduledStart,
            WaitingForStablePlayhead,
            Aligned,
            TimedOut,
            Failed,
            Cancelled
        }

        private static HandshakeState state;
        private static scrConductor conductor;
        private static AudioSource source;
        private static AudioClip clip;
        private static Coroutine coroutine;
        private static int generation;

        private static double requestedLogicalSeconds;
        private static int requestedSample;
        private static int lastObservedSample;
        private static int consecutiveMovingFrames;
        private static int stagnantFramesAfterMotion;
        private static double attemptStartRealtime;
        private static double scheduledStartDsp;
        private static double scheduledPitch;
        private static double scheduledClipSeconds;
        private static double lastStartDelayMs;
        private static double lastAnchorAdjustmentMs;
        private static double lastScheduleResidualMs;
        private static double lastExpectedSample;
        private static int completionCount;
        private static int timeoutCount;
        private static int retryCount;
        private static int attemptNumber;
        private static int lastRequestedSample;
        private static int lastActualSample;
        private static bool usedTimeSamplesForSeek;

        private static string status = "待機中";
        private static string lastError = string.Empty;

        internal static bool IsActive
        {
            get
            {
                return state == HandshakeState.Priming ||
                       state == HandshakeState.WaitingForScheduledStart ||
                       state == HandshakeState.WaitingForStablePlayhead;
            }
        }

        internal static string Status { get { return status; } }
        internal static string LastError { get { return lastError; } }
        internal static double LastStartDelayMs { get { return lastStartDelayMs; } }
        internal static double LastAnchorAdjustmentMs { get { return lastAnchorAdjustmentMs; } }
        internal static double LastScheduleResidualMs { get { return lastScheduleResidualMs; } }
        internal static int ExpectedSample { get { return (int)Math.Round(lastExpectedSample); } }
        internal static int CompletionCount { get { return completionCount; } }
        internal static int TimeoutCount { get { return timeoutCount; } }
        internal static int RetryCount { get { return retryCount; } }
        internal static int ConsecutiveMovingFrames { get { return consecutiveMovingFrames; } }
        internal static int RequestedSample { get { return IsActive ? requestedSample : lastRequestedSample; } }
        internal static int CurrentSample { get { return IsActive ? ReadSampleSafe(source) : lastActualSample; } }
        internal static bool UsedTimeSamplesForSeek { get { return usedTimeSamplesForSeek; } }

        internal static void Initialize()
        {
            Reset("初期化");
        }

        internal static bool ShouldRunStockScrub(scrConductor instance, double newTime)
        {
            if (!ShouldIntercept(instance, newTime))
            {
                return true;
            }

            CancelActive("新しい途中再生", false);

            AudioSource nextSource = instance.song;
            AudioClip nextClip = nextSource == null ? null : nextSource.clip;
            if (nextSource == null || nextClip == null || nextClip.frequency <= 0)
            {
                status = "音源情報不足: 本体Scrubを使用";
                return true;
            }

            try
            {
                AudioListener.pause = true;
                if (AudioManager.Instance != null)
                {
                    AudioManager.Instance.StopAllSounds();
                }

                conductor = instance;
                source = nextSource;
                clip = nextClip;
                requestedLogicalSeconds = newTime;
                lastStartDelayMs = 0d;
                lastAnchorAdjustmentMs = 0d;
                lastScheduleResidualMs = 0d;
                lastExpectedSample = 0d;
                attemptNumber = 0;
                consecutiveMovingFrames = 0;
                stagnantFramesAfterMotion = 0;
                lastError = string.Empty;

                BeginScheduledAttempt(false);

                // Keep the logical chart at the exact stock scrub start while the future
                // AudioSource reservation is pending. This is also what Checkpoint_Enter
                // observes immediately after ScrubMusicToTime returns.
                PinChartAtRequestedLogical(instance, instance.dspTime);
                instance.lastHit = newTime;

                generation++;
                int token = generation;
                coroutine = instance.StartCoroutine(HandshakeCoroutine(token));
                return false;
            }
            catch (Exception ex)
            {
                AudioListener.pause = false;
                state = HandshakeState.Failed;
                lastError = ex.GetType().Name + ": " + ex.Message;
                status = "途中再生予約失敗: 本体Scrubへフォールバック";
                ClearReferences(false);
                if (Main.Logger != null)
                {
                    Main.Logger.Warning("Checkpoint scheduled start setup failed: " + ex);
                }
                return true;
            }
        }

        /// <summary>
        /// Pin the chart clock until the scheduled AudioSource start is confirmed.
        /// </summary>
        internal static void BeforeConductorUpdate(scrConductor instance)
        {
            if (!IsActive || instance == null || instance != conductor)
            {
                return;
            }

            if (!IsSessionValid())
            {
                FailAndRelease("途中再生中にAudioSourceが失われました");
                return;
            }

            double nowDsp = AudioSettings.dspTime;
            PinChartAtRequestedLogical(instance, nowDsp);

            if (state == HandshakeState.Priming)
            {
                status = "途中再生: 予約準備中";
                return;
            }

            if (state == HandshakeState.WaitingForScheduledStart)
            {
                double remainingMs = Math.Max(0d, (scheduledStartDsp - nowDsp) * 1000d);
                status = "途中再生: DSP予約待ち " + remainingMs.ToString("0") + "ms";
                if (nowDsp + 0.0005d < scheduledStartDsp)
                {
                    return;
                }
                state = HandshakeState.WaitingForStablePlayhead;
                lastObservedSample = ReadSampleSafe(source);
                consecutiveMovingFrames = 0;
                stagnantFramesAfterMotion = 0;
            }

            if (state != HandshakeState.WaitingForStablePlayhead)
            {
                return;
            }

            int currentSample = ReadSampleSafe(source);
            int delta = currentSample - lastObservedSample;
            if (!AudioListener.pause && source.isPlaying && delta > 0)
            {
                consecutiveMovingFrames++;
                stagnantFramesAfterMotion = 0;
            }
            else if (consecutiveMovingFrames > 0)
            {
                stagnantFramesAfterMotion++;
                if (stagnantFramesAfterMotion > 1)
                {
                    consecutiveMovingFrames = 0;
                    stagnantFramesAfterMotion = 0;
                }
            }
            lastObservedSample = currentSample;

            int required = GetRequiredMovingFrames();
            status = "途中再生: 開始サンプル確認 " + consecutiveMovingFrames + "/" + required;
            if (consecutiveMovingFrames < required)
            {
                return;
            }

            double observedDspBefore = AudioSettings.dspTime;
            currentSample = ReadSampleSafe(source);
            double observedDspAfter = AudioSettings.dspTime;
            double observedDsp = (observedDspBefore + observedDspAfter) * 0.5d;
            double pitch = GetAttemptPitch();
            double expectedSample = GetExpectedSampleAtDsp(observedDsp);
            double residualMs = SampleDeltaToRealMilliseconds(currentSample - expectedSample, pitch);
            lastExpectedSample = expectedSample;
            lastScheduleResidualMs = residualMs;

            if (Math.Abs(residualMs) > GetMaxScheduleResidualMs())
            {
                if (attemptNumber < GetMaxRetryCount())
                {
                    retryCount++;
                    attemptNumber++;
                    status = "途中再生: 予約残差 " + FormatSignedMilliseconds(residualMs) +
                             " のため再予約";
                    if (Main.Logger != null)
                    {
                        Main.Logger.Warning(
                            "Checkpoint schedule residual was " +
                            residualMs.ToString("+0.0;-0.0;0.0") +
                            " ms on attempt " + attemptNumber + "; rescheduling before release.");
                    }
                    BeginScheduledAttempt(true);
                    return;
                }

                // Final attempt: do not hang forever. Align to the observed playhead, but make
                // the failure explicit in the overlay/log. Normal cases should be caught by
                // the future reservation and never reach this branch.
                lastError = "開始サンプルの予約残差が許容値を超えました: " +
                            residualMs.ToString("+0.0;-0.0;0.0") + "ms";
                status = "途中再生: 再予約上限、実サンプルへフォールバック";
            }

            AlignAndRelease(
                instance,
                observedDsp,
                currentSample,
                Math.Abs(residualMs) > GetMaxScheduleResidualMs());
        }

        internal static void NotifyStop(string reason)
        {
            if (!IsActive && coroutine == null)
            {
                return;
            }
            CancelActive(reason, true);
        }

        internal static void Reset(string reason)
        {
            CancelActive(reason, true);
            state = HandshakeState.Idle;
            status = reason ?? "待機中";
            lastError = string.Empty;
            lastStartDelayMs = 0d;
            lastAnchorAdjustmentMs = 0d;
            lastScheduleResidualMs = 0d;
            lastExpectedSample = 0d;
            consecutiveMovingFrames = 0;
            stagnantFramesAfterMotion = 0;
        }

        internal static void Shutdown()
        {
            CancelActive("mod unload", true);
            state = HandshakeState.Idle;
            status = "終了";
        }

        private static IEnumerator HandshakeCoroutine(int token)
        {
            while (TokenIsCurrent(token))
            {
                if (state == HandshakeState.Priming)
                {
                    // Preserve the two-frame initialization window used by stock DesyncFix.
                    yield return null;
                    yield return null;
                    if (!TokenIsCurrent(token) || !IsSessionValid())
                    {
                        yield break;
                    }
                    AudioListener.pause = false;
                    lastObservedSample = ReadSampleSafe(source);
                    consecutiveMovingFrames = 0;
                    stagnantFramesAfterMotion = 0;
                    state = HandshakeState.WaitingForScheduledStart;
                }

                if (state == HandshakeState.WaitingForScheduledStart ||
                    state == HandshakeState.WaitingForStablePlayhead)
                {
                    double timeoutSeconds = GetTimeoutSeconds();
                    if (Time.realtimeSinceStartupAsDouble - attemptStartRealtime >= timeoutSeconds)
                    {
                        HandleTimeout();
                    }
                    yield return null;
                    continue;
                }

                break;
            }

            if (!TokenIsCurrent(token))
            {
                yield break;
            }

            if (state == HandshakeState.Aligned || state == HandshakeState.TimedOut)
            {
                RebuildHitTimelineOnce();
            }
            coroutine = null;
        }

        private static void BeginScheduledAttempt(bool retry)
        {
            if (!IsSessionValid())
            {
                throw new InvalidOperationException("Checkpoint audio session is no longer valid.");
            }

            AudioListener.pause = true;
            try { source.Stop(); } catch { }

            double countdownSeconds = GetStockScrubCountdownSeconds(conductor);
            double clipSeconds = requestedLogicalSeconds + conductor.addoffset - countdownSeconds;
            clipSeconds = ClampClipSeconds(clipSeconds, clip);
            int sample = SecondsToSample(clipSeconds, clip);

            bool sampleSeeked = TrySetTimeSamples(source, clip, sample);
            if (!sampleSeeked)
            {
                source.time = (float)clipSeconds;
            }
            int readbackSample = ReadSampleSafe(source);
            int seekTolerance = Math.Max(2, clip.frequency / 200);
            if (readbackSample >= 0 &&
                (clip.samples <= 0 || readbackSample < clip.samples) &&
                Math.Abs(readbackSample - sample) <= seekTolerance)
            {
                sample = readbackSample;
            }

            requestedSample = sample;
            lastRequestedSample = sample;
            lastObservedSample = sample;
            lastExpectedSample = sample;
            usedTimeSamplesForSeek = sampleSeeked;
            consecutiveMovingFrames = 0;
            stagnantFramesAfterMotion = 0;
            scheduledPitch = GetPitch();
            scheduledClipSeconds = SampleToSeconds(sample, clip);
            if (!IsFinite(scheduledClipSeconds))
            {
                scheduledClipSeconds = clipSeconds;
            }

            double leadMs = GetScheduleLeadMs() + (retry ? attemptNumber * 250d : 0d);
            scheduledStartDsp = AudioSettings.dspTime + leadMs / 1000d;
            source.PlayScheduled(scheduledStartDsp);

            attemptStartRealtime = Time.realtimeSinceStartupAsDouble;
            state = HandshakeState.Priming;
            status = "途中再生: " + (retry ? "再予約" : "予約") + " " + leadMs.ToString("0") + "ms先";
        }

        private static void AlignAndRelease(
            scrConductor instance,
            double nowDsp,
            int currentSample,
            bool fallback)
        {
            double pitch = GetAttemptPitch();
            double actualClipSeconds = SampleToSeconds(currentSample, clip);
            if (!IsFinite(actualClipSeconds))
            {
                actualClipSeconds = source.time;
            }

            double countdownSeconds = GetStockScrubCountdownSeconds(instance);
            double previousOrigin = ReadDspTimeSong(instance);
            double finalOrigin = fallback
                ? nowDsp - (actualClipSeconds + countdownSeconds) / pitch
                : scheduledStartDsp - (scheduledClipSeconds + countdownSeconds) / pitch;
            WriteDspTimeSong(instance, finalOrigin);

            lastActualSample = currentSample;
            lastStartDelayMs = Math.Max(0d,
                (Time.realtimeSinceStartupAsDouble - attemptStartRealtime) * 1000d);
            lastAnchorAdjustmentMs = (finalOrigin - previousOrigin) * 1000d;
            completionCount++;
            state = fallback ? HandshakeState.TimedOut : HandshakeState.Aligned;
            status = fallback
                ? "途中再生: 残差警告つきで実playheadへ整列"
                : "途中再生: DSP予約へ固定 / 残差 " +
                  FormatSignedMilliseconds(lastScheduleResidualMs);

            if (Main.Logger != null)
            {
                Main.Logger.Log(
                    "Checkpoint scheduled start " + (fallback ? "fallback" : "aligned") +
                    ": requestedSample=" + requestedSample +
                    ", expectedSample=" + lastExpectedSample.ToString("0.0") +
                    ", actualSample=" + currentSample +
                    ", scheduleResidual=" +
                    lastScheduleResidualMs.ToString("+0.0;-0.0;0.0") + " ms" +
                    ", attempt=" + (attemptNumber + 1) +
                    ", originAdjustment=" + lastAnchorAdjustmentMs.ToString("+0.0;-0.0;0.0") + " ms.");
            }
        }

        private static void HandleTimeout()
        {
            timeoutCount++;
            if (!IsSessionValid())
            {
                FailAndRelease("途中再生予約がtimeoutし、音源も失われました");
                return;
            }

            int currentSample = ReadSampleSafe(source);
            if (attemptNumber < GetMaxRetryCount())
            {
                retryCount++;
                attemptNumber++;
                status = "途中再生: 開始timeoutのため再予約";
                BeginScheduledAttempt(true);
                return;
            }

            lastActualSample = currentSample;
            lastError = "AudioSource.timeSamples did not begin normally before timeout.";
            AlignAndRelease(conductor, AudioSettings.dspTime, currentSample, true);
        }

        private static void RebuildHitTimelineOnce()
        {
            scrConductor activeConductor = conductor;
            if (activeConductor == null)
            {
                ClearReferences(false);
                return;
            }

            try
            {
                activeConductor.PlayHitTimes();
            }
            catch (Exception ex)
            {
                lastError = ex.GetType().Name + ": " + ex.Message;
                status += " / hit timeline失敗";
                if (Main.Logger != null)
                {
                    Main.Logger.Warning("PlayHitTimes after checkpoint scheduled start failed: " + ex);
                }
            }
            finally
            {
                ClearReferences(false);
            }
        }

        private static void PinChartAtRequestedLogical(scrConductor instance, double nowDsp)
        {
            double pitch = GetAttemptPitch();
            WriteDspTimeSong(
                instance,
                nowDsp - requestedLogicalSeconds / pitch - instance.addoffset / pitch);
        }

        private static bool ShouldIntercept(scrConductor instance, double newTime)
        {
            AudioSyncSettings settings = Main.Settings;
            if (!Main.Enabled || settings == null || !settings.EnableCheckpointStartHandshake)
            {
                return false;
            }
            if (instance == null || instance.song == null || instance.song.clip == null)
            {
                return false;
            }
            if (!ADOBase.isLevelEditor || GCS.checkpointNum <= 0)
            {
                return false;
            }
            return IsFinite(newTime) && newTime >= 0d;
        }

        private static bool IsSessionValid()
        {
            return conductor != null && source != null && clip != null &&
                   conductor.song == source && source.clip == clip;
        }

        private static bool TokenIsCurrent(int token)
        {
            return token == generation && IsActiveOrCompleted();
        }

        private static bool IsActiveOrCompleted()
        {
            return IsActive || state == HandshakeState.Aligned || state == HandshakeState.TimedOut;
        }

        private static void FailAndRelease(string message)
        {
            AudioListener.pause = false;
            state = HandshakeState.Failed;
            lastError = message ?? "checkpoint scheduled start failed";
            status = lastError;
            if (Main.Logger != null)
            {
                Main.Logger.Warning(lastError);
            }
            ClearReferences(true);
        }

        private static void CancelActive(string reason, bool unpause)
        {
            bool wasActive = IsActive;
            if (coroutine != null && conductor != null)
            {
                try { conductor.StopCoroutine(coroutine); } catch { }
            }
            if (wasActive && source != null)
            {
                try { source.Stop(); } catch { }
            }
            generation++;
            if (unpause)
            {
                AudioListener.pause = false;
            }
            if (IsActiveOrCompleted())
            {
                state = HandshakeState.Cancelled;
                status = "途中再生予約中止: " + (reason ?? "stop");
            }
            ClearReferences(false);
        }

        private static void ClearReferences(bool keepErrorState)
        {
            coroutine = null;
            conductor = null;
            source = null;
            clip = null;
            requestedLogicalSeconds = 0d;
            requestedSample = 0;
            lastObservedSample = 0;
            consecutiveMovingFrames = 0;
            stagnantFramesAfterMotion = 0;
            attemptStartRealtime = 0d;
            scheduledStartDsp = 0d;
            scheduledPitch = 0d;
            scheduledClipSeconds = 0d;
            usedTimeSamplesForSeek = false;
            attemptNumber = 0;
            if (!keepErrorState && state != HandshakeState.Aligned &&
                state != HandshakeState.TimedOut && state != HandshakeState.Cancelled)
            {
                state = HandshakeState.Idle;
            }
        }

        private static double GetExpectedSampleAtDsp(double dspTime)
        {
            if (clip == null || clip.frequency <= 0 || !IsFinite(dspTime))
            {
                return requestedSample;
            }

            double elapsedDsp = Math.Max(0d, dspTime - scheduledStartDsp);
            double expected = requestedSample +
                              elapsedDsp * clip.frequency * GetAttemptPitch();
            if (clip.samples > 0)
            {
                expected = Math.Max(0d, Math.Min(clip.samples - 1d, expected));
            }
            return expected;
        }

        private static int GetRequiredMovingFrames()
        {
            int value = Main.Settings == null ? 2 : Main.Settings.CheckpointStartStableFrames;
            return Math.Max(1, Math.Min(6, value));
        }

        private static double GetTimeoutSeconds()
        {
            double value = Main.Settings == null ? 2500d : Main.Settings.CheckpointStartTimeoutMs;
            return Math.Max(0.5d, Math.Min(6d, value / 1000d));
        }

        private static double GetScheduleLeadMs()
        {
            double value = Main.Settings == null ? 600d : Main.Settings.CheckpointScheduleLeadMs;
            return Math.Max(100d, Math.Min(2000d, value));
        }

        private static double GetMaxScheduleResidualMs()
        {
            double value = Main.Settings == null ? 50d : Main.Settings.CheckpointMaxInitialAdvanceMs;
            return Math.Max(5d, Math.Min(250d, value));
        }

        private static int GetMaxRetryCount()
        {
            int value = Main.Settings == null ? 1 : Main.Settings.CheckpointScheduleRetryCount;
            return Math.Max(0, Math.Min(3, value));
        }

        private static double GetPitch()
        {
            return Math.Max(0.0001d, Math.Abs(source == null ? 1d : (double)source.pitch));
        }

        private static double GetAttemptPitch()
        {
            return IsFinite(scheduledPitch) && scheduledPitch > 0d
                ? scheduledPitch
                : GetPitch();
        }

        private static double SampleDeltaToRealMilliseconds(double sampleDelta, double pitch)
        {
            if (clip == null || clip.frequency <= 0)
            {
                return 0d;
            }
            return sampleDelta / clip.frequency /
                   Math.Max(0.0001d, Math.Abs(pitch)) * 1000d;
        }

        private static string FormatSignedMilliseconds(double milliseconds)
        {
            return milliseconds.ToString("+0.0;-0.0;0.0") + "ms";
        }

        private static double GetStockScrubCountdownSeconds(scrConductor instance)
        {
            return instance.separateCountdownTime
                ? instance.crotchetAtStart * (double)instance.countdownTicks
                : 0d;
        }

        private static double ClampClipSeconds(double seconds, AudioClip audioClip)
        {
            if (seconds < 0d) return 0d;
            if (audioClip == null || audioClip.samples <= 1 || audioClip.frequency <= 0)
            {
                return seconds;
            }
            double maximum = (double)(audioClip.samples - 1) / audioClip.frequency;
            return Math.Min(seconds, maximum);
        }

        private static int SecondsToSample(double seconds, AudioClip audioClip)
        {
            if (audioClip == null || audioClip.frequency <= 0 || audioClip.samples <= 0)
            {
                return 0;
            }
            long sample = (long)Math.Round(seconds * audioClip.frequency);
            if (sample < 0L) sample = 0L;
            if (sample >= audioClip.samples) sample = audioClip.samples - 1L;
            return (int)sample;
        }

        private static bool TrySetTimeSamples(AudioSource audioSource, AudioClip audioClip, int sample)
        {
            try
            {
                if (audioSource == null || audioClip == null || audioClip.samples <= 0)
                {
                    return false;
                }
                int clamped = Math.Max(0, Math.Min(audioClip.samples - 1, sample));
                audioSource.timeSamples = clamped;
                int actual = audioSource.timeSamples;
                return Math.Abs(actual - clamped) <= Math.Max(2, audioClip.frequency / 200);
            }
            catch
            {
                return false;
            }
        }

        private static int ReadSampleSafe(AudioSource audioSource)
        {
            try { return audioSource == null ? 0 : audioSource.timeSamples; }
            catch { return 0; }
        }

        private static double SampleToSeconds(int sample, AudioClip audioClip)
        {
            if (audioClip == null || audioClip.frequency <= 0)
            {
                return double.NaN;
            }
            return (double)Math.Max(0, sample) / audioClip.frequency;
        }

        private static double ReadDspTimeSong(scrConductor instance)
        {
            if (instance == null || DspTimeSongField == null)
            {
                return double.NaN;
            }
            try
            {
                object value = DspTimeSongField.GetValue(instance);
                return value is double ? (double)value : double.NaN;
            }
            catch { return double.NaN; }
        }

        private static void WriteDspTimeSong(scrConductor instance, double value)
        {
            if (instance == null || DspTimeSongField == null)
            {
                throw new InvalidOperationException("scrConductor.dspTimeSong field was not found.");
            }
            DspTimeSongField.SetValue(instance, value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
