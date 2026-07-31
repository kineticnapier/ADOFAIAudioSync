using System;
using HarmonyLib;
using Kiner.ADOFAIAudioSync.Runtime;

namespace Kiner.ADOFAIAudioSync.Patches
{
    [HarmonyPatch(typeof(AudioManager), "StopAllSounds", new Type[] { })]
    internal static class AudioManagerStopLifecyclePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(AudioManager), "StopAllSounds", new Type[] { }) != null;
        }

        private static void Postfix()
        {
            AudioSyncLifecycleRuntime.NotifyStop("AudioManager.StopAllSounds", false);
        }
    }

    [HarmonyPatch(typeof(scrConductor), "KillAllSounds", new Type[] { })]
    internal static class ConductorKillSoundsLifecyclePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(scrConductor), "KillAllSounds", new Type[] { }) != null;
        }

        private static void Prefix()
        {
            AudioSyncLifecycleRuntime.NotifyStop("scrConductor.KillAllSounds", false);
        }
    }

    [HarmonyPatch(typeof(scrController), "Restart", new Type[] { typeof(bool) })]
    internal static class ControllerRestartLifecyclePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(scrController), "Restart", new Type[] { typeof(bool) }) != null;
        }

        private static void Prefix()
        {
            AudioSyncRuntime.NotifyPlaybackStopped("scrController.Restart");
            AudioSyncLifecycleRuntime.NotifyStop("scrController.Restart", false);
        }
    }

    [HarmonyPatch(typeof(scrController), "FailAction",
        new Type[] { typeof(bool), typeof(bool), typeof(string), typeof(bool) })]
    internal static class ControllerFailLifecyclePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(scrController), "FailAction",
                new Type[] { typeof(bool), typeof(bool), typeof(string), typeof(bool) }) != null;
        }

        private static void Postfix()
        {
            AudioSyncRuntime.NotifyPlaybackStopped("scrController.FailAction");
            AudioSyncLifecycleRuntime.NotifyStop("scrController.FailAction", false);
        }
    }
}
