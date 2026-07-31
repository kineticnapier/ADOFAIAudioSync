using UnityEngine.SceneManagement;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Central cleanup route for every independent AudioSource owned by the mod.
    /// Mirrors the lifecycle coverage used by robust rendered-audio mods: stop,
    /// restart, failure, scene unload, disable and unload all converge here.
    /// </summary>
    internal static class AudioSyncLifecycleRuntime
    {
        private static bool sceneHookInstalled;
        private static string lastReason = "-";

        internal static string LastReason { get { return lastReason; } }

        internal static void Initialize()
        {
            if (!sceneHookInstalled)
            {
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                sceneHookInstalled = true;
            }
        }

        internal static void NotifyStop(string reason)
        {
            lastReason = reason ?? "stop";
            // Generic sound-effect cleanup also occurs inside normal checkpoint setup and
            // PlayHitTimes. Only definite playback/scene stops should cancel the music
            // playhead handshake.
            if (lastReason != "AudioManager.StopAllSounds" &&
                lastReason != "scrConductor.KillAllSounds")
            {
                CheckpointStartHandshakeRuntime.NotifyStop(lastReason);
                CheckpointCountdownRuntime.Reset(lastReason);
            }
            OggAudioCacheRuntime.NotifyLifecycleStop();
        }

        internal static void Shutdown()
        {
            NotifyStop("mod unload");
            if (sceneHookInstalled)
            {
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                sceneHookInstalled = false;
            }
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            NotifyStop("scene unloaded: " + scene.name);
        }
    }
}
