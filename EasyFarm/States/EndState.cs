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
using MemoryAPI;

namespace EasyFarm.States
{
    /// <summary>
    ///     Handles the end of battle situation.
    ///     Fires off the end list, sets FightStart to true so other
    ///     lists can fire and replaces targets that are dead, null,
    ///     empty or invalid.
    /// </summary>
    public class EndState : AgentState
    {
        public EndState(StateMemory memory) : base(memory)
        {
        }

        // Stalemate watchdog: if the target takes no damage for this long
        // while we are engaged, abort the fight and blacklist the mob.
        // (Observed cause: flying mobs gaining height - 2D distance looks
        // in-range but every melee round whiffs while the mob still hits.)
        private const int StalemateSeconds = 45;
        private const int BlacklistMinutes = 10;

        // Failed-engage watchdog: combat intent set and /attack issued, but
        // Player never reaches Fighting and the target takes no damage
        // (unclaimed wandering mob we chase but never lock onto).
        private const int FailedEngageSeconds = 20;
        private const int FailedEngageBlacklistMinutes = 3;

        private static int _watchTargetId;
        private static short _watchTargetHpp;
        private static DateTime _watchLastProgress;
        private static bool _watchWasEngaged;

        private static int _feTargetId;
        private static short _feTargetHpp;
        private static DateTime _feFailingSince;

        /// <summary>Chat-commanded logic reset: restart stalemate tracking.</summary>
        public static void ResetWatchdog()
        {
            _watchTargetId = 0;
            _watchTargetHpp = 0;
            _watchLastProgress = DateTime.MinValue;
            _watchWasEngaged = false;

            _feTargetId = 0;
            _feTargetHpp = 0;
            _feFailingSince = DateTime.MinValue;
        }

        private bool FightIsStalemated()
        {
            if (!IsEngagedWithLiveTarget)
            {
                // Not in a fight: the next engaged evaluation starts a fresh
                // baseline. Without this, a respawn that reuses the server
                // id of a previous kill inherits the old baseline and trips
                // an instant false stalemate (which blacklists the mob while
                // it already has aggro - forcing every following fight to be
                // fought with it beating on us).
                _watchWasEngaged = false;
                return false;
            }

            if (!_watchWasEngaged ||
                Target.Id != _watchTargetId ||
                Target.HppCurrent > _watchTargetHpp)
            {
                // New fight, new target, or HP above baseline (repop with a
                // recycled id, or regen): restart tracking.
                _watchWasEngaged = true;
                _watchTargetId = Target.Id;
                _watchTargetHpp = Target.HppCurrent;
                _watchLastProgress = DateTime.Now;
                return false;
            }

            // Any damage counts as progress (regen can raise it back later).
            if (Target.HppCurrent < _watchTargetHpp)
            {
                _watchTargetHpp = Target.HppCurrent;
                _watchLastProgress = DateTime.Now;
                return false;
            }

            if (DateTime.Now < _watchLastProgress.AddSeconds(StalemateSeconds)) return false;

            Diagnostics.CombatDiag.Event(string.Format(
                "STALEMATE {0}[{1}] hp:{2}% no damage for {3}s - disengaging, blacklisted {4}min",
                Target.Name, Target.Id, Target.HppCurrent, StalemateSeconds, BlacklistMinutes));
            TargetBlacklist.Add(Target.Id, BlacklistMinutes);
            // Drop the target reference too: the mob may still be attacking
            // us (HasAggroed), and a held reference let the attacker-target
            // path re-engage a mob we just declared unkillable, looping
            // stalemate -> blacklist -> re-engage until death.
            Target = null;
            return true;
        }

        private bool EngageIsFailing()
        {
            // Complement to the stalemate watchdog. Stalemate only runs once
            // Player.Status == Fighting; this covers the opposite hole - we
            // have combat intent (IsFighting) and a live target, but /attack
            // never takes: Player stays Standing, TP frozen, target 100%,
            // and we chase the mob forever. Arm condition (IsFighting +
            // Standing) is exactly the pathology, so a real fight - where
            // Player.Status == Fighting even during a trust resummon - never
            // trips this.
            if (!IsFighting ||
                Target == null || !Target.IsActive || Target.IsDead ||
                EliteApi.Player.Status.Equals(Status.Fighting))
            {
                _feFailingSince = DateTime.MinValue;
                return false;
            }

            if (Target.Id != _feTargetId ||
                _feFailingSince == DateTime.MinValue ||
                Target.HppCurrent < _feTargetHpp)
            {
                // New target, first failing pass, or damage landed (engage is
                // working after all): (re)start the timer.
                _feTargetId = Target.Id;
                _feTargetHpp = Target.HppCurrent;
                _feFailingSince = DateTime.Now;
                return false;
            }

            if (DateTime.Now < _feFailingSince.AddSeconds(FailedEngageSeconds)) return false;

            Diagnostics.CombatDiag.Event(string.Format(
                "FAILED-ENGAGE {0}[{1}] hp:{2}% intent but Standing for {3}s - dropping, blacklisted {4}min",
                Target.Name, Target.Id, Target.HppCurrent, FailedEngageSeconds, FailedEngageBlacklistMinutes));
            TargetBlacklist.Add(Target.Id, FailedEngageBlacklistMinutes);
            IsFighting = false;
            Target = null;
            _feFailingSince = DateTime.MinValue;
            return true;
        }

        public override bool Check()
        {
            // Prevent making the player stand up from resting.
            if (new RestState(Memory).Check()) return false;

            // Unwinnable fight: force the end of the battle.
            if (FightIsStalemated()) return true;

            // Combat intent that never lands a hit: chasing a mob we can't
            // engage. Drop it before it eats the whole session.
            if (EngageIsFailing()) return true;

            // Never end the fight while engaged with a live target: transient
            // MobFilter failures (distance / waypoint / claim flicker) would
            // otherwise disengage us and cause a retarget onto a second mob.
            if (IsEngagedWithLiveTarget) return false;

            // A live mob attacking us is a valid target even when it fails
            // MobFilter (out of detection range / camp radius).
            if (TargetIsAttacker) return false;

            // Creature is unkillable and does not meets the
            // user's criteria for valid mobs defined in MobFilters.
            return !UnitFilters.MobFilter(EliteApi, Target, Config);
        }

        /// <summary>
        ///     Force player when changing targets.
        /// </summary>
        public override void Enter()
        {
            EliteApi.Navigator.Reset();

            while (EliteApi.Player.Status == Status.Fighting) Player.Disengage(EliteApi);
        }

        public override void Run()
        {
            // Execute moves.
            var usable = Config.BattleLists["End"].Actions
                .Where(x => ActionFilters.BuffingFilter(EliteApi, x));

            Executor.UseBuffingActions(usable);

            // Reset all usage data to begin a new battle.
            foreach (var action in Config.BattleLists.Actions) action.Usages = 0;
        }
    }
}
