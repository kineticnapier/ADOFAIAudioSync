using System;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Observes the stock editor-to-game playback path without delaying, suppressing,
    /// or invoking scnGame.Play itself. Audio synchronization is handled independently
    /// by the ScrubMusicToTime handshake.
    /// </summary>
    internal static class AudioSyncRuntime
    {
        private enum StartState
        {
            Idle,
            EditorPreparing,
            GamePlayRunning,
            Running,
            Passthrough,
            Failed
        }

        private static StartState state;
        private static int playbackStartFloor;
        private static int playRequestFrame;
        private static double playRequestRealtime;
        private static double editorPreparationMs;
        private static double gamePlayCallMs;
        private static string status = "待機中";
        private static string lastError = string.Empty;
        private static bool playCallObserved;
        private static bool observationPatchInstalled;
        private static int observedPlayCallCount;

        // Preserve the exact checkpoint chosen by ADOFAI. A chart may legitimately use floor
        // indices that are larger than an older copy of the same chart, so the mod must never
        // fold or clamp them using angleData.Count.
        private static int actualPlaybackStartFloor;
        private static string sameFloorTakeoffStatus = "待機中";

        internal static bool Active
        {
            get { return state != StartState.Idle; }
        }

        internal static bool IsPreparing
        {
            get
            {
                return state == StartState.EditorPreparing ||
                       state == StartState.GamePlayRunning;
            }
        }

        internal static string Status { get { return status; } }
        internal static string LastError { get { return lastError; } }
        internal static int PlaybackStartFloor { get { return playbackStartFloor; } }
        internal static int PlayRequestFrame { get { return playRequestFrame; } }
        internal static double EditorPreparationMs { get { return editorPreparationMs; } }
        internal static double GamePlayCallMs { get { return gamePlayCallMs; } }
        internal static bool CallCaptured { get { return playCallObserved; } }
        internal static bool GatePatchInstalled { get { return observationPatchInstalled; } }
        internal static int CapturedPlayCallCount { get { return observedPlayCallCount; } }
        internal static bool UsesFutureDspTime { get { return false; } }
        internal static int ActualPlaybackStartFloor { get { return actualPlaybackStartFloor; } }
        internal static string SameFloorTakeoffStatus { get { return sameFloorTakeoffStatus; } }

        internal static void Initialize()
        {
            Reset("初期化");
        }

        internal static void SetGatePatchInstalled(bool installed)
        {
            observationPatchInstalled = installed;
            if (!installed && Main.Logger != null)
            {
                Main.Logger.Warning(
                    "再生開始の観測パッチを導入できませんでした。" +
                    "再生処理は変更せず、途中再生handshakeは独立して動作します。");
            }
        }

        internal static void NotifyEditorPlayPrefix(scnEditor instance)
        {
            int startFloor = ResolveStartFloor(instance);
            playbackStartFloor = Math.Max(0, startFloor);
            actualPlaybackStartFloor = playbackStartFloor;
            sameFloorTakeoffStatus = playbackStartFloor > 0
                ? "選択床 " + playbackStartFloor + "（本体checkpoint待ち）"
                : "先頭再生";
            playRequestFrame = Time.frameCount;
            playRequestRealtime = Time.realtimeSinceStartupAsDouble;
            editorPreparationMs = 0d;
            gamePlayCallMs = 0d;
            lastError = string.Empty;
            playCallObserved = false;
            observedPlayCallCount = 0;

            if (!Main.Enabled)
            {
                state = StartState.Passthrough;
                status = "Mod無効: 本体再生";
                return;
            }

            state = StartState.EditorPreparing;
            status = "本体の再生開始を待機中（介入なし）";
        }

        /// <summary>
        /// Harmony Prefix observation for scnGame.Play(int, bool). This method deliberately
        /// returns no bool and never changes checkpoint: the stock call must execute exactly
        /// once so other Harmony Postfix patches also receive exactly one notification.
        /// </summary>
        internal static double NotifyGamePlayPrefix(int checkpoint)
        {
            observedPlayCallCount++;
            playCallObserved = true;
            actualPlaybackStartFloor = Math.Max(0, checkpoint);

            if (actualPlaybackStartFloor <= 0)
            {
                sameFloorTakeoffStatus = "先頭再生";
            }
            else if (actualPlaybackStartFloor == playbackStartFloor)
            {
                sameFloorTakeoffStatus = "床 " + actualPlaybackStartFloor +
                                         " / ADOFAI本体の同一床内助走";
            }
            else
            {
                sameFloorTakeoffStatus = "本体指定床 " + actualPlaybackStartFloor +
                                         " を変更せず使用（選択床 " + playbackStartFloor + "）";
            }

            state = StartState.GamePlayRunning;
            status = "scnGame.Play実行中（本体呼び出しを変更せず通過）";
            return Time.realtimeSinceStartupAsDouble;
        }

        internal static void NotifyGamePlayPostfix(double startedRealtime)
        {
            if (startedRealtime > 0d)
            {
                gamePlayCallMs =
                    (Time.realtimeSinceStartupAsDouble - startedRealtime) * 1000d;
            }
            state = StartState.Running;
            status = "開始済み（scnGame.Play 1回・介入なし）";
        }

        internal static void NotifyEditorPlayPostfix()
        {
            if (playRequestRealtime > 0d)
            {
                editorPreparationMs =
                    (Time.realtimeSinceStartupAsDouble - playRequestRealtime) * 1000d;
            }

            if (state == StartState.Passthrough)
            {
                return;
            }

            if (!playCallObserved)
            {
                // Diagnostics only: never turn a missing observation into a playback failure.
                status = observationPatchInstalled
                    ? "scnGame.Play未観測（本体処理には介入なし）"
                    : "観測パッチ未導入（本体処理には介入なし）";
            }
        }

        internal static Exception NotifyEditorPlayFinalizer(Exception exception)
        {
            if (exception != null)
            {
                state = StartState.Failed;
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
            state = StartState.Idle;
            status = "再生停止";
        }

        internal static void Reset(string reason)
        {
            Reset(reason, false);
        }

        // The second parameter is retained for source compatibility with v0.9.22 callers.
        // There is no pending Play call to invoke in v0.9.23.
        internal static void Reset(string reason, bool invokePendingFallback)
        {
            state = StartState.Idle;
            status = reason ?? "待機中";
            lastError = string.Empty;
            editorPreparationMs = 0d;
            gamePlayCallMs = 0d;
            playbackStartFloor = 0;
            playRequestFrame = 0;
            playRequestRealtime = 0d;
            observedPlayCallCount = 0;
            playCallObserved = false;
            actualPlaybackStartFloor = 0;
            sameFloorTakeoffStatus = reason ?? "待機中";
        }

        internal static void Shutdown()
        {
            state = StartState.Idle;
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
    }
}
