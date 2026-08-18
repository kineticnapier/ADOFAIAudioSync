using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kiner.ADOFAIAudioSync.Runtime;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Patches
{
    [HarmonyPatch(typeof(scrConductor), "ScrubMusicToTime", new Type[] { typeof(double) })]
    internal static class CheckpointScrubHandshakePatch
    {
        private static bool Prepare()
        {
            CheckpointDspWaitCompatibilityPatch.Install();
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

    /// <summary>
    /// CheckpointStartHandshakeRuntime waits for AudioSettings.dspTime to advance after
    /// resuming the listener and after the muted decoder-prime Stop. Those waits were
    /// originally capped at 12 render frames. At high frame rates 12 frames can be shorter
    /// than one DSP buffer (for example 2048 / 48000 Hz = 42.7 ms), so a healthy audio
    /// thread could be treated as stalled before it had a chance to publish its next update.
    ///
    /// The handshake coroutine is compiler-generated, so patch its two baked-in frame caps
    /// to a limit derived from the active DSP buffer period. This keeps the existing source
    /// flow intact while making the wait independent of render FPS.
    /// </summary>
    internal static class CheckpointDspWaitCompatibilityPatch
    {
        private const string HarmonyId =
            "Kiner.ADOFAIAudioSync.CheckpointDspWaitCompatibility";
        private static bool installed;

        internal static void Install()
        {
            if (installed) return;

            try
            {
                MethodBase target = FindHandshakeMoveNext();
                MethodInfo transpiler = AccessTools.Method(
                    typeof(CheckpointDspWaitCompatibilityPatch),
                    "Transpiler");
                if (target == null || transpiler == null)
                {
                    if (Main.Logger != null)
                    {
                        Main.Logger.Warning(
                            "Checkpoint DSP wait compatibility patch target was not found.");
                    }
                    return;
                }

                new Harmony(HarmonyId).Patch(
                    target,
                    transpiler: new HarmonyMethod(transpiler));
                installed = true;
            }
            catch (Exception ex)
            {
                if (Main.Logger != null)
                {
                    Main.Logger.Warning(
                        "Checkpoint DSP wait compatibility patch failed: " + ex);
                }
            }
        }

        private static MethodBase FindHandshakeMoveNext()
        {
            Type[] nestedTypes = typeof(CheckpointStartHandshakeRuntime).GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < nestedTypes.Length; i++)
            {
                Type nestedType = nestedTypes[i];
                if (!nestedType.Name.StartsWith(
                        "<HandshakeCoroutine>d__",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                MethodInfo moveNext = AccessTools.Method(nestedType, "MoveNext");
                if (moveNext != null) return moveNext;
            }
            return null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo helper = AccessTools.Method(
                typeof(CheckpointDspWaitCompatibilityPatch),
                "GetDspAdvanceFrameLimit");
            int replacements = 0;

            foreach (CodeInstruction instruction in instructions)
            {
                if (helper != null && replacements < 2 && IsIntConstant(instruction, 12))
                {
                    CodeInstruction replacement =
                        new CodeInstruction(OpCodes.Call, helper);
                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    replacements++;
                    yield return replacement;
                    continue;
                }

                yield return instruction;
            }

            if (Main.Logger != null)
            {
                if (replacements == 2)
                {
                    Main.Logger.Log(
                        "Checkpoint DSP wait limits now follow the active DSP buffer period.");
                }
                else
                {
                    Main.Logger.Warning(
                        "Checkpoint DSP wait compatibility patch replaced " + replacements +
                        "/2 frame limits; checkpoint start compatibility may be incomplete.");
                }
            }
        }

        private static bool IsIntConstant(CodeInstruction instruction, int value)
        {
            if (instruction.opcode == OpCodes.Ldc_I4_S)
            {
                try { return Convert.ToInt32(instruction.operand) == value; }
                catch { return false; }
            }
            if (instruction.opcode == OpCodes.Ldc_I4)
            {
                try { return Convert.ToInt32(instruction.operand) == value; }
                catch { return false; }
            }
            return value == 0 && instruction.opcode == OpCodes.Ldc_I4_0 ||
                   value == 1 && instruction.opcode == OpCodes.Ldc_I4_1 ||
                   value == 2 && instruction.opcode == OpCodes.Ldc_I4_2 ||
                   value == 3 && instruction.opcode == OpCodes.Ldc_I4_3 ||
                   value == 4 && instruction.opcode == OpCodes.Ldc_I4_4 ||
                   value == 5 && instruction.opcode == OpCodes.Ldc_I4_5 ||
                   value == 6 && instruction.opcode == OpCodes.Ldc_I4_6 ||
                   value == 7 && instruction.opcode == OpCodes.Ldc_I4_7 ||
                   value == 8 && instruction.opcode == OpCodes.Ldc_I4_8 ||
                   value == -1 && instruction.opcode == OpCodes.Ldc_I4_M1;
        }

        private static int GetDspAdvanceFrameLimit()
        {
            int dspBufferLength = 1024;
            try
            {
                int dspBufferCount;
                AudioSettings.GetDSPBufferSize(out dspBufferLength, out dspBufferCount);
            }
            catch
            {
                dspBufferLength = 1024;
            }

            int sampleRate = Math.Max(1, AudioSettings.outputSampleRate);
            double bufferSeconds =
                (double)Math.Max(1, dspBufferLength) / sampleRate;
            double waitSeconds = Math.Max(
                0.1d,
                Math.Min(0.5d, bufferSeconds * 3d));

            double frameSeconds = Time.unscaledDeltaTime;
            if (double.IsNaN(frameSeconds) || double.IsInfinity(frameSeconds) ||
                frameSeconds <= 0d)
            {
                frameSeconds = 1d / 240d;
            }

            int frames = (int)Math.Ceiling(waitSeconds / frameSeconds) + 2;
            return Math.Max(12, Math.Min(30000, frames));
        }
    }
}
