using System;
using HarmonyLib;
using Kiner.ADOFAIAudioSync.Runtime;

namespace Kiner.ADOFAIAudioSync.Patches
{
    // Do not rewrite scnEditor.Play's IL. Modified ADOFAI builds often add their own
    // transpilers, and stacking another transpiler can produce an invalid evaluation stack.
    // Instead, mark the duration of scnEditor.Play and intercept scnGame.Play at its own
    // method boundary while that duration is active.
    [HarmonyPatch(typeof(scnEditor), "Play")]
    internal static class EditorPlayLifecyclePatch
    {
        private static void Prefix(scnEditor __instance)
        {
            CheckpointCountdownRuntime.BeginEditorPlay();
            AudioSyncRuntime.NotifyEditorPlayPrefix(
                __instance,
                AudioSyncRuntime.ResolveStartFloor(__instance));
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
    internal static class GamePlayStartGatePatch
    {
        private static bool Prefix(scnGame __instance, ref int __0, bool __1)
        {
            return AudioSyncRuntime.ShouldRunGamePlayNow(__instance, ref __0, __1);
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
