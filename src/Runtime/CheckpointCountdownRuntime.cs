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
        private static string status = "待機中";
        private static float originalBpm;
        private static float foldedBpm;
        private static float countdownSeconds;
        private static int divisor = 1;
        private static int targetFloor;

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
            originalBpm = 0f;
            foldedBpm = 0f;
            countdownSeconds = 0f;
            divisor = 1;
            targetFloor = 0;
            status = "再生準備中";
        }

        internal static void BeforeScrub(int floorNumber)
        {
            SetConductorMultiplier(1f);
            originalBpm = 0f;
            foldedBpm = 0f;
            countdownSeconds = 0f;
            divisor = 1;
            targetFloor = floorNumber;

            AudioSyncSettings settings = Main.Settings;
            if (!Main.Enabled || settings == null || !settings.EnableCheckpointCountdownFold)
            {
                status = "OFF";
                return;
            }
            if (!ADOBase.isLevelEditor || GCS.checkpointNum <= 0)
            {
                status = "先頭再生";
                return;
            }

            try
            {
                scrConductor activeConductor = scrConductor.instance;
                scrLevelMaker maker = ADOBase.lm;
                if (activeConductor == null || maker == null || maker.listFloors == null ||
                    floorNumber < 0 || floorNumber >= maker.listFloors.Count)
                {
                    status = "床情報不足";
                    return;
                }

                scrFloor floor = maker.listFloors[floorNumber];
                if (floor == null)
                {
                    status = "床情報不足";
                    return;
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
                    return;
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

                activeConductor.countdownSpeedMultiplier = multiplier;
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
                        ", duration=" + countdownSeconds.ToString("0.000") + " s.");
                }
            }
            catch (Exception ex)
            {
                SetConductorMultiplier(1f);
                status = "倍率設定失敗";
                if (Main.Logger != null)
                {
                    Main.Logger.Warning("Checkpoint countdown setup failed: " + ex);
                }
            }
        }

        internal static void Reset(string reason)
        {
            SetConductorMultiplier(1f);
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

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
