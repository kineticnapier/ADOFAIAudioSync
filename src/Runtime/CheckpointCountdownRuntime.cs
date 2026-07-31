using System;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Keeps checkpoint countdowns readable at very high effective BPM values.
    /// The multiplier is always an inverse power of two so the original beat
    /// subdivision remains recognizable.
    /// </summary>
    internal static class CheckpointCountdownRuntime
    {
        internal sealed class ScrubScope
        {
            internal readonly scrConductor Conductor;
            internal readonly double OriginalCrotchet;
            internal readonly float FinalMultiplier;
            internal bool Restored;

            internal ScrubScope(
                scrConductor conductor,
                double originalCrotchet,
                float finalMultiplier)
            {
                Conductor = conductor;
                OriginalCrotchet = originalCrotchet;
                FinalMultiplier = finalMultiplier;
            }
        }

        private static string status = "待機中";
        private static float originalBpm;
        private static float foldedBpm;
        private static float countdownSeconds;
        private static int divisor = 1;
        private static int targetFloor;
        private static scrConductor synchronizationConductor;
        private static double synchronizationCountdownSeconds;

        internal static string Status { get { return status; } }
        internal static float OriginalBpm { get { return originalBpm; } }
        internal static float FoldedBpm { get { return foldedBpm; } }
        internal static float CountdownSeconds { get { return countdownSeconds; } }
        internal static int Divisor { get { return divisor; } }
        internal static int TargetFloor { get { return targetFloor; } }

        internal static void Initialize()
        {
            Reset("待機中");
        }

        internal static void BeginEditorPlay()
        {
            SetConductorMultiplier(1f);
            ClearSynchronizationSnapshot();
            originalBpm = 0f;
            foldedBpm = 0f;
            countdownSeconds = 0f;
            divisor = 1;
            targetFloor = 0;
            status = "再生準備中";
        }

        internal static ScrubScope BeforeScrub(int floorNumber)
        {
            SetConductorMultiplier(1f);
            ClearSynchronizationSnapshot();
            originalBpm = 0f;
            foldedBpm = 0f;
            countdownSeconds = 0f;
            divisor = 1;
            targetFloor = floorNumber;

            AudioSyncSettings settings = Main.Settings;
            if (!Main.Enabled || settings == null || !settings.EnableCheckpointCountdownFold)
            {
                status = "OFF";
                return null;
            }
            if (!ADOBase.isLevelEditor || GCS.checkpointNum <= 0)
            {
                status = "先頭再生";
                return null;
            }

            ScrubScope scope = null;
            try
            {
                scrConductor activeConductor = scrConductor.instance;
                scrLevelMaker maker = ADOBase.lm;
                if (activeConductor == null || maker == null || maker.listFloors == null ||
                    floorNumber < 0 || floorNumber >= maker.listFloors.Count)
                {
                    status = "床情報不足";
                    return null;
                }

                scrFloor floor = maker.listFloors[floorNumber];
                if (floor == null)
                {
                    status = "床情報不足";
                    return null;
                }

                float pitch = 1f;
                if (activeConductor.song != null)
                {
                    pitch = Math.Abs(activeConductor.song.pitch);
                }
                if (pitch < 0.0001f) pitch = 1f;

                float speed = Math.Abs(floor.speed);
                if (speed < 0.0001f) speed = 1f;

                // GetCountdownTime also scales checkpoint ticks by half the active
                // planet count. Include the same factor when deciding the fold.
                float planetFactor = Math.Max(0.5f, floor.numPlanets * 0.5f);
                originalBpm = Math.Abs(activeConductor.bpm) * speed * pitch * planetFactor;
                if (!IsFinitePositive(originalBpm))
                {
                    status = "BPM情報不足";
                    originalBpm = 0f;
                    return null;
                }

                float maximumBpm = Math.Max(60f, Math.Min(600f, settings.CheckpointCountdownMaxBpm));
                foldedBpm = originalBpm;
                float multiplier = 1f;
                while (foldedBpm > maximumBpm && divisor < 1024)
                {
                    foldedBpm *= 0.5f;
                    multiplier *= 0.5f;
                    divisor *= 2;
                }

                // scrController.Scrub uses countdownSpeedMultiplier for one part of
                // the checkpoint calculation, but its planetary lead-in calculation
                // reads crotchetAtStart directly. Applying only the multiplier makes
                // early countdown numbers elapse before the planets are positioned,
                // leaving only "1" visible.
                //
                // During Scrub, transfer the same scale into crotchetAtStart and keep
                // the multiplier at 1. This makes every stock Scrub calculation use
                // the same extended duration. Restore the original crotchet immediately
                // afterward and apply the multiplier for the live countdown. Planet
                // angular velocity therefore continues to use the chart's original BPM.
                double originalCrotchet = activeConductor.crotchetAtStart;
                CaptureSynchronizationCountdown(
                    activeConductor,
                    originalCrotchet);
                scope = new ScrubScope(
                    activeConductor,
                    originalCrotchet,
                    multiplier);
                if (multiplier < 0.9999f &&
                    originalCrotchet > 0d &&
                    !double.IsNaN(originalCrotchet) &&
                    !double.IsInfinity(originalCrotchet))
                {
                    activeConductor.crotchetAtStart =
                        originalCrotchet / multiplier;
                }
                activeConductor.countdownSpeedMultiplier = 1f;

                int ticks = Math.Max(1, activeConductor.countdownTicks);
                countdownSeconds = ticks * 60f / Math.Max(0.0001f, foldedBpm);
                status = divisor > 1
                    ? originalBpm.ToString("0.0") + " → " + foldedBpm.ToString("0.0") +
                      " BPM（÷" + divisor + "）"
                    : foldedBpm.ToString("0.0") + " BPM（変更なし）";

                if (Main.Logger != null)
                {
                    Main.Logger.Log(
                        "Checkpoint countdown: floor=" + floorNumber +
                        ", effectiveBpm=" + originalBpm.ToString("0.0") +
                        ", foldedBpm=" + foldedBpm.ToString("0.0") +
                        ", divisor=" + divisor +
                        ", duration=" + countdownSeconds.ToString("0.000") + " s" +
                        ", scrubCrotchet=" +
                        activeConductor.crotchetAtStart.ToString("0.000000") + " s.");
                }
                return scope;
            }
            catch (Exception ex)
            {
                EndScrub(scope);
                SetConductorMultiplier(1f);
                status = "倍率設定失敗";
                if (Main.Logger != null)
                {
                    Main.Logger.Warning("Checkpoint countdown setup failed: " + ex);
                }
                return null;
            }
        }

        internal static void EndScrub(ScrubScope scope)
        {
            if (scope == null || scope.Restored)
            {
                return;
            }

            scope.Restored = true;
            try
            {
                if (scope.Conductor != null)
                {
                    scope.Conductor.crotchetAtStart = scope.OriginalCrotchet;
                    scope.Conductor.countdownSpeedMultiplier =
                        scope.FinalMultiplier;
                }
            }
            catch (Exception ex)
            {
                SetConductorMultiplier(1f);
                status = "Scrub後の倍率復元失敗";
                if (Main.Logger != null)
                {
                    Main.Logger.Warning(
                        "Checkpoint countdown restore failed: " + ex);
                }
            }
        }

        internal static double GetAudioSynchronizationCountdownSeconds(
            scrConductor activeConductor)
        {
            if (activeConductor == null || !activeConductor.separateCountdownTime)
            {
                return 0d;
            }

            if (object.ReferenceEquals(
                    activeConductor,
                    synchronizationConductor) &&
                IsFiniteNonNegative(synchronizationCountdownSeconds))
            {
                return synchronizationCountdownSeconds;
            }

            return CalculateStockCountdownSeconds(
                activeConductor,
                activeConductor.crotchetAtStart);
        }

        internal static void Reset(string reason)
        {
            SetConductorMultiplier(1f);
            ClearSynchronizationSnapshot();
            originalBpm = 0f;
            foldedBpm = 0f;
            countdownSeconds = 0f;
            divisor = 1;
            targetFloor = 0;
            status = reason ?? "待機中";
        }

        internal static void Shutdown()
        {
            Reset("終了");
        }

        private static void SetConductorMultiplier(float value)
        {
            try
            {
                scrConductor activeConductor = scrConductor.instance;
                if (activeConductor != null)
                {
                    activeConductor.countdownSpeedMultiplier = value;
                }
            }
            catch
            {
                // The conductor may already have been destroyed during a scene transition.
            }
        }

        private static void CaptureSynchronizationCountdown(
            scrConductor activeConductor,
            double originalCrotchet)
        {
            synchronizationConductor = activeConductor;
            synchronizationCountdownSeconds =
                CalculateStockCountdownSeconds(
                    activeConductor,
                    originalCrotchet);
        }

        private static double CalculateStockCountdownSeconds(
            scrConductor activeConductor,
            double crotchet)
        {
            if (activeConductor == null ||
                !activeConductor.separateCountdownTime)
            {
                return 0d;
            }

            double seconds =
                crotchet * (double)Math.Max(0, activeConductor.countdownTicks);
            return IsFiniteNonNegative(seconds) ? seconds : 0d;
        }

        private static void ClearSynchronizationSnapshot()
        {
            synchronizationConductor = null;
            synchronizationCountdownSeconds = 0d;
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return value >= 0d &&
                   !double.IsNaN(value) &&
                   !double.IsInfinity(value);
        }
    }
}
