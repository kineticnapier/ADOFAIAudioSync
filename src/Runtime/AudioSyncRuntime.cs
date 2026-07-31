using System;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    internal static class AudioSyncRuntime
    {
        private enum GateState
        {
            Idle,
            EditorPreparing,
            DeferredCaptured,
            WaitingExtraFrames,
            WaitingRestartCooldown,
            InvokingGamePlay,
            Running,
            Passthrough,
            Failed
        }

        private static GateState state;
        private static scnEditor editor;
        private static scnGame deferredGame;
        private static int deferredCheckpoint;
        private static bool deferredFlag;
        private static int playbackStartFloor;
        private static int remainingFrames;
        private static int playRequestFrame;
        private static double playRequestRealtime;
        private static double editorPreparationMs;
        private static double gamePlayCallMs;
        private static string status = "待機中";
        private static string lastError = string.Empty;
        private static bool callCaptured;
        private static bool invokingDeferredCall;
        private static bool gatePatchInstalled;
        private static int capturedPlayCallCount;

        // ADOFAI and Unity can still be cleaning up AudioSources/coroutines at the end
        // of the frame after returning to the editor. Keep the last definite stop time
        // and only delay starts that arrive inside the configured cooldown window.
        private static double lastPlaybackStopRealtime = double.NegativeInfinity;
        private static string lastPlaybackStopReason = "-";
        private static int lastPlaybackStopFrame = int.MinValue;
        private static double remainingRestartCooldownMs;
        private static int remainingRestartCleanupFrames;
        private static double lastAppliedRestartCooldownMs;
        private static int lastAppliedRestartCleanupFrames;
        private static int restartCooldownApplyCount;

        // Never change the checkpoint chosen by ADOFAI. Stock scrController.Scrub keeps the
        // selected floor fixed while positioning the planets at an earlier angular phase of
        // that same floor, so they naturally reach the floor when the lead-in audio arrives.
        private static int actualPlaybackStartFloor;
        private static string sameFloorTakeoffStatus = "待機中";

        internal static bool Active
        {
            get { return state != GateState.Idle; }
        }

        internal static bool IsPreparing
        {
            get
            {
                return state == GateState.EditorPreparing ||
                       state == GateState.DeferredCaptured ||
                       state == GateState.WaitingExtraFrames ||
                       state == GateState.WaitingRestartCooldown ||
                       state == GateState.InvokingGamePlay;
            }
        }

        internal static string Status { get { return status; } }
        internal static string LastError { get { return lastError; } }
        internal static int PlaybackStartFloor { get { return playbackStartFloor; } }
        internal static int RemainingFrames { get { return remainingFrames; } }
        internal static int PlayRequestFrame { get { return playRequestFrame; } }
        internal static double EditorPreparationMs { get { return editorPreparationMs; } }
        internal static double GamePlayCallMs { get { return gamePlayCallMs; } }
        internal static bool CallCaptured { get { return callCaptured; } }
        internal static bool GatePatchInstalled { get { return gatePatchInstalled; } }
        internal static int CapturedPlayCallCount { get { return capturedPlayCallCount; } }
        internal static bool UsesFutureDspTime { get { return false; } }
        internal static double RemainingRestartCooldownMs { get { return remainingRestartCooldownMs; } }
        internal static double LastAppliedRestartCooldownMs { get { return lastAppliedRestartCooldownMs; } }
        internal static int RemainingRestartCleanupFrames { get { return remainingRestartCleanupFrames; } }
        internal static int LastAppliedRestartCleanupFrames { get { return lastAppliedRestartCleanupFrames; } }
        internal static int RestartCooldownApplyCount { get { return restartCooldownApplyCount; } }
        internal static string LastPlaybackStopReason { get { return lastPlaybackStopReason; } }
        internal static int ActualPlaybackStartFloor { get { return actualPlaybackStartFloor; } }
        internal static string SameFloorTakeoffStatus { get { return sameFloorTakeoffStatus; } }

        internal static void Initialize()
        {
            Reset("初期化");
        }

        internal static void SetGatePatchInstalled(bool installed)
        {
            gatePatchInstalled = installed;
            if (!installed && Main.Logger != null)
            {
                Main.Logger.Warning(
                    "開始ゲートのHarmonyパッチを導入できませんでした。" +
                    "BPM Tap Meterは動作しますが、再生開始は通常動作になります。");
            }
        }

        internal static void NotifyEditorPlayPrefix(scnEditor instance, int startFloor)
        {
            CancelPending(false);
            editor = instance;
            playbackStartFloor = Math.Max(0, startFloor);
            actualPlaybackStartFloor = playbackStartFloor;
            sameFloorTakeoffStatus = playbackStartFloor > 0
                ? "選択床 " + playbackStartFloor + " を固定（本体指定待ち）"
                : "先頭再生";
            playRequestFrame = Time.frameCount;
            playRequestRealtime = Time.realtimeSinceStartupAsDouble;
            editorPreparationMs = 0d;
            gamePlayCallMs = 0d;
            lastAppliedRestartCooldownMs = 0d;
            lastAppliedRestartCleanupFrames = 0;
            remainingRestartCooldownMs = 0d;
            remainingRestartCleanupFrames = 0;
            lastError = string.Empty;
            callCaptured = false;
            capturedPlayCallCount = 0;
            remainingFrames = 0;

            if (!Main.Enabled || Main.Settings == null || !Main.Settings.EnableStartGate || !gatePatchInstalled)
            {
                state = GateState.Passthrough;
                status = !gatePatchInstalled
                    ? "開始ゲート未導入: 通常再生"
                    : "開始ゲートOFF: 通常再生";
                return;
            }

            state = GateState.EditorPreparing;
            status = "タイル・イベント・アセットを準備中";
        }

        // Harmony Prefix for scnGame.Play(int, bool). Returning false suppresses the
        // original call only while scnEditor.Play is still preparing the editor scene.
        // The exact same call is executed after preparation and, when necessary, after
        // the rapid-restart cleanup window has elapsed.
        internal static bool ShouldRunGamePlayNow(scnGame game, ref int checkpoint, bool flag)
        {
            if (!invokingDeferredCall)
            {
                // Diagnostic only. Never rewrite checkpoint or GCS.checkpointNum here.
                actualPlaybackStartFloor = Math.Max(0, checkpoint);
                if (actualPlaybackStartFloor <= 0)
                {
                    sameFloorTakeoffStatus = "先頭再生";
                }
                else if (actualPlaybackStartFloor == playbackStartFloor)
                {
                    sameFloorTakeoffStatus = "床 " + actualPlaybackStartFloor +
                                             " 固定 / ADOFAI本体の同一床内助走";
                }
                else
                {
                    sameFloorTakeoffStatus = "本体指定床 " + actualPlaybackStartFloor +
                                             " を変更せず使用（選択床 " + playbackStartFloor + "）";
                }
            }

            if (invokingDeferredCall || !Main.Enabled || Main.Settings == null ||
                !Main.Settings.EnableStartGate || !gatePatchInstalled)
            {
                return true;
            }

            if (state != GateState.EditorPreparing && state != GateState.DeferredCaptured)
            {
                return true;
            }

            capturedPlayCallCount++;
            if (!callCaptured)
            {
                deferredGame = game;
                deferredCheckpoint = checkpoint;
                deferredFlag = flag;
                callCaptured = true;
                state = GateState.DeferredCaptured;
                status = "ゲーム開始だけ保留中";
            }
            else if (Main.Logger != null && capturedPlayCallCount == 2)
            {
                Main.Logger.Warning(
                    "1回のscnEditor.Play中にscnGame.Playが複数回呼ばれました。" +
                    "最初の呼び出しだけを保留し、重複呼び出しは抑止します。");
            }

            return false;
        }

        internal static void NotifyEditorPlayPostfix()
        {
            if (playRequestRealtime > 0d)
            {
                editorPreparationMs =
                    (Time.realtimeSinceStartupAsDouble - playRequestRealtime) * 1000d;
            }

            if (state == GateState.Passthrough)
            {
                status = "通常再生（ゲート未使用）";
                return;
            }

            if (!callCaptured || deferredGame == null)
            {
                Fail("scnGame.Play呼び出しを保留できませんでした。通常処理を変更していません。");
                return;
            }

            remainingFrames = Math.Max(0, Math.Min(3, Main.Settings.ExtraPreparationFrames));
            if (remainingFrames == 0)
            {
                ContinueAfterPreparationFrames();
            }
            else
            {
                state = GateState.WaitingExtraFrames;
                status = "追加準備フレーム待ち: " + remainingFrames;
            }
        }

        internal static Exception NotifyEditorPlayFinalizer(Exception exception)
        {
            if (exception != null)
            {
                CancelPending(false);
                state = GateState.Failed;
                lastError = exception.GetType().Name + ": " + exception.Message;
                status = "エディター再生初期化で例外";
            }
            return exception;
        }

        internal static void UpdateFrame()
        {
            if (!Main.Enabled || Main.Settings == null)
            {
                CancelPending(false);
                return;
            }

            if (state == GateState.WaitingExtraFrames)
            {
                if (!IsEditorStillValid())
                {
                    Fail("追加準備中にエディターが失われました。");
                    return;
                }

                if (remainingFrames > 0)
                {
                    remainingFrames--;
                    status = "追加準備フレーム待ち: " + remainingFrames;
                }
                if (remainingFrames <= 0)
                {
                    ContinueAfterPreparationFrames();
                }
                return;
            }

            if (state == GateState.WaitingRestartCooldown)
            {
                if (!IsEditorStillValid())
                {
                    Fail("高速再開ガード待機中にエディターが失われました。");
                    return;
                }

                remainingRestartCooldownMs = CalculateRemainingRestartCooldownMs();
                remainingRestartCleanupFrames = CalculateRemainingRestartCleanupFrames();
                if (remainingRestartCooldownMs <= 0.01d && remainingRestartCleanupFrames <= 0)
                {
                    remainingRestartCooldownMs = 0d;
                    remainingRestartCleanupFrames = 0;
                    InvokeDeferredGamePlay();
                }
                else
                {
                    status = BuildRestartWaitStatus();
                }
            }
        }

        internal static void NotifyPlaybackStopped()
        {
            NotifyPlaybackStopped("playback stopped");
        }

        internal static void NotifyPlaybackStopped(string reason)
        {
            CheckpointStartHandshakeRuntime.NotifyStop(reason);
            lastPlaybackStopRealtime = Time.realtimeSinceStartupAsDouble;
            lastPlaybackStopFrame = Time.frameCount;
            lastPlaybackStopReason = string.IsNullOrEmpty(reason) ? "playback stopped" : reason;
            CancelPending(false);
            state = GateState.Idle;
            status = "再生停止";
        }

        internal static void Reset(string reason)
        {
            CancelPending(false);
            state = GateState.Idle;
            status = reason ?? "待機中";
            lastError = string.Empty;
            editorPreparationMs = 0d;
            gamePlayCallMs = 0d;
            playbackStartFloor = 0;
            playRequestFrame = 0;
            capturedPlayCallCount = 0;
            remainingRestartCooldownMs = 0d;
            remainingRestartCleanupFrames = 0;
            lastAppliedRestartCooldownMs = 0d;
            lastAppliedRestartCleanupFrames = 0;
            actualPlaybackStartFloor = 0;
            sameFloorTakeoffStatus = reason ?? "待機中";
        }

        internal static void Shutdown()
        {
            CancelPending(false);
            state = GateState.Idle;
            status = "終了";
        }

        internal static int ResolveStartFloor(scnEditor instance)
        {
            if (instance == null)
            {
                return 0;
            }

            try
            {
                return EditorSelectionCompat.ResolveSelectedFloor(instance, 0);
            }
            catch (Exception)
            {
                // Diagnostic-only value; never allow failure here to block normal playback.
                return 0;
            }
        }

        private static void ContinueAfterPreparationFrames()
        {
            remainingRestartCooldownMs = CalculateRemainingRestartCooldownMs();
            remainingRestartCleanupFrames = CalculateRemainingRestartCleanupFrames();
            if (remainingRestartCooldownMs > 0.01d || remainingRestartCleanupFrames > 0)
            {
                state = GateState.WaitingRestartCooldown;
                lastAppliedRestartCooldownMs = remainingRestartCooldownMs;
                lastAppliedRestartCleanupFrames = remainingRestartCleanupFrames;
                restartCooldownApplyCount++;
                status = BuildRestartWaitStatus();
                if (Main.Logger != null)
                {
                    Main.Logger.Log(
                        "Rapid restart guard delayed scnGame.Play by " +
                        remainingRestartCooldownMs.ToString("0.0") + " ms and " +
                        remainingRestartCleanupFrames + " frame(s) after " +
                        lastPlaybackStopReason + ".");
                }
                return;
            }

            remainingRestartCooldownMs = 0d;
            remainingRestartCleanupFrames = 0;
            InvokeDeferredGamePlay();
        }

        private static double CalculateRemainingRestartCooldownMs()
        {
            if (Main.Settings == null || !Main.Settings.EnableRapidRestartGuard ||
                double.IsNegativeInfinity(lastPlaybackStopRealtime))
            {
                return 0d;
            }

            double configuredMs = Math.Max(0d, Main.Settings.RapidRestartCooldownMs);
            double elapsedMs =
                (Time.realtimeSinceStartupAsDouble - lastPlaybackStopRealtime) * 1000d;
            if (elapsedMs < 0d)
            {
                return 0d;
            }

            return Math.Max(0d, configuredMs - elapsedMs);
        }

        private static int CalculateRemainingRestartCleanupFrames()
        {
            if (Main.Settings == null || !Main.Settings.EnableRapidRestartGuard ||
                lastPlaybackStopFrame == int.MinValue)
            {
                return 0;
            }

            int configuredFrames = Math.Max(0, Main.Settings.RapidRestartCleanupFrames);
            int elapsedFrames = Math.Max(0, Time.frameCount - lastPlaybackStopFrame);
            return Math.Max(0, configuredFrames - elapsedFrames);
        }

        private static string BuildRestartWaitStatus()
        {
            return "Audio reset待ち: " +
                   Math.Ceiling(remainingRestartCooldownMs).ToString("0") + "ms / " +
                   remainingRestartCleanupFrames + "f";
        }

        private static bool IsEditorStillValid()
        {
            return editor != null && scnEditor.instance == editor;
        }

        private static void InvokeDeferredGamePlay()
        {
            scnGame game = deferredGame;
            int checkpoint = deferredCheckpoint;
            bool flag = deferredFlag;

            deferredGame = null;
            callCaptured = false;
            remainingFrames = 0;
            remainingRestartCooldownMs = 0d;
            remainingRestartCleanupFrames = 0;

            if (game == null)
            {
                Fail("保留したscnGameインスタンスがありません。");
                return;
            }

            state = GateState.InvokingGamePlay;
            status = "準備完了: 音源と譜面を通常開始";
            double started = Time.realtimeSinceStartupAsDouble;
            try
            {
                invokingDeferredCall = true;
                game.Play(checkpoint, flag);
                gamePlayCallMs =
                    (Time.realtimeSinceStartupAsDouble - started) * 1000d;
                state = GateState.Running;
                status = lastAppliedRestartCooldownMs > 0d || lastAppliedRestartCleanupFrames > 0
                    ? "開始済み（高速再開ガード適用）"
                    : "開始済み（本体の再生経路を変更せず使用）";
            }
            catch (Exception ex)
            {
                gamePlayCallMs =
                    (Time.realtimeSinceStartupAsDouble - started) * 1000d;
                Fail("保留したゲーム開始に失敗: " + ex.GetType().Name + ": " + ex.Message);
                if (Main.Logger != null)
                {
                    Main.Logger.Error(ex.ToString());
                }
                throw;
            }
            finally
            {
                invokingDeferredCall = false;
            }
        }

        private static void CancelPending(bool invokeFallback)
        {
            scnGame pending = deferredGame;
            int checkpoint = deferredCheckpoint;
            bool flag = deferredFlag;

            deferredGame = null;
            callCaptured = false;
            capturedPlayCallCount = 0;
            remainingFrames = 0;
            remainingRestartCooldownMs = 0d;
            remainingRestartCleanupFrames = 0;
            editor = null;

            if (invokeFallback && pending != null)
            {
                try
                {
                    invokingDeferredCall = true;
                    pending.Play(checkpoint, flag);
                }
                finally
                {
                    invokingDeferredCall = false;
                }
            }
        }

        private static void Fail(string message)
        {
            lastError = message ?? "不明なエラー";
            status = "開始ゲート失敗";
            state = GateState.Failed;
            if (Main.Logger != null)
            {
                Main.Logger.Warning(lastError);
            }
        }
    }
}
