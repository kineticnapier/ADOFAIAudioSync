using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kiner.ADOFAIAudioSync.Runtime;

namespace Kiner.ADOFAIAudioSync.Patches
{
    [HarmonyPatch(typeof(scrController), "Scrub",
        new Type[] { typeof(int), typeof(bool) })]
    internal static class CheckpointCountdownFoldPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(scrController), "Scrub",
                new Type[] { typeof(int), typeof(bool) }) != null;
        }

        private static void Prefix(int __0)
        {
            CheckpointCountdownRuntime.BeforeScrub(__0);
        }
    }

    [HarmonyPatch(typeof(AudioManager), "FindOrLoadAudioClipExternal",
        new Type[] { typeof(string), typeof(bool), typeof(float) })]
    internal static class ExternalOggCachePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(AudioManager), "FindOrLoadAudioClipExternal",
                new Type[] { typeof(string), typeof(bool), typeof(float) }) != null;
        }

        private static void Postfix(
            AudioManager __instance,
            string __0,
            ref IEnumerator __result)
        {
            __result = OggAudioCacheRuntime.WrapExternalLoad(__instance, __0, __result);
        }
    }

    /// <summary>
    /// ADOFAI sets DownloadHandlerAudioClip.streamAudio inside the compiler-generated
    /// iterator. Replace only the boolean value passed to that setter; other formats
    /// and all other UnityWebRequests retain the game's original behavior.
    /// </summary>
    [HarmonyPatch]
    internal static class ExternalOggNonStreamingPatch
    {
        private static MethodBase TargetMethod()
        {
            Type[] nestedTypes = typeof(AudioManager).GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < nestedTypes.Length; i++)
            {
                Type nestedType = nestedTypes[i];
                if (nestedType.Name.StartsWith(
                        "<FindOrLoadAudioClipExternal>d__",
                        StringComparison.Ordinal))
                {
                    return AccessTools.Method(nestedType, "MoveNext");
                }
            }
            return null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo helper = AccessTools.Method(
                typeof(OggAudioCacheRuntime),
                "OverrideStreamAudio");
            List<CodeInstruction> result = new List<CodeInstruction>();
            int replacements = 0;

            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo calledMethod = instruction.operand as MethodInfo;
                if (calledMethod != null &&
                    calledMethod.Name == "set_streamAudio" &&
                    calledMethod.DeclaringType != null &&
                    calledMethod.DeclaringType.FullName ==
                    "UnityEngine.Networking.DownloadHandlerAudioClip")
                {
                    CodeInstruction helperCall =
                        new CodeInstruction(OpCodes.Call, helper);
                    helperCall.labels.AddRange(instruction.labels);
                    instruction.labels.Clear();
                    helperCall.blocks.AddRange(instruction.blocks);
                    instruction.blocks.Clear();
                    result.Add(helperCall);
                    replacements++;
                }
                result.Add(instruction);
            }

            OggAudioCacheRuntime.SetStreamOverridePatchInstalled(replacements > 0);
            return result;
        }
    }
}
