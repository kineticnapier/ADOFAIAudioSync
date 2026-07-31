using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Optional audible probe scheduled from the same absolute floor DSP formula used by
    /// ADOFAI Access: conductor.dspTimeSongPosZero + floor.entryTimePitchAdj.
    /// It is disabled by default and exists only for timing diagnosis.
    /// </summary>
    internal static class DspProbeCueRuntime
    {
        private sealed class Voice
        {
            public GameObject GameObject;
            public AudioSource Source;
            public double BusyUntil;
        }

        private const int MaxVoices = 32;
        private const int SampleRate = 48000;
        private const float ClipLengthSeconds = 0.035f;
        private static readonly List<Voice> voices = new List<Voice>();
        private static readonly HashSet<int> scheduledFloors = new HashSet<int>();
        private static GameObject root;
        private static AudioClip clickClip;
        private static scrConductor lastConductor;
        private static AudioClip lastSongClip;
        private static int lastCurrentFloor = -1;
        private static string status = "OFF";
        private static int scheduledCount;
        private static double nextPumpDsp;
        private static bool probeEnabledLastFrame;

        private const double PumpIntervalSeconds = 0.025d;
        private const int MaxQueuePerPump = 24;

        internal static string Status { get { return status; } }
        internal static int ScheduledCount { get { return scheduledCount; } }
        internal static int VoiceCount { get { return voices.Count; } }

        internal static void Initialize()
        {
            status = "OFF";
            nextPumpDsp = 0d;
        }

        internal static void PrewarmClip()
        {
            EnsureRootOnly();
            EnsureClickClip();
        }

        // Returns true when the requested pool size has been reached. Only one voice is
        // created per call so AudioSyncPrewarmRuntime can spread work over idle frames.
        internal static bool PrewarmOneVoice(int targetCount)
        {
            targetCount = Mathf.Clamp(targetCount, 1, MaxVoices);
            EnsureRootOnly();
            EnsureClickClip();
            if (voices.Count < targetCount) AddVoice();
            return voices.Count >= targetCount;
        }

        internal static void Update()
        {
            AudioSyncSettings settings = Main.Settings;
            if (!Main.Enabled || settings == null || !settings.EnableDspProbeCue)
            {
                if (probeEnabledLastFrame || scheduledFloors.Count > 0) StopAll("probe disabled", false);
                probeEnabledLastFrame = false;
                status = "OFF";
                return;
            }
            probeEnabledLastFrame = true;

            scrConductor conductor = scrConductor.instance;
            if (conductor == null || conductor.song == null || conductor.song.clip == null ||
                !conductor.song.isPlaying || ADOBase.lm == null || ADOBase.lm.listFloors == null)
            {
                status = "再生待ち";
                return;
            }

            if (conductor != lastConductor || conductor.song.clip != lastSongClip)
            {
                ResetSchedule("new source");
                lastConductor = conductor;
                lastSongClip = conductor.song.clip;
            }

            EnsureAudioObjects();
            if (clickClip == null) return;

            double pumpNow = conductor.dspTime;
            if (pumpNow < nextPumpDsp) return;
            nextPumpDsp = pumpNow + PumpIntervalSeconds;

            scrController controller = scrController.instance;
            int current = controller == null ? 0 : controller.currentSeqID;
            if (current < lastCurrentFloor - 2)
            {
                ResetSchedule("seek backward");
            }
            lastCurrentFloor = current;

            double now = conductor.dspTime;
            double horizon = Math.Max(0.25d, Math.Min(10d, settings.DspProbeLookAheadSeconds));
            double latest = now + horizon;
            int start = Math.Max(1, current - 1);
            int count = ADOBase.lm.listFloors.Count;
            int queuedThisFrame = 0;

            for (int i = start; i < count && queuedThisFrame < MaxQueuePerPump; i++)
            {
                scrFloor floor = ADOBase.lm.listFloors[i];
                if (floor == null) continue;
                double due = conductor.dspTimeSongPosZero + floor.entryTimePitchAdj;
                if (due > latest) break;
                if (due < now + 0.025d) continue;
                if (floor.auto || floor.midSpin) continue;
                if (scheduledFloors.Contains(i)) continue;

                Voice voice = AcquireVoice(now);
                if (voice == null)
                {
                    status = "音源プール不足";
                    break;
                }

                AudioSource source = voice.Source;
                source.clip = clickClip;
                source.volume = Mathf.Clamp01(settings.DspProbeCueVolume);
                source.panStereo = -0.65f;
                source.pitch = 1f;
                source.timeSamples = 0;
                source.PlayScheduled(due);
                voice.BusyUntil = due + ClipLengthSeconds + 0.05d;
                scheduledFloors.Add(i);
                scheduledCount++;
                queuedThisFrame++;
            }

            if (scheduledFloors.Count > 4096)
            {
                scheduledFloors.RemoveWhere(delegate(int floor) { return floor < current - 64; });
            }
            status = "予約済み " + scheduledFloors.Count + "床 / horizon " + horizon.ToString("0.0") + "s";
        }

        internal static void ResetSchedule(string reason)
        {
            scheduledFloors.Clear();
            lastCurrentFloor = -1;
            nextPumpDsp = 0d;
            for (int i = 0; i < voices.Count; i++)
            {
                Voice voice = voices[i];
                if (voice.Source != null) voice.Source.Stop();
                voice.BusyUntil = 0d;
            }
            status = reason ?? "リセット";
        }

        internal static void StopAll(string reason, bool destroy)
        {
            ResetSchedule(reason);
            lastConductor = null;
            lastSongClip = null;
            probeEnabledLastFrame = false;
            if (!destroy) return;

            for (int i = 0; i < voices.Count; i++)
            {
                Voice voice = voices[i];
                if (voice.GameObject != null) UnityEngine.Object.Destroy(voice.GameObject);
            }
            voices.Clear();
            if (clickClip != null) UnityEngine.Object.Destroy(clickClip);
            clickClip = null;
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
        }

        internal static void Shutdown()
        {
            StopAll("終了", true);
        }

        private static void EnsureAudioObjects()
        {
            if (root == null)
            {
                root = new GameObject("ADOFAIAudioSync DSP Probe");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.hideFlags = HideFlags.HideAndDontSave;
            }
            EnsureClickClip();
            if (voices.Count == 0)
            {
                for (int i = 0; i < 4; i++) voices.Add(CreateVoice());
            }
        }


        private static void EnsureClickClip()
        {
            if (clickClip != null) return;
            int samples = Mathf.CeilToInt(SampleRate * ClipLengthSeconds);
            float[] data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)SampleRate;
                float envelope = 1f - i / (float)samples;
                data[i] = Mathf.Sin(2f * Mathf.PI * 1760f * t) * envelope * 0.28f;
            }
            clickClip = AudioClip.Create("ADOFAIAudioSync_DSP_Probe", samples, 1, SampleRate, false);
            clickClip.SetData(data, 0);
        }

        private static Voice AcquireVoice(double now)
        {
            for (int i = 0; i < voices.Count; i++)
            {
                Voice voice = voices[i];
                if (voice.Source != null && voice.BusyUntil <= now + 0.0001d && !voice.Source.isPlaying)
                    return voice;
            }
            if (voices.Count >= MaxVoices) return null;
            return AddVoice();
        }

        private static Voice AddVoice()
        {
            Voice voice = CreateVoice();
            voices.Add(voice);
            return voice;
        }

        private static Voice CreateVoice()
        {
            EnsureRootOnly();
            GameObject go = new GameObject("Probe Voice " + voices.Count);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.SetParent(root.transform, false);
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.priority = 32;
            return new Voice { GameObject = go, Source = source, BusyUntil = 0d };
        }

        private static void EnsureRootOnly()
        {
            if (root != null) return;
            root = new GameObject("ADOFAIAudioSync DSP Probe");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;
        }
    }
}
