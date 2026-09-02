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

using System.Linq;
using EasyFarm.Classes;
using EasyFarm.UserSettings;
using MemoryAPI;

namespace EasyFarm.States
{
    /// <summary>
    ///     Moves to target enemies.
    /// </summary>
    public class ApproachState : AgentState
    {
        public ApproachState(StateMemory memory) : base(memory)
        {
        }

        public override bool Check()
        {
            if (new RestState(Memory).Check()) return false;

            // Make sure we don't need trusts
            if (new SummonTrustsState(Memory).Check()) return false;

            // Target dead or null. Engaged live targets and mobs attacking
            // us always count as valid so we keep closing distance through
            // filter flicker / detection limits.
            if (!IsEngagedWithLiveTarget && !TargetIsAttacker &&
                !UnitFilters.MobFilter(EliteApi, Target, Config)) return false;

            // We should approach mobs that have aggroed or have been pulled. 
            if (Target.Status.Equals(Status.Fighting)) return true;

            // Get usable abilities. 
            var usable = Config.BattleLists["Pull"].Actions
                .Where(x => ActionFilters.BuffingFilter(EliteApi, x));

            // Approach when there are no pulling moves available. 
            if (!usable.Any()) return true;

            // Approach mobs if their distance is close. 
            return Target.Distance < 8;
        }

        public override void Run()
        {
            // Chat-commanded pull hold: do not start anything new while a
            // hold is armed/active. Current fights are unaffected.
            if (!EliteApi.Player.Status.Equals(Status.Fighting) &&
                ChatCommands.ShouldHoldPulls(EliteApi)) return;

            // Re-check the trust gate at action time. Within one FSM pass the
            // player can auto-disengage (previous mob died) AFTER Check()
            // evaluated the gate while status was still Fighting, so a due
            // low-MP resummon was invisible to Check() but the engage
            // condition below (!Fighting) had already become true.
            if (new SummonTrustsState(Memory).Check())
            {
                Diagnostics.CombatDiag.Event("ENGAGE blocked: trusts pending");
                return;
            }

            // Has the user decided that we should approach targets?
            if (Config.IsApproachEnabled)
            {
                // Move to target if out of melee range. 
                EliteApi.Navigator.DistanceTolerance = Config.MeleeDistance;
                EliteApi.Navigator.GotoNPC(Target.Id, Config.IsObjectAvoidanceEnabled);

                // Engaged but still at or beyond melee tolerance well after
                // the engage: the navigator considers us "arrived" while the
                // game says our swings cannot reach (observed: engaged at
                // 4.1y on a large-model worm, zero hits landed for six
                // minutes while it cast us to death). Push in tighter.
                if (EliteApi.Player.Status.Equals(Status.Fighting) &&
                    Target != null && !Target.IsDead &&
                    Target.Distance >= Config.MeleeDistance &&
                    System.DateTime.Now > SummonTrustsState.LastEngageCommand.AddSeconds(10))
                {
                    EliteApi.Navigator.DistanceTolerance = 1.5;
                    EliteApi.Navigator.GotoNPC(Target.Id, Config.IsObjectAvoidanceEnabled);
                }
            }

            // Face mob. 
            EliteApi.Navigator.FaceHeading(Target.Position);

            // Target mob if not currently targeted. 
            Player.SetTarget(EliteApi, Target);

            // Has the user decided we should engage in battle. 
            if (Config.IsEngageEnabled)
                if (!EliteApi.Player.Status.Equals(Status.Fighting) && Target.Distance < 25)
                {
                    // Engage confirmation takes ~0.5-4s server-side; don't
                    // spam /attack every 350ms pass in the meantime.
                    if (System.DateTime.Now < SummonTrustsState.LastEngageCommand.AddSeconds(2)) return;

                    // Constants.AttackTarget is "/attack <t>" - it binds to
                    // the CLIENT's target cursor, not to our Target object.
                    // If the cursor has not committed yet (it lags the memory
                    // write by a frame or two, and tab-targeting gives up
                    // after 1s without confirming) we engage whatever it was
                    // pointing at before - typically the mob that just died.
                    // Verify, then engage; a deferral just retries next pass.
                    // Do not start a NEW fight while the trusts are being
                    // rebuilt after an untargeted "/retr all". The dismissals
                    // land seconds after the command, and FFXI refuses to
                    // summon an alter ego once we are engaged - so engaging
                    // inside that window strands us solo for the rest of the
                    // fight. A mob already on us is exempt: that fight is
                    // happening whether we engage or not.
                    if (SummonTrustsState.IsRebuildingTrusts && !TargetIsAttacker)
                    {
                        Diagnostics.CombatDiag.Event(string.Format(
                            "ENGAGE deferred: rebuilding trusts, holding off {0}[{1}]",
                            Target.Name, Target.Id));
                        return;
                    }

                    if (!Player.IsTargeting(EliteApi, Target))
                    {
                        Diagnostics.CombatDiag.Event(string.Format(
                            "ENGAGE deferred: cursor on [{0}] but want {1}[{2}] d:{3:F1}",
                            EliteApi.Target.ID, Target.Name, Target.Id, Target.Distance));
                        return;
                    }

                    int trusts;
                    try
                    {
                        trusts = EliteApi.PartyMember.Values
                            .Where(x => x.UnitPresent)
                            .Count(x => { try { return x.NpcType == NpcType.NPC; } catch { return false; } });
                    }
                    catch { trusts = -1; }
                    Diagnostics.CombatDiag.Event(string.Format(
                        "ENGAGE {0}[{1}] cursor:[{2}] d:{3:F1} hp:{4}% trustsInParty:{5}",
                        Target.Name, Target.Id, EliteApi.Target.ID, Target.Distance, Target.HppCurrent, trusts));
                    SummonTrustsState.LastEngageCommand = System.DateTime.Now;
                    EliteApi.Windower.SendString(Constants.AttackTarget);
                }
        }
    }
}