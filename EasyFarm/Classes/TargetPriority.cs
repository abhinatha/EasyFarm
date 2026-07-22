// ///////////////////////////////////////////////////////////////////
// This file is a part of EasyFarm for Final Fantasy XI.
// ///////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using MemoryAPI;

namespace EasyFarm.Classes
{
    public static class TargetPriority
    {
        /// <summary>
        ///     Radius within which another live mob counts as a neighbor.
        ///     Roughly the detection radius of clustered stationary mobs:
        ///     fighting inside it wakes them.
        /// </summary>
        private const double NeighborRadius = 8.0;

        public static IOrderedEnumerable<IUnit> Prioritize(IEnumerable<IUnit> units)
        {
            return units.OrderByDescending(x => x.PartyClaim)
                .ThenByDescending(x => x.HasAggroed)
                .ThenBy(x => x.Distance);
        }

        /// <summary>
        ///     Isolation-aware ordering: among equal-priority candidates,
        ///     prefer the mob with the fewest OTHER live mobs standing
        ///     within NeighborRadius of it, so clusters are eaten from the
        ///     edge inward instead of fighting in the middle of them.
        ///     Claim/aggro priority still dominates; distance breaks ties.
        /// </summary>
        public static IOrderedEnumerable<IUnit> Prioritize(
            IEnumerable<IUnit> units, IEnumerable<IUnit> allMobs)
        {
            var candidates = units.ToList();
            var field = (allMobs ?? candidates)
                .Where(x => x != null && x.IsActive && !x.IsDead)
                .Select(x => new { x.Id, x.PosX, x.PosZ })
                .ToList();

            Func<IUnit, int> neighbors = m =>
            {
                try
                {
                    return field.Count(o =>
                        o.Id != m.Id &&
                        Math.Sqrt((o.PosX - m.PosX) * (o.PosX - m.PosX) +
                                  (o.PosZ - m.PosZ) * (o.PosZ - m.PosZ)) < NeighborRadius);
                }
                catch
                {
                    return 0;
                }
            };

            return candidates.OrderByDescending(x => x.PartyClaim)
                .ThenByDescending(x => x.HasAggroed)
                .ThenBy(neighbors)
                .ThenBy(x => x.Distance);
        }
    }
}
