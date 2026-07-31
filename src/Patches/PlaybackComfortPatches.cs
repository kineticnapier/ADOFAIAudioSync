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

        private static void Prefix(
            int __0,
            out CheckpointCountdownRuntime.ScrubScope __state)
        {
            __state = CheckpointCountdownRuntime.BeforeScrub(__0);
        }

        private static void Postfix(
            CheckpointCountdownRuntime.ScrubScope __state)
        {
            CheckpointCountdownRuntime.EndScrub(__state);
        }

        private static Exception Finalizer(
            Exception __exception,
            CheckpointCountdownRuntime.ScrubScope __state)
        {
            // Postfix does not run when the original method throws. EndScrub is
            // idempotent, so the normal path is safe when Harmony also invokes
            // this finalizer after Postfix.
            CheckpointCountdownRuntime.EndScrub(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(scrConductor), "PlayHitTimes", new Type[] { })]
    internal static class WaitBeatsCountdownIsolationPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(
                       typeof(scrConductor),
                       "PlayHitTimes",
                       new Type[] { }) != null &&
                   AccessTools.Field(
                       typeof(scrConductor),
                       "countdownSpeedMultiplier") != null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo multiplierField = AccessTools.Field(
                typeof(scrConductor),
                "countdownSpeedMultiplier");
            MethodInfo helper = AccessTools.Method(
                typeof(CheckpointCountdownRuntime),
                "GetWaitBeatsTimelineMultiplier");
            int replacements = 0;

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldfld &&
                    object.Equals(instruction.operand, multiplierField))
                {
                    // The scrConductor instance already on the evaluation stack
                    // becomes the helper argument, and the helper returns the
                    // float expected by the original division.
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = helper;
                    replacements++;
                }
                yield return instruction;
            }

            CheckpointCountdownRuntime.SetWaitBeatsTimelinePatchInstalled(
                replacements > 0);
        }
    }

    [HarmonyPatch(typeof(scrController), "PlayerControl_Enter",
        new Type[] { })]
    internal static class CountdownMultiplierReleasePatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(
                typeof(scrController),
                "PlayerControl_Enter",
                new Type[] { }) != null;
        }

        private static void Prefix()
        {
            CheckpointCountdownRuntime.OnPlayerControlEnter();
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
