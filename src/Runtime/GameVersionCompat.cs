using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LevelEvent = ADOFAI.LevelEvent;

namespace Kiner.ADOFAIAudioSync.Runtime
{
    /// <summary>
    /// Bridges game API moves between ADOFAI 2.9.x and 3.3.x without binding the
    /// compiled mod to only one side of the changed member signature.
    /// </summary>
    internal static class GameVersionCompat
    {
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo ConductorLastHitField =
            AccessTools.Field(typeof(scrConductor), "lastHit");

        private static readonly AccessTools.FieldRef<LevelEvent, Dictionary<string, object>>
            EventDataRef = AccessTools.FieldRefAccess<LevelEvent, Dictionary<string, object>>(
                "data");

        private static readonly PropertyInfo PlayerManagerProperty =
            typeof(ADOBase).GetProperty("playerManager", StaticMembers);

        private static readonly Type PlayerManagerType = PlayerManagerProperty == null
            ? null
            : PlayerManagerProperty.PropertyType;

        private static readonly PropertyInfo PlayersProperty = PlayerManagerType == null
            ? null
            : PlayerManagerType.GetProperty("players", InstanceMembers);

        private static readonly FieldInfo AllPlayersField = PlayerManagerType == null
            ? null
            : AccessTools.Field(PlayerManagerType, "allPlayers");

        private static readonly Type PlayerType = AccessTools.TypeByName("scrPlayer");

        private static readonly FieldInfo PlayerLastHitField = PlayerType == null
            ? null
            : AccessTools.Field(PlayerType, "lastHit");

        private static bool warningLogged;

        internal static IDictionary<string, object> GetEventData(this LevelEvent source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return EventDataRef(source);
        }

        internal static void SetLastHit(scrConductor instance, double value)
        {
            bool updated = false;

            try
            {
                // ADOFAI 2.9.x stored this clock on scrConductor.
                if (instance != null && ConductorLastHitField != null)
                {
                    ConductorLastHitField.SetValue(instance, value);
                    updated = true;
                }

                // ADOFAI 3.3.x moved it to every active scrPlayer.
                if (PlayerManagerProperty != null && PlayerLastHitField != null)
                {
                    object playerManager = PlayerManagerProperty.GetValue(null, null);
                    IEnumerable players = playerManager as IEnumerable;
                    if (players == null && playerManager != null && PlayersProperty != null)
                    {
                        players = PlayersProperty.GetValue(playerManager, null) as IEnumerable;
                    }
                    if (players == null && playerManager != null && AllPlayersField != null)
                    {
                        players = AllPlayersField.GetValue(playerManager) as IEnumerable;
                    }
                    if (players != null)
                    {
                        foreach (object player in players)
                        {
                            if (player == null || !PlayerType.IsInstanceOfType(player))
                            {
                                continue;
                            }

                            PlayerLastHitField.SetValue(player, value);
                            updated = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce("lastHit compatibility update failed: " + ex.Message);
                return;
            }

            if (!updated)
            {
                LogWarningOnce("No compatible lastHit target was found.");
            }
        }

        private static void LogWarningOnce(string message)
        {
            if (warningLogged)
            {
                return;
            }

            warningLogged = true;
            if (Main.Logger != null)
            {
                Main.Logger.Warning(message);
            }
        }
    }
}
