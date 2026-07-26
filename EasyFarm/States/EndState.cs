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
        private const int FailedEngageSkipMinutes = 3;

        // Engage recycle: Player.Status stays Fighting while the client's
        // engage target is a DIFFERENT entity than the mob actually on us, so
        // no swing ever lands and no watchdog fires. Bounce the stance after
        // this long without damaging the mob attacking us.
        private const int EngageRecycleSeconds = 30;

        private static int _watchTargetId;
        private static short _watchTargetHpp;
        private static DateTime _watchLastProgress;
        private static bool _watchWasEngaged;
        private static int _stalemateHeldId;
        private static short _heldHpp;
        private static DateTime _heldLastProgress;
        private static DateTime _strandedDisengageAt;
        private static bool _strandedLogged;

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
            _stalemateHeldId = 0;
            _heldHpp = 0;
            _heldLastProgress = DateTime.MinValue;
            _strandedDisengageAt = DateTime.MinValue;
            _strandedLogged = false;

            _feTargetId = 0;
            _feTargetHpp = 0;
            _feFailingSince = DateTime.MinValue;

            FailedEngageSkip.Clear();
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

            // Attacker exemption: never disengage from a mob that is hitting
            // us, and never blacklist it. It is the target in front of us, not
            // a pull candidate. Blacklisting the mob we are actively fighting
            // makes BattleState/WeaponskillState filter it out (their action
            // selection runs through MobFilter, which honours the blacklist),
            // so the bot drops WS/JA/spells and grinds it with bare auto-
            // attack only - which whiffs on high-evasion Escha birds. Observed
            // death: held mob frozen at ~71% for 4 min, rotation silent, player
            // beaten from full to 0 while five trusts sat at 100%. Blacklist is
            // for PULLS, never for the current engaged mob. Escaping a fight we
            // are LOSING is the survival state's job (low player HP), not this
            // watchdog's - walking away mid-fight just idles the trusts.
            if (TargetIsAttacker)
            {
                if (_stalemateHeldId != Target.Id)
                {
                    _stalemateHeldId = Target.Id;
                    _heldHpp = Target.HppCurrent;
                    _heldLastProgress = DateTime.Now;
                    Diagnostics.CombatDiag.Event(string.Format(
                        "STALEMATE-HELD {0}[{1}] hp:{2}% attacking us - staying engaged, full rotation kept",
                        Target.Name, Target.Id, Target.HppCurrent));
                }
                else if (Target.HppCurrent < _heldHpp)
                {
                    // Damage is landing - the engage is genuinely on this mob.
                    _heldHpp = Target.HppCurrent;
                    _heldLastProgress = DateTime.Now;
                }
                else if (DateTime.Now > _heldLastProgress.AddSeconds(EngageRecycleSeconds))
                {
                    // Mis-engage recovery. Player.Status is Fighting and this
                    // mob is beating on us, but it has taken no damage for
                    // EngageRecycleSeconds. That means the client's engage
                    // target is a DIFFERENT entity - the cursor raced at
                    // engage time and we locked onto the mob we originally
                    // issued /attack for, now far out of melee range. Every
                    // auto-attack swings at nothing while Status stays
                    // Fighting, so no watchdog sees it (observed: engaged on
                    // [294] at d:16.1 while [296] at d:2.0 killed us; TP only
                    // ever moved in +34 increments from hits TAKEN, never from
                    // swings). Drop the stance; ApproachState re-engages next
                    // pass with a cursor-verified /attack. Weaponskills still
                    // landed throughout because Executor sets the cursor per
                    // action - only auto-attack was bound to the stale target.
                    _heldLastProgress = DateTime.Now;
                    Diagnostics.CombatDiag.Event(string.Format(
                        "ENGAGE-RECYCLE {0}[{1}] hp:{2}% d:{3:F1} no damage in {4}s while it attacks us - dropping stance to re-engage",
                        Target.Name, Target.Id, Target.HppCurrent, Target.Distance, EngageRecycleSeconds));
                    EliteApi.Windower.SendString(Constants.AttackOff);
                }

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
            // Attacker check: a mob hitting us is never dropped here -
            // blacklisting it just makes us ignore it while it keeps beating
            // on us and we pull a fresh mob. Only mobs we chase but that
            // ignore us (AggroUs:none) get dropped.
            if (!IsFighting || TargetIsAttacker ||
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
                "FAILED-ENGAGE {0}[{1}] hp:{2}% intent but Standing for {3}s - dropping, skipped {4}min",
                Target.Name, Target.Id, Target.HppCurrent, FailedEngageSeconds, FailedEngageSkipMinutes));
            // Soft skip, NOT TargetBlacklist. Normal selection avoids this id
            // so we rotate onto a fresh mob; the aggro override ignores it so
            // if this mob later actually attacks us we still turn and fight.
            // Keeping it off the hard blacklist is what stops this recovery
            // from also un-hiding stalemate-blacklisted mobs from the override.
            FailedEngageSkip.Add(Target.Id, FailedEngageSkipMinutes);
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
            // Stranded-engage recovery. Enter() only disengages if the client
            // was ALREADY Fighting the moment we transitioned in. An /attack
            // sent a fraction of a second earlier is still in flight and lands
            // several seconds later - after Enter() has run - leaving the
            // player engaged with Target == null. Enter() never fires again
            // while this state stays active, so nothing breaks the stance.
            // Observed: ENGAGE [295] at 15:17:07.9, EndState entered 15:17:08.3
            // while still Standing (disengage loop was a no-op), engage landed
            // 15:17:13 -> Fighting with Tgt:none, stuck 12 minutes with zero
            // state transitions. Re-check every pass instead.
            if (EliteApi.Player.Status.Equals(Status.Fighting) &&
                !IsEngagedWithLiveTarget && !TargetIsAttacker)
            {
                if (DateTime.Now > _strandedDisengageAt.AddSeconds(2))
                {
                    _strandedDisengageAt = DateTime.Now;
                    if (!_strandedLogged)
                    {
                        _strandedLogged = true;
                        Diagnostics.CombatDiag.Event(
                            "STRANDED-ENGAGE: Fighting with no valid target - disengaging");
                    }
                    Player.Disengage(EliteApi);
                }
            }
            else
            {
                _strandedLogged = false;
            }

            // Execute moves.
            var usable = Config.BattleLists["End"].Actions
                .Where(x => ActionFilters.BuffingFilter(EliteApi, x));

            Executor.UseBuffingActions(usable);

            // Reset all usage data to begin a new battle.
            foreach (var action in Config.BattleLists.Actions) action.Usages = 0;
        }
    }

    /// <summary>
    ///     Transient skip-list for mobs we issued /attack at but never
    ///     engaged (Player stayed Standing, target took no damage).
    ///     Deliberately separate from the hard <see cref="Classes.TargetBlacklist"/>:
    ///     - normal target selection skips these ids, so we rotate onto a
    ///       fresh mob instead of re-locking the same un-engageable one;
    ///     - the aggro override in SetTargetState ignores this list, so a
    ///       skipped mob that later actually attacks us (add / link) is still
    ///       picked up and fought.
    ///     Keeping these off TargetBlacklist is what prevents the failed-
    ///     engage recovery from also un-hiding stalemate-blacklisted mobs
    ///     (unkillable flyers) from the override.
    /// </summary>
    public static class FailedEngageSkip
    {
        private static readonly object Gate = new object();

        private static readonly System.Collections.Generic.Dictionary<int, DateTime> Until
            = new System.Collections.Generic.Dictionary<int, DateTime>();

        public static void Add(int id, int minutes)
        {
            lock (Gate) Until[id] = DateTime.Now.AddMinutes(minutes);
        }

        public static bool IsSkipped(int id)
        {
            lock (Gate)
            {
                DateTime until;
                if (!Until.TryGetValue(id, out until)) return false;
                if (DateTime.Now >= until)
                {
                    Until.Remove(id);
                    return false;
                }
                return true;
            }
        }

        public static void Clear()
        {
            lock (Gate) Until.Clear();
        }
    }
}
