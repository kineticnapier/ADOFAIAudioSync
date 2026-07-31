using System;
using HarmonyLib;
using Kiner.ADOFAIAudioSync.Runtime;

namespace Kiner.ADOFAIAudioSync.Patches
{
    [HarmonyPatch(typeof(scrConductor), "ScrubMusicToTime", new Type[] { typeof(double) })]
    internal static class CheckpointScrubHandshakePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(scrConductor), "ScrubMusicToTime", new Type[] { typeof(double) }) != null;
        }

        private static bool Prefix(scrConductor __instance, double __0)
        {
            return CheckpointStartHandshakeRuntime.ShouldRunStockScrub(__instance, __0);
        }
    }

    [HarmonyPatch(typeof(scrConductor), "Update", new Type[] { })]
    internal static class ConductorCheckpointFreezePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(scrConductor), "Update", new Type[] { }) != null;
        }

        private static void Prefix(scrConductor __instance)
        {
            CheckpointStartHandshakeRuntime.BeforeConductorUpdate(__instance);
        }
    }
}
