// ///////////////////////////////////////////////////////////////////
// This file is a part of EasyFarm for Final Fantasy XI
// Copyright (C) 2013-2017 Mykezero
// 
// EasyFarm is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// EasyFarm is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// If not, see <http://www.gnu.org/licenses/>.
// ///////////////////////////////////////////////////////////////////

using System;
using System.Linq;
using EasyFarm.Classes;
using EasyFarm.UserSettings;
using EasyFarm.ViewModels;
using MemoryAPI;

namespace EasyFarm.States
{
    public class SetTargetState : AgentState
    {
        private DateTime? lastTargetCheck;

        // Last moment we were observed in combat; anchors the pull
        // cooldown at "time since combat ended".
        private static DateTime _lastCombat = DateTime.MinValue;

        public SetTargetState(StateMemory memory) : base(memory)
        {
        }

        public override bool Check()
        {
            // Sticky target: never acquire a new target while engaged with a
            // live one. Prevents pulling a second enemy mid-fight.
            if (IsEngagedWithLiveTarget) return false;

            // Chat-commanded pull hold: no new target acquisition.
            if (ChatCommands.ShouldHoldPulls(EliteApi)) return false;

            // Track combat presence for the pull cooldown anchor.
            if (EliteApi.Player.Status.Equals(Status.Fighting))
                _lastCombat = DateTime.Now;

            // Aggro override: if a valid mob is already fighting us and our
            // current (not yet engaged) pick isn't, switch to the attacker.
            // Evaluated every pass - the normal selection below can race a
            // fresh add's status flip and pick a bystander by distance,
            // after which the sticky rule holds the wrong mob while the
            // attacker kills the trusts one fight at a time.
            // Attacker validity deliberately bypasses MobFilter: detection
            // distance and camp radius must not hide a mob that is already
            // hitting us (observed: attacker at 7.4y with DetectionDistance
            // 5 stayed invisible while killing the trusts).
            var attacker = UnitService.MobArray
                .Where(x => x.IsActive && !x.IsDead && x.HasAggroed)
                .Where(x => x.Distance < 30)
                .Where(x => !TargetBlacklist.IsBlacklisted(x.Id))
                .OrderBy(x => x.Distance)
                .FirstOrDefault();


            if (attacker != null &&
                (Target == null || Target.Id != attacker.Id) &&
                !(Target != null && !Target.IsDead && Target.HasAggroed))
            {
                Target = attacker;
                Diagnostics.CombatDiag.Event(string.Format(
                    "AGGRO OVERRIDE: targeting attacker {0}[{1}] d:{2:F1} tgt:{3}",
                    attacker.Name, attacker.Id, attacker.Distance,
                    PartyIndex.DescribeTargeting(EliteApi, attacker)));
                LogViewModel.Write("Aggro override: targeting " + attacker.Name + " : " + attacker.Id);
                return false;
            }

            // Keep an attacker target: the normal reselection below uses
            // MobFilter and would swap it back to a filtered bystander.
            if (TargetIsAttacker) return false;

            // Currently fighting, do not change target. 
            if (!UnitFilters.MobFilter(EliteApi, Target, Config))
            {
                // Still not time to update for new target. 
                if (!ShouldCheckTarget()) return false;

                // Pull cooldown: keep the quiet window after combat open so
                // resting and trust resummons get their turn. Attackers are
                // handled above and are never delayed by this.
                if (Config.PullCooldown > 0 &&
                    DateTime.Now < _lastCombat.AddSeconds(Config.PullCooldown))
                    return false;

                // First get the first mob by distance.
                var mobs = UnitService.MobArray.Where(x => UnitFilters.MobFilter(EliteApi, x, Config)).ToList();
                // Isolation-aware: prefer candidates with the fewest live
                // neighbors so the bot fights at the edge of a cluster
                // instead of inside it (stationary mobs wake when the party
                // fights within their detection radius).
                mobs = TargetPriority.Prioritize(mobs, UnitService.MobArray).ToList();

                // Set our new target at the end so that we don't accidentaly cast on a new target.
                Target = mobs.FirstOrDefault();

                // Update last time target was updated. 
                lastTargetCheck = DateTime.Now;

                if (Target != null) LogViewModel.Write("Now targeting " + Target.Name + " : " + Target.Id);
            }

            return false;
        }

        private bool ShouldCheckTarget()
        {
            if (lastTargetCheck == null) return true;
            return DateTime.Now >= lastTargetCheck.Value.AddSeconds(Constants.UnitArrayCheckRate);
        }
    }
}