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
            InvokingGamePlay,
            Running,
            Passthrough,
            Failed
        }

        private static GateState state;
        private static scnGame deferredGame;
        private static int deferredCheckpoint;
        private static bool deferredFlag;
        private static int playbackStartFloor;
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

        // Preserve the exact checkpoint chosen by ADOFAI. A chart may legitimately use floor
        // indices that are larger than an older copy of the same chart, so the mod must never
        // fold or clamp them using angleData.Count.
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
                       state == GateState.InvokingGamePlay;
            }
        }

        internal static string Status { get { return status; } }
        internal static string LastError { get { return lastError; } }
        internal static int PlaybackStartFloor { get { return playbackStartFloor; } }
        internal static int PlayRequestFrame { get { return playRequestFrame; } }
        internal static double EditorPreparationMs { get { return editorPreparationMs; } }
        internal static double GamePlayCallMs { get { return gamePlayCallMs; } }
        internal static bool CallCaptured { get { return callCaptured; } }
        internal static bool GatePatchInstalled { get { return gatePatchInstalled; } }
        internal static int CapturedPlayCallCount { get { return capturedPlayCallCount; } }
        internal static bool UsesFutureDspTime { get { return false; } }
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

        internal static void NotifyEditorPlayPrefix(scnEditor instance)
        {
            CancelPending(false);
            int startFloor = ResolveStartFloor(instance);
            playbackStartFloor = Math.Max(0, startFloor);
            actualPlaybackStartFloor = playbackStartFloor;
            sameFloorTakeoffStatus = playbackStartFloor > 0
                ? "選択床 " + playbackStartFloor + " を固定（本体指定待ち）"
                : "先頭再生";
            playRequestFrame = Time.frameCount;
            playRequestRealtime = Time.realtimeSinceStartupAsDouble;
            editorPreparationMs = 0d;
            gamePlayCallMs = 0d;
            lastError = string.Empty;
            callCaptured = false;
            capturedPlayCallCount = 0;

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
        // The exact same call is executed from scnEditor.Play's Postfix. Do not move it
        // to a later frame: ReloadAssets and Play are one atomic sequence in stock ADOFAI,
        // and detaching Play lets floor objects/selection state change underneath it.
        internal static bool ShouldRunGamePlayNow(scnGame game, ref int checkpoint, bool flag)
        {
            if (!invokingDeferredCall)
            {
                // Diagnostic only. The stock checkpoint is intentionally left unchanged.
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

            InvokeDeferredGamePlay();
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

        internal static void NotifyPlaybackStopped()
        {
            NotifyPlaybackStopped("playback stopped");
        }

        internal static void NotifyPlaybackStopped(string reason)
        {
            CheckpointStartHandshakeRuntime.NotifyStop(reason);
            CancelPending(false);
            state = GateState.Idle;
            status = "再生停止";
        }

        internal static void Reset(string reason)
        {
            Reset(reason, false);
        }

        internal static void Reset(string reason, bool invokePendingFallback)
        {
            CancelPending(invokePendingFallback);
            state = GateState.Idle;
            status = reason ?? "待機中";
            lastError = string.Empty;
            editorPreparationMs = 0d;
            gamePlayCallMs = 0d;
            playbackStartFloor = 0;
            playRequestFrame = 0;
            capturedPlayCallCount = 0;
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
                // Match scnEditor.Play exactly: stock playback reads selectedFloors[0],
                // not the most recently appended item in a multi-selection.
                if (instance.selectedFloors != null && instance.selectedFloors.Count > 0 &&
                    instance.selectedFloors[0] != null)
                {
                    return Math.Max(0, instance.selectedFloors[0].seqID);
                }
                return EditorSelectionCompat.ResolveSelectedFloor(instance, 0);
            }
            catch (Exception)
            {
                // Diagnostic-only value; never allow failure here to block normal playback.
                return 0;
            }
        }

        private static void InvokeDeferredGamePlay()
        {
            scnGame game = deferredGame;
            int checkpoint = deferredCheckpoint;
            bool flag = deferredFlag;

            deferredGame = null;
            callCaptured = false;

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
                status = "開始済み（本体の再生経路を変更せず使用）";
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

            if (invokeFallback && pending != null)
            {
                try
                {
                    invokingDeferredCall = true;
                    pending.Play(checkpoint, flag);
                }
                catch (Exception ex)
                {
                    lastError = "保留開始のフォールバックに失敗: " +
                                ex.GetType().Name + ": " + ex.Message;
                    if (Main.Logger != null) Main.Logger.Warning(lastError);
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
