// ///////////////////////////////////////////////////////////////////
// This file is a part of EasyFarm for Final Fantasy XI.
// ///////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using MemoryAPI;

namespace EasyFarm.Classes
{
    /// <summary>
    ///     Entity indices of everyone in our party (self, trusts, players),
    ///     used to decide whether a mob's current target is one of ours.
    ///     Cached briefly - party composition changes rarely.
    /// </summary>
    public static class PartyIndex
    {
        private static HashSet<int> _cached = new HashSet<int>();
        private static DateTime _stamp = DateTime.MinValue;

        public static HashSet<int> Get(IMemoryAPI fface)
        {
            if (DateTime.Now < _stamp.AddSeconds(1)) return _cached;

            var set = new HashSet<int>();
            try
            {
                foreach (var pm in fface.PartyMember.Values)
                {
                    if (pm == null || !pm.UnitPresent) continue;
                    var idx = pm.TargetIndex;
                    if (idx > 0) set.Add(idx);
                }
            }
            catch
            {
                // On failure keep whatever we last knew.
                return _cached;
            }

            _cached = set;
            _stamp = DateTime.Now;
            return _cached;
        }

        /// <summary>
        ///     Diagnostic: where does this unit's target pointer point?
        ///     "ours" = a member of our party, "none" = nothing, otherwise
        ///     the raw entity index. Validation data for possibly promoting
        ///     TargetingIndex into the attacker-detection path.
        /// </summary>
        public static string DescribeTargeting(IMemoryAPI fface, IUnit unit)
        {
            try
            {
                var tidx = unit.TargetingIndex;
                if (tidx <= 0) return "none";
                return Get(fface).Contains(tidx) ? "ours" : "other:" + tidx;
            }
            catch
            {
                return "err";
            }
        }

        /// <summary>
        ///     True when this unit is actively fighting and its current
        ///     target is a member of our party. This is ground truth for
        ///     "attacking us" - unlike claim/status heuristics it cannot
        ///     false-positive on other players' loose or returning mobs.
        /// </summary>
        public static bool IsAttackingParty(IMemoryAPI fface, IUnit unit)
        {
            if (unit == null || !unit.IsActive || unit.IsDead) return false;
            if (!unit.Status.Equals(Status.Fighting)) return false;
            var tidx = unit.TargetingIndex;
            return tidx > 0 && Get(fface).Contains(tidx);
        }
    }
}
