// ///////////////////////////////////////////////////////////////////
// Temporary per-mob blacklist keyed by server id. Used when a fight
// stalemates (target takes no damage while we do) so the bot moves on
// instead of dying to an unhittable mob.
// ///////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

namespace EasyFarm.Classes
{
    public static class TargetBlacklist
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, DateTime> Entries = new Dictionary<int, DateTime>();

        public static void Add(int id, int minutes)
        {
            lock (Sync)
            {
                Entries[id] = DateTime.Now.AddMinutes(minutes);
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Entries.Clear();
            }
        }

        public static bool IsBlacklisted(int id)
        {
            lock (Sync)
            {
                DateTime until;
                if (!Entries.TryGetValue(id, out until)) return false;
                if (DateTime.Now < until) return true;
                Entries.Remove(id);
                return false;
            }
        }
    }
}
