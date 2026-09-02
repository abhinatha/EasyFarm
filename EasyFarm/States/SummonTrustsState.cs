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
using System.Collections.Generic;
using System.Linq;
using EasyFarm.Classes;
using EasyFarm.UserSettings;
using MemoryAPI;

namespace EasyFarm.States
{
    public class SummonTrustsState : AgentState
    {
        // Cast-failure protection: if a trust spell is attempted repeatedly
        // without the trust ever joining the party (e.g. not party leader
        // while another player is in the party, so the server rejects the
        // cast), suspend that trust instead of blocking the bot forever.
        private const int MaxCastAttempts = 12;
        private const int SuspendMinutes = 5;
        private const int StaleEpisodeMinutes = 5;

        private class CastTracker
        {
            public int Attempts;
            public DateTime LastAttempt;
            public DateTime NextAllowed;
            public DateTime SuspendedUntil;
        }

        private static readonly Dictionary<string, CastTracker> Trackers =
            new Dictionary<string, CastTracker>();

        private static DateTime _lastRelease = DateTime.MinValue;

        private static bool IsSuspended(BattleAbility trust)
        {
            CastTracker t;
            return Trackers.TryGetValue(trust.Name, out t) &&
                   DateTime.Now < t.SuspendedUntil;
        }

        /// <summary>
        ///     True while this trust is waiting out its retry backoff after
        ///     a failed cast.
        /// </summary>
        private static bool IsBackingOff(BattleAbility trust)
        {
            CastTracker t;
            return Trackers.TryGetValue(trust.Name, out t) &&
                   DateTime.Now < t.NextAllowed;
        }

        private void RecordAttempt(BattleAbility trust)
        {
            CastTracker t;
            // Count CONSECUTIVE failed attempts; only start a fresh episode
            // after a long gap. (A fixed time window never triggered with
            // several trusts rotating - each trust's window expired before
            // it reached the attempt cap, so a 3-minute server-rejection
            // loop ran unchecked.)
            if (!Trackers.TryGetValue(trust.Name, out t) ||
                DateTime.Now > t.LastAttempt.AddMinutes(StaleEpisodeMinutes))
            {
                t = new CastTracker();
                Trackers[trust.Name] = t;
            }

            t.LastAttempt = DateTime.Now;
            t.Attempts++;

            // Exponential backoff between retries (5s, 10s, 20s, 40s, then
            // 60s), so a transient server-side blocker - e.g. a freshly
            // invited player still engaged - is retried through instead of
            // burning all attempts in seconds and suspending the healer.
            var delay = Math.Min(60, 5 * (1 << Math.Min(4, t.Attempts - 1)));
            t.NextAllowed = DateTime.Now.AddSeconds(delay);

            if (t.Attempts >= MaxCastAttempts)
            {
                t.SuspendedUntil = DateTime.Now.AddMinutes(SuspendMinutes);
                t.Attempts = 0;
                Diagnostics.CombatDiag.Event(string.Format(
                    "TRUST suspended {0} for {1}min: {2} casts without joining party " +
                    "(not party leader? no room? wrong area?) {3}",
                    trust.Name, SuspendMinutes, MaxCastAttempts, PartyStatusSummary()));
            }
            else if (t.Attempts >= 2)
            {
                Diagnostics.CombatDiag.Event(string.Format(
                    "TRUST cast failing for {0} (attempt {1}, next retry in {2}s) {3}",
                    trust.Name, t.Attempts, delay, PartyStatusSummary()));
            }
        }

        /// <summary>
        ///     Diagnostic: each party member with their entity-table combat
        ///     status, to identify what the server is objecting to when
        ///     trust casts fail.
        /// </summary>
        private string PartyStatusSummary()
        {
            try
            {
                var parts = EliteApi.PartyMember.Values
                    .Where(x => x.UnitPresent)
                    .Select(x =>
                    {
                        string st;
                        try { st = x.Status.ToString(); }
                        catch { st = "err"; }
                        return x.Name + ":" + st;
                    });
                return "party[" + string.Join(", ", parts) + "]";
            }
            catch
            {
                return "party[unavailable]";
            }
        }

        private static void ClearAttempts(BattleAbility trust)
        {
            Trackers.Remove(trust.Name);
        }

        /// <summary>Chat-commanded logic reset: forget suspensions, failure counts, and gate memory.</summary>
        public static void ResetTrackers()
        {
            Trackers.Clear();
            LastEngageCommand = DateTime.MinValue;
            _lastRelease = DateTime.MinValue;
            _lastGate = null;
            _rebuildingUntil = DateTime.MinValue;
            _releaseTargetName = null;
            _lastRetrAll = DateTime.MinValue;
        }

        public SummonTrustsState(StateMemory memory) : base(memory)
        {
        }

        private bool PartyHasSpace()
        {
            var slots = 0;
            for (var i = 1; i < 6; i++)
                if (!EliteApi.PartyMember[(byte) i].UnitPresent)
                    slots++;
            return slots > 0;
        }

        private IPartyMemberTools FindPartyMember(BattleAbility trust)
        {
            if (string.IsNullOrEmpty(trust.Name)) return null;

            for (var i = 1; i < 6; i++)
            {
                var p = EliteApi.PartyMember[(byte) i];
                var comp = trust.Name;
                if (comp.Contains("(UC)") || comp.Contains("II") || comp.Contains("AA"))
                {
                    comp = comp.Replace(" (UC)", "");
                    comp = comp.Replace(" II", "");
                    comp = comp.Replace("AA", "Ark");
                }

                comp = comp.Replace(" ", "");

                if (p.UnitPresent && p.Name == comp) return p;
            }

            return null;
        }

        private bool TrustInParty(BattleAbility trust)
        {
            var t = FindPartyMember(trust);
            return t != null;
        }

        private bool TrustNeedsDismissal(BattleAbility trust)
        {
            var t = FindPartyMember(trust);
            if (t == null) return false;

            // If the trust is set to be resummonable, respect the MP.
            if (trust.ResummonOnLowMP)
                if (t.MPPCurrent <= trust.ResummonMPHigh && t.MPPCurrent >= trust.ResummonMPLow)
                    return true;

            // If the trust is set to be resummonable, respect the HP 
            if (trust.ResummonOnLowHP)
                if (t.HPPCurrent <= trust.ResummonHPHigh && t.HPPCurrent >= trust.ResummonHPLow)
                    return true;

            return false;
        }

        /// <summary>
        ///     Maximum range at which the game will let us dismiss an alter
        ///     ego. Beyond this the command is silently ignored.
        /// </summary>
        private const double TrustReleaseRange = 30;

        /// <summary>
        ///     How long to keep trying to dismiss one alter ego before falling
        ///     back to the untargeted group dismiss.
        /// </summary>
        private const int ReleaseStuckSeconds = 30;

        private const int ReleaseAllCooldownSeconds = 30;

        /// <summary>
        ///     How long to keep the bot out of combat after an untargeted
        ///     "/retr all" so the dismissals can land and the trusts can be
        ///     resummoned.
        /// </summary>
        private const int RebuildWindowSeconds = 45;

        /// <summary>
        ///     Armed when "/retr all" goes out. The command is asynchronous:
        ///     the party list still reported all four trusts nearly three
        ///     seconds after it was sent. Without an explicit settle window
        ///     Check() sees a full party, returns false, and ApproachState
        ///     engages half a second later - then the dismissals land and FFXI
        ///     will not let an alter ego be summoned while engaged, so the
        ///     trusts never come back and the fight is fought solo. Observed:
        ///     /retr all 11:08:42, engage 11:08:45.6, trusts gone 11:08:53,
        ///     dead 11:12:31 on a worm that took 90s with a full party.
        /// </summary>
        private static DateTime _rebuildingUntil = DateTime.MinValue;

        public static bool IsRebuildingTrusts
        {
            get { return DateTime.Now < _rebuildingUntil; }
        }

        public static void FinishRebuild()
        {
            _rebuildingUntil = DateTime.MinValue;
        }

        private static string _releaseTargetName;
        private static DateTime _releaseStartedAt;
        private static DateTime _lastRetrAll = DateTime.MinValue;

        private bool ReleaseTrust(BattleAbility trust)
        {
            var comp = trust.Name;
            if (comp.Contains("(UC)") || comp.Contains("II") || comp.Contains("AA"))
            {
                comp = comp.Replace(" (UC)", "");
                comp = comp.Replace(" II", "");
                comp = comp.Replace("AA", "Ark");
            }

            comp = comp.Replace(" ", "");

            // "/refa" and its aliases (/retr, /returnfaith, /returntrust)
            // dismiss the TARGETED alter ego. The only subcommand they accept
            // is "all". A name argument - "/refa Koru-Moru" - is not valid
            // syntax, so the game discarded every send.
            //
            // Observed: 7,920 "TRUST releasing Koru-Moru" events across an
            // 11.5 hour session, the trust never leaving the party, and Run()
            // returning early on that same entry every pass - which wedges the
            // whole trust rotation and leaves the bot standing and doing
            // nothing. That is also why ChatCommands' "/retr all" works while
            // this never did: "all" IS a real subcommand.
            //
            // So: target the alter ego, confirm the cursor actually committed
            // (same race the engage path had to solve), then send the bare
            // command. Range matters too - the game only dismisses a trust
            // within targeting distance.
            // Returns false when the alter ego cannot be targeted at all -
            // absent from the entity array, dead, or stranded out of range.
            // That is the signature of a GHOST party entry: the party list
            // still shows the trust, but there is nothing there to target, so
            // no amount of targeted dismissal will ever clear it. The caller
            // escalates to "/retr all" on a false return.
            var unit = UnitService.GetUnitByName(comp);
            if (unit == null || !unit.IsActive || unit.IsDead) return false;
            if (unit.Distance > TrustReleaseRange) return false;

            Classes.Player.SetTarget(EliteApi, unit);
            if (!Classes.Player.IsTargeting(EliteApi, unit)) return false;

            EliteApi.Windower.SendString("/refa");
            return true;
        }

        private static string _lastGate;

        private static void Gate(string reason)
        {
            if (reason == _lastGate) return;
            _lastGate = reason;
            Diagnostics.CombatDiag.Event("TRUSTGATE " + reason);
        }

        // Set by ApproachState when /attack is sent. Combat status takes
        // ~0.5-1s to confirm; in that window the player still reads
        // Standing with no enmity, which once allowed a low-MP dismissal
        // to release the healer straight into a starting fight.
        public static DateTime LastEngageCommand = DateTime.MinValue;

        public override bool Check()
        {
            if (new RestState(Memory).Check()) { Gate("no: resting"); return false; }
            if (!EliteApi.Player.Status.Equals(Status.Standing)) { Gate("no: status " + EliteApi.Player.Status); return false; }
            if (DateTime.Now < LastEngageCommand.AddSeconds(3)) { Gate("no: engage in progress"); return false; }

            // Trust magic cannot be cast while we have enmity. Enmity cannot
            // be read from memory, so approximate it: any live mob fighting
            // that is (a) our claim, (b) party-claimed nearby (we may be on
            // its hate list from an earlier engage, and its fight blocks the
            // cast), or (c) unclaimed and close enough to be on us. In these
            // cases stop blocking engagement: go finish the fight, then
            // resummon. The earlier check missed case (b) - an eft claimed
            // by a party member kept rejecting the summon while the gate
            // reported no aggro and cast-looped.
            var aggroMob = UnitService.MobArray.FirstOrDefault(x =>
                !x.IsDead && x.Status.Equals(Status.Fighting) &&
                (x.MyClaim ||
                 x.PartyClaim && x.Distance < 30 ||
                 !x.IsClaimed && x.Distance < 12));
            if (aggroMob != null)
            {
                Gate(string.Format("no: enmity {0}[{1}] d:{2:F1} party:{3} mine:{4}",
                    aggroMob.Name, aggroMob.Id, aggroMob.Distance, aggroMob.PartyClaim, aggroMob.MyClaim));
                return false;
            }

            // Trust magic is also rejected while any (real) party member is
            // engaged in battle. Detected via the entity table; trusts are
            // excluded since they mirror the player's own combat state.
            var memberFighting = EliteApi.PartyMember.Values
                .Where(x => x.UnitPresent)
                .FirstOrDefault(x =>
                {
                    try { return x.NpcType != NpcType.NPC && x.Status == Status.Fighting; }
                    catch { return false; }
                });
            if (memberFighting != null)
            {
                Gate("no: party member fighting (" + memberFighting.Name + ")");
                return false;
            }

            // A chat-commanded party invite is in progress: leave the party
            // slot open for the invited player instead of refilling it with
            // trusts. The pull hold armed by the invite keeps the bot from
            // engaging meanwhile.
            if (ChatCommands.SuppressTrustSummons())
            {
                Gate("no: party invite in progress");
                return false;
            }

            // NOTE: deliberately NOT filtered by ActionFilters.BuffingFilter.
            // BuffingFilter returns false while the trust spell is on recast,
            // which previously made this Check return false and allowed
            // Approach/Pull to engage the next mob before trusts were
            // (re)summoned. Check() now answers "are trusts pending?";
            // Run() alone decides castability via IsRecastable.
            var trusts = Config.BattleLists["Trusts"].Actions
                .Where(t => t.IsEnabled)
                .Where(t => !string.IsNullOrEmpty(t.Name))
                .Where(t => !IsSuspended(t))
                .ToList();

            var maxTrustPartySize = Config.TrustPartySize;

            foreach (var trust in trusts)
                // A low-MP/HP dismissal only becomes actionable when the
                // trust spell is off recast: the trust stays in the party
                // (still functional at low MP) instead of being released
                // into a 4-minute healerless recast wait.
                if (TrustNeedsDismissal(trust) && AbilityUtils.IsRecastable(EliteApi, trust) ||
                    !TrustInParty(trust) && PartyHasSpace() && !MaxTrustsReached(maxTrustPartySize))
                {
                    Gate(string.Format("yes: pending {0} (dismissal:{1})", trust.Name, TrustNeedsDismissal(trust)));
                    return true;
                }

            Gate("no: all trusts up");
            return false;
        }

        private bool MaxTrustsReached(int maxTrustPartySize)
        {
            return EliteApi.PartyMember.Values
                       .Where(x => x.UnitPresent)
                       .Count(x =>
                       {
                           // PartyMemberTools.NpcType resolves the entity by
                           // server id and can throw NullReferenceException
                           // while the entity table is stale (zoning, trust
                           // despawn). Treat unresolved members as non-NPC.
                           try { return x.NpcType == NpcType.NPC; }
                           catch { return false; }
                       }) >= maxTrustPartySize;
        }

        public override void Run()
        {
            if (EliteApi.Player.Status.Equals(Status.Fighting)) return;
            if (DateTime.Now < LastEngageCommand.AddSeconds(3)) return;

            // Camp mode: summon/release at camp; walk home first. Check()
            // already blocks engaging while trusts are pending, so the walk
            // is uninterrupted unless something aggros (enmity escape).
            if (CampService.Active(Config) && !CampService.AtCamp(EliteApi, Config))
            {
                CampService.WalkToCamp(EliteApi, Config);
                return;
            }

            var trusts = Config.BattleLists["Trusts"].Actions.Where(t => t.IsEnabled);

            // Strict top-to-bottom priority: act on exactly ONE trust per
            // pass - the highest entry in the list that needs anything. If
            // its spell is on recast, WAIT; never let a lower-priority
            // trust take the slot (it might displace the main healer).
            foreach (var trust in trusts)
            {
                if (IsSuspended(trust)) continue;

                var inParty = TrustInParty(trust);

                // Present and healthy: nothing to do, check next in priority.
                if (inParty && !TrustNeedsDismissal(trust))
                {
                    ClearAttempts(trust);
                    if (_releaseTargetName == trust.Name) _releaseTargetName = null;
                    continue;
                }

                // Present but flagged for resummon: release only when the
                // replacement is immediately castable, then stop - the
                // summon happens next pass once the party slot updates.
                // The party list takes a few seconds to reflect the
                // release, so rate-limit the /refa send instead of
                // repeating it every pass.
                if (inParty)
                {
                    if (AbilityUtils.IsRecastable(EliteApi, trust) &&
                        DateTime.Now > _lastRelease.AddSeconds(5))
                    {
                        _lastRelease = DateTime.Now;

                        if (_releaseTargetName != trust.Name)
                        {
                            _releaseTargetName = trust.Name;
                            _releaseStartedAt = DateTime.Now;
                        }

                        Diagnostics.CombatDiag.Event("TRUST releasing " + trust.Name);
                        var targeted = ReleaseTrust(trust);
                        var stuck = DateTime.Now > _releaseStartedAt.AddSeconds(ReleaseStuckSeconds);

                        // Escalation. A ghost party entry - the alter ego is
                        // listed as a member but is not actually present, or is
                        // stranded somewhere unreachable - can never be cleared
                        // by a targeted dismiss. Without this the release
                        // silently no-ops forever, and because this branch
                        // returns, Run() lands on the SAME trust every pass:
                        // no other trust is processed, nothing is resummoned,
                        // and Check() keeps blocking combat, so the bot simply
                        // stands there. Observed: 7,920 release attempts across
                        // 11.5 hours, broken instantly by a manual "/retr all".
                        //
                        // "/retr all" takes no target, so it clears phantom
                        // entries the targeted form cannot touch. It drops the
                        // healthy trusts too, but they resummon on the next
                        // passes - which is strictly better than standing still
                        // indefinitely.
                        if ((!targeted || stuck) &&
                            DateTime.Now > _lastRetrAll.AddSeconds(ReleaseAllCooldownSeconds))
                        {
                            _lastRetrAll = DateTime.Now;
                            _releaseStartedAt = DateTime.Now;
                            Diagnostics.CombatDiag.Event(string.Format(
                                "TRUST release fallback: {0} {1} - sending /retr all",
                                trust.Name,
                                targeted
                                    ? "still in party after " + ReleaseStuckSeconds + "s"
                                    : "not targetable (ghost party entry)"));
                            EliteApi.Windower.SendString("/retr all");
                            _rebuildingUntil = DateTime.Now.AddSeconds(RebuildWindowSeconds);
                        }
                    }
                    return;
                }

                // Missing: this is the highest-priority absent trust. If no
                // slot exists nothing can be summoned at all; if its spell
                // is on recast, wait for it rather than summoning a
                // lower-priority trust into the slot.
                // The release worked (or it was never in the party): end any
                // release campaign so a later dismissal starts a fresh clock.
                if (_releaseTargetName == trust.Name) _releaseTargetName = null;

                if (!PartyHasSpace() || MaxTrustsReached(Config.TrustPartySize)) return;
                if (!AbilityUtils.IsRecastable(EliteApi, trust)) return;
                if (IsBackingOff(trust)) return;

                RecordAttempt(trust);
                if (IsSuspended(trust)) return;
                Diagnostics.CombatDiag.Event("TRUST casting " + trust.Name);
                Executor.UseActions(new[] {trust});
                return;
            }

            // Fell through the whole list without acting: every enabled trust
            // is present and healthy, so a rebuild after "/retr all" is done.
            // Release the combat hold early rather than idling out the full
            // window.
            FinishRebuild();
        }
    }
}