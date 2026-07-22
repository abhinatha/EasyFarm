// ///////////////////////////////////////////////////////////////////
// Camp mode helpers: a stationary anchor point the bot returns to
// after each kill. Resting and trust summoning happen at camp.
// ///////////////////////////////////////////////////////////////////

using System;
using EasyFarm.UserSettings;
using MemoryAPI;
using MemoryAPI.Navigation;

namespace EasyFarm.Classes
{
    public static class CampService
    {
        /// <summary>Distance considered "at camp".</summary>
        public const double ArrivalTolerance = 3.0;

        public static bool Active(IConfig config)
        {
            return config.IsCampEnabled && config.IsCampSet;
        }

        public static bool InCampZone(IMemoryAPI fface, IConfig config)
        {
            return config.CampZone == fface.Player.Zone;
        }

        public static double DistanceToCamp(IMemoryAPI fface, IConfig config)
        {
            var player = fface.Player.Position;
            var camp = config.CampPosition;
            return Math.Sqrt(Math.Pow(camp.X - player.X, 2) + Math.Pow(camp.Z - player.Z, 2));
        }

        public static bool AtCamp(IMemoryAPI fface, IConfig config)
        {
            return DistanceToCamp(fface, config) <= ArrivalTolerance;
        }

        /// <summary>Take one navigation step toward the camp position.</summary>
        public static void WalkToCamp(IMemoryAPI fface, IConfig config)
        {
            if (!InCampZone(fface, config)) return;
            fface.Navigator.DistanceTolerance = 2;
            fface.Navigator.GotoWaypoint(
                config.CampPosition,
                config.IsObjectAvoidanceEnabled,
                false);
        }
    }
}
