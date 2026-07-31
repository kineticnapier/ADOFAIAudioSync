using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Spreads one-time allocations and reflection/file preparation over idle editor frames.
    /// No prewarm work is started while a song is playing or the start gate is preparing.
    /// </summary>
    internal static class AudioSyncPrewarmRuntime
    {
        private enum PrewarmStage
        {
            Waiting,
            DriftReflectionAndLog,
            ProbeClip,
            ProbeVoices,
            Completed,
            Disabled
        }

        private static PrewarmStage stage;
        private static int nextStepFrame;
        private static string status = "未開始";

        internal static string Status { get { return status; } }
        internal static bool Completed { get { return stage == PrewarmStage.Completed; } }

        internal static void Initialize()
        {
            stage = PrewarmStage.Waiting;
            nextStepFrame = Time.frameCount + 2;
            status = "待機中";
        }

        internal static void Update()
        {
            AudioSyncSettings settings = Main.Settings;
            if (!Main.Enabled || settings == null || !settings.EnableIdlePrewarm)
            {
                stage = PrewarmStage.Disabled;
                status = "OFF";
                return;
            }

            if (stage == PrewarmStage.Completed) return;
            if (stage == PrewarmStage.Disabled)
            {
                stage = PrewarmStage.Waiting;
                nextStepFrame = Time.frameCount + 1;
            }
            if (Time.frameCount < nextStepFrame) return;

            if (!IsIdle())
            {
                status = "再生終了待ち";
                nextStepFrame = Time.frameCount + 15;
                return;
            }

            switch (stage)
            {
                case PrewarmStage.Waiting:
                    stage = PrewarmStage.DriftReflectionAndLog;
                    status = "ドリフト監視を準備中";
                    break;

                case PrewarmStage.DriftReflectionAndLog:
                    ConductorDriftRuntime.Prewarm();
                    stage = PrewarmStage.ProbeClip;
                    status = "診断音クリップを準備中";
                    break;

                case PrewarmStage.ProbeClip:
                    DspProbeCueRuntime.PrewarmClip();
                    stage = PrewarmStage.ProbeVoices;
                    status = "AudioSourceプールを準備中";
                    break;

                case PrewarmStage.ProbeVoices:
                    int target = Mathf.Clamp(settings.IdlePrewarmVoiceCount, 1, 16);
                    if (DspProbeCueRuntime.PrewarmOneVoice(target))
                    {
                        stage = PrewarmStage.Completed;
                        status = "完了（" + DspProbeCueRuntime.VoiceCount + " voices）";
                        if (Main.Logger != null) Main.Logger.Log("AudioSync idle prewarm completed.");
                    }
                    break;
            }

            // Deliberately perform at most one allocation-heavy step per frame.
            nextStepFrame = Time.frameCount + 1;
        }

        internal static void Restart()
        {
            stage = PrewarmStage.Waiting;
            nextStepFrame = Time.frameCount + 1;
            status = "再実行待ち";
        }

        internal static void Shutdown()
        {
            stage = PrewarmStage.Disabled;
            status = "終了";
        }

        private static bool IsIdle()
        {
            if (AudioSyncRuntime.IsPreparing) return false;
            try
            {
                scrConductor conductor = scrConductor.instance;
                if (conductor != null && conductor.song != null && conductor.song.isPlaying)
                    return false;
            }
            catch
            {
                return false;
            }
            return true;
        }
    }
}
