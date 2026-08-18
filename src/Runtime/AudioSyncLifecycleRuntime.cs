using System;
using System.Reflection;
using System.Reflection.Emit;
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
        private static EventInfo sceneUnloadedEvent;
        private static Delegate sceneUnloadedHandler;
        private static string lastReason = "-";

        internal static string LastReason { get { return lastReason; } }

        internal static void Initialize()
        {
            if (sceneHookInstalled) return;

            try
            {
                sceneUnloadedEvent = typeof(SceneManager).GetEvent(
                    "sceneUnloaded",
                    BindingFlags.Public | BindingFlags.Static);
                if (sceneUnloadedEvent == null || sceneUnloadedEvent.EventHandlerType == null)
                {
                    WarnSceneHook("SceneManager.sceneUnloaded was not found; scene-unload cleanup is disabled.");
                    return;
                }

                MethodInfo invoke = sceneUnloadedEvent.EventHandlerType.GetMethod("Invoke");
                ParameterInfo[] parameters = invoke == null ? null : invoke.GetParameters();
                if (invoke == null || invoke.ReturnType != typeof(void) ||
                    parameters == null || parameters.Length != 1)
                {
                    WarnSceneHook("SceneManager.sceneUnloaded has an unexpected delegate shape; scene-unload cleanup is disabled.");
                    return;
                }

                MethodInfo target = typeof(AudioSyncLifecycleRuntime).GetMethod(
                    "OnSceneUnloadedObject",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (target == null)
                {
                    WarnSceneHook("Scene-unload bridge target was not found; scene-unload cleanup is disabled.");
                    return;
                }

                Type sceneType = parameters[0].ParameterType;
                DynamicMethod bridge = new DynamicMethod(
                    "ADOFAIAudioSync_SceneUnloadedBridge",
                    typeof(void),
                    new Type[] { sceneType },
                    typeof(AudioSyncLifecycleRuntime),
                    true);
                ILGenerator il = bridge.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                if (sceneType.IsValueType) il.Emit(OpCodes.Box, sceneType);
                il.Emit(OpCodes.Call, target);
                il.Emit(OpCodes.Ret);

                sceneUnloadedHandler = bridge.CreateDelegate(sceneUnloadedEvent.EventHandlerType);
                sceneUnloadedEvent.AddEventHandler(null, sceneUnloadedHandler);
                sceneHookInstalled = true;
            }
            catch (Exception ex)
            {
                sceneUnloadedEvent = null;
                sceneUnloadedHandler = null;
                sceneHookInstalled = false;
                WarnSceneHook("SceneManager.sceneUnloaded hook failed; scene-unload cleanup is disabled: " + ex.Message);
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
            if (!sceneHookInstalled) return;

            try
            {
                if (sceneUnloadedEvent != null && sceneUnloadedHandler != null)
                {
                    sceneUnloadedEvent.RemoveEventHandler(null, sceneUnloadedHandler);
                }
            }
            catch (Exception ex)
            {
                WarnSceneHook("SceneManager.sceneUnloaded unhook failed: " + ex.Message);
            }
            finally
            {
                sceneUnloadedEvent = null;
                sceneUnloadedHandler = null;
                sceneHookInstalled = false;
            }
        }

        private static void OnSceneUnloadedObject(object scene)
        {
            string sceneName = "?";
            try
            {
                if (scene != null)
                {
                    PropertyInfo nameProperty = scene.GetType().GetProperty(
                        "name",
                        BindingFlags.Public | BindingFlags.Instance);
                    object value = nameProperty == null ? null : nameProperty.GetValue(scene, null);
                    if (value != null) sceneName = value.ToString();
                }
            }
            catch
            {
                // Scene identity is diagnostic only; cleanup must still happen.
            }

            NotifyStop("scene unloaded: " + sceneName);
        }

        private static void WarnSceneHook(string message)
        {
            if (Main.Logger != null) Main.Logger.Warning(message);
        }
    }
}
