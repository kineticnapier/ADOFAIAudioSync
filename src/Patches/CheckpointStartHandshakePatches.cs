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
            CheckpointResidualSafetyPatch.Install();
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

    /// <summary>
    /// Protects the checkpoint handshake from treating a whole DSP-buffer observation jump
    /// as a real chart/audio offset. A 2048-sample buffer at 48 kHz is 42.7 ms, matching the
    /// rare fixed residual seen when AudioSource.timeSamples is published one buffer ahead.
    ///
    /// Buffer-sized residuals are forced through the existing retry path even when the user
    /// configured a wider residual limit. If every retry still lands on a buffer boundary,
    /// the final fallback keeps the known PlayScheduled origin instead of writing that
    /// suspicious timeSamples observation into dspTimeSong.
    /// </summary>
    internal static class CheckpointResidualSafetyPatch
    {
        private const string HarmonyId =
            "Kiner.ADOFAIAudioSync.CheckpointResidualSafety";

        private static readonly FieldInfo LastScheduleResidualMsField =
            AccessTools.Field(typeof(CheckpointStartHandshakeRuntime), "lastScheduleResidualMs");
        private static readonly FieldInfo LastExpectedSampleField =
            AccessTools.Field(typeof(CheckpointStartHandshakeRuntime), "lastExpectedSample");
        private static readonly FieldInfo StatusField =
            AccessTools.Field(typeof(CheckpointStartHandshakeRuntime), "status");

        private static bool installed;
        private static bool replacedFallbackSample;
        private static double replacedResidualMs;
        private static double replacedBufferMs;

        internal static void Install()
        {
            if (installed) return;

            try
            {
                MethodBase limitTarget = AccessTools.Method(
                    typeof(CheckpointStartHandshakeRuntime),
                    "GetMaxScheduleResidualMs");
                MethodBase alignTarget = AccessTools.Method(
                    typeof(CheckpointStartHandshakeRuntime),
                    "AlignAndRelease");
                MethodInfo limitPostfix = AccessTools.Method(
                    typeof(CheckpointResidualSafetyPatch),
                    "ResidualLimitPostfix");
                MethodInfo alignPrefix = AccessTools.Method(
                    typeof(CheckpointResidualSafetyPatch),
                    "AlignPrefix");
                MethodInfo alignPostfix = AccessTools.Method(
                    typeof(CheckpointResidualSafetyPatch),
                    "AlignPostfix");

                if (limitTarget == null || alignTarget == null || limitPostfix == null ||
                    alignPrefix == null || alignPostfix == null ||
                    LastScheduleResidualMsField == null || LastExpectedSampleField == null)
                {
                    if (Main.Logger != null)
                    {
                        Main.Logger.Warning(
                            "Checkpoint residual safety patch target was not found.");
                    }
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    limitTarget,
                    postfix: new HarmonyMethod(limitPostfix));
                harmony.Patch(
                    alignTarget,
                    prefix: new HarmonyMethod(alignPrefix),
                    postfix: new HarmonyMethod(alignPostfix));
                installed = true;

                if (Main.Logger != null)
                {
                    Main.Logger.Log(
                        "Checkpoint residual safety now rejects DSP-buffer-sized playhead jumps.");
                }
            }
            catch (Exception ex)
            {
                if (Main.Logger != null)
                {
                    Main.Logger.Warning(
                        "Checkpoint residual safety patch failed: " + ex);
                }
            }
        }

        private static void ResidualLimitPostfix(ref double __result)
        {
            double bufferMs = GetDspBufferMilliseconds();
            if (!IsFinite(bufferMs) || bufferMs <= 0d) return;

            // Leave ordinary small residual handling untouched, but make one full output
            // buffer unambiguously exceed the acceptance limit. 90% leaves room for the
            // small dspTime/timeSamples sampling jitter around a buffer boundary.
            double bufferSafeLimitMs = Math.Max(5d, bufferMs * 0.9d);
            __result = Math.Min(__result, bufferSafeLimitMs);
        }

        private static void AlignPrefix(double __1, ref int __2, bool __3)
        {
            replacedFallbackSample = false;
            if (!__3) return;

            double residualMs = ReadDoubleField(LastScheduleResidualMsField);
            double expectedSample = ReadDoubleField(LastExpectedSampleField);
            double bufferMs = GetDspBufferMilliseconds();
            if (!IsFinite(residualMs) || !IsFinite(expectedSample) ||
                !IsLikelyBufferQuantizedResidual(residualMs, bufferMs))
            {
                return;
            }

            __2 = (int)Math.Round(expectedSample);
            replacedFallbackSample = true;
            replacedResidualMs = residualMs;
            replacedBufferMs = bufferMs;
        }

        private static void AlignPostfix()
        {
            if (!replacedFallbackSample) return;

            replacedFallbackSample = false;
            try
            {
                if (StatusField != null)
                {
                    StatusField.SetValue(
                        null,
                        "途中再生: DSPバッファ境界残差を無視して予約時刻を維持");
                }
            }
            catch
            {
                // Status text is diagnostic only.
            }

            if (Main.Logger != null)
            {
                Main.Logger.Warning(
                    "Ignored a DSP-buffer-quantized checkpoint residual after retries: " +
                    replacedResidualMs.ToString("+0.0;-0.0;0.0") + " ms" +
                    " (buffer " + replacedBufferMs.ToString("0.0") +
                    " ms); kept the PlayScheduled origin.");
            }
        }

        private static bool IsLikelyBufferQuantizedResidual(
            double residualMs,
            double bufferMs)
        {
            if (!IsFinite(residualMs) || !IsFinite(bufferMs) || bufferMs <= 0d)
            {
                return false;
            }

            double absoluteMs = Math.Abs(residualMs);
            if (absoluteMs < bufferMs * 0.5d) return false;

            int multiple = Math.Max(1, (int)Math.Round(absoluteMs / bufferMs));
            if (multiple > 4) return false;

            double targetMs = multiple * bufferMs;
            double toleranceMs = Math.Max(1.5d, bufferMs * 0.08d);
            return Math.Abs(absoluteMs - targetMs) <= toleranceMs;
        }

        private static double GetDspBufferMilliseconds()
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
            return (double)Math.Max(1, dspBufferLength) / sampleRate * 1000d;
        }

        private static double ReadDoubleField(FieldInfo field)
        {
            try
            {
                if (field == null) return double.NaN;
                object value = field.GetValue(null);
                return value is double ? (double)value : double.NaN;
            }
            catch
            {
                return double.NaN;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
