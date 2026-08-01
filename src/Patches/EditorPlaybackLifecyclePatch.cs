using System;
using HarmonyLib;
using Kiner.ADOFAIAudioSync.Runtime;

namespace Kiner.ADOFAIAudioSync.Patches
{
    // Observe the stock editor playback lifecycle without rewriting its IL or suppressing
    // scnGame.Play. The latter is important because Harmony Postfix patches from other mods
    // run even when an original is skipped; suppressing and manually reinvoking Play therefore
    // exposes two lifecycle notifications for one editor start.
    [HarmonyPatch(typeof(scnEditor), "Play")]
    internal static class EditorPlayLifecyclePatch
    {
        private static void Prefix(scnEditor __instance)
        {
            CheckpointCountdownRuntime.BeginEditorPlay();
            AudioSyncRuntime.NotifyEditorPlayPrefix(__instance);
        }

        private static void Postfix()
        {
            AudioSyncRuntime.NotifyEditorPlayPostfix();
        }

        private static Exception Finalizer(Exception __exception)
        {
            return AudioSyncRuntime.NotifyEditorPlayFinalizer(__exception);
        }
    }

    [HarmonyPatch(typeof(scnGame), "Play", new Type[] { typeof(int), typeof(bool) })]
    internal static class GamePlayStartObservationPatch
    {
        private static void Prefix(int __0, out double __state)
        {
            __state = AudioSyncRuntime.NotifyGamePlayPrefix(__0);
        }

        private static void Postfix(double __state)
        {
            AudioSyncRuntime.NotifyGamePlayPostfix(__state);
        }
    }

    [HarmonyPatch(typeof(scnEditor), "SwitchToEditMode")]
    internal static class EditorStopLifecyclePatch
    {
        private static void Prefix()
        {
            AudioSyncRuntime.NotifyPlaybackStopped("scnEditor.SwitchToEditMode");
            Kiner.ADOFAIAudioSync.Timing.PlayErrorCorrectionRuntime.NotifyPlaybackStopped();
            AudioSyncLifecycleRuntime.NotifyStop("scnEditor.SwitchToEditMode");
        }
    }
}
