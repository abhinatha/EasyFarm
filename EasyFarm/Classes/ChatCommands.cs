// ///////////////////////////////////////////////////////////////////
// Chat-command listener. Any chat line containing "hold pull" arms a
// pull hold: the current fight finishes normally, then new pulls are
// suppressed for one minute after combat ends (so real players can
// leave the party or regroup safely). "resume pull" cancels the hold
// immediately.
// ///////////////////////////////////////////////////////////////////

using System;
using System.Linq;
using EasyFarm.Infrastructure;
using EasyFarm.UserSettings;
using MemoryAPI;

namespace EasyFarm.Classes
{
    public static class ChatCommands
    {
        private static string HoldPhrase =>
            (Config.Instance.HoldPullPhrase ?? "").Trim();

        private static string ResumePhrase =>
            (Config.Instance.ResumePullPhrase ?? "").Trim();

        private static int HoldMinutes =>
            Config.Instance.HoldPullMinutes < 0 ? 0 : Config.Instance.HoldPullMinutes;

        private static string ResetPhrase =>
            (Config.Instance.ResetLogicPhrase ?? "").Trim();

        private static string FollowMePhrase =>
            (Config.Instance.FollowMePhrase ?? "").Trim();

        private static string InvitePhrase =>
            (Config.Instance.InvitePartyPhrase ?? "").Trim();

        private static string StopFollowPhrase =>
            (Config.Instance.StopFollowPhrase ?? "").Trim();

        private static int _processed;
        private static bool _holdRequested;
        private static DateTime? _holdTimerStart;
        private static bool _resetRequested;

        // Party-invite sequence: 0 = idle, 1 = armed (waiting for combat
        // to end), 2 = trusts released (waiting to send invite),
        // 3 = invite sent (waiting for the player to join).
        private static int _inviteStage;
        private static string _inviteName;
        private static DateTime _inviteStamp;
        private static DateTime _lastZoneReject = DateTime.MinValue;
        private static DateTime _inviteEnmityLogged = DateTime.MinValue;

        // Stage 1 will not release trusts while anything still holds enmity
        // on us. Capped so a mob we can never shake cannot strand the invite
        // forever.
        private const int InviteCombatWaitSeconds = 120;

        // Stage 2 waits for /retr all to actually free a party slot before
        // sending /pcmd add. Capped so a stuck slot cannot hang the sequence.
        private const int InvitePartyDrainSeconds = 15;
        private static DateTime _invitePartyLogged = DateTime.MinValue;

        /// <summary>
        ///     Whitelist gate. The bot's own character is always allowed.
        ///     An empty whitelist allows everyone; once it has entries,
        ///     only listed names (and self) may issue commands. Lines whose
        ///     speaker cannot be parsed are denied while a whitelist is
        ///     active.
        /// </summary>
        private static bool IsAuthorized(IMemoryAPI fface, string speaker)
        {
            string self = null;
            try { self = fface.Player.Name; } catch { }
            if (!string.IsNullOrEmpty(speaker) &&
                string.Equals(speaker, self, StringComparison.OrdinalIgnoreCase))
                return true;

            var list = Config.Instance.CommandWhitelist;
            if (list == null || list.Count == 0) return true;
            if (string.IsNullOrEmpty(speaker)) return false;

            foreach (var name in list)
                if (!string.IsNullOrWhiteSpace(name) &&
                    string.Equals(name.Trim(), speaker, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>Scan new chat lines for commands. Called once per FSM pass.</summary>
        public static void Poll(IMemoryAPI fface)
        {
            try
            {
                var entries = fface.Chat.ChatEntries.ToList();
                if (entries.Count < _processed) _processed = 0;

                for (var i = _processed; i < entries.Count; i++)
                {
                    var text = entries[i].Text ?? "";

                    // Our own outgoing tells render as ">>Name : message".
                    // Parsing them would treat the RECIPIENT as the speaker,
                    // and a reply that echoes a phrase (e.g. an invite
                    // phrase equal to the requester's name) would re-trigger
                    // itself in an infinite tell loop. Never read them as
                    // commands.
                    if (text.TrimStart().StartsWith(">>")) continue;

                    // Cheap pre-check: does the line contain any command at all?
                    var matchedPhrase =
                        ResetPhrase.Length > 0 && text.IndexOf(ResetPhrase, StringComparison.OrdinalIgnoreCase) >= 0 ? ResetPhrase :
                        ResumePhrase.Length > 0 && text.IndexOf(ResumePhrase, StringComparison.OrdinalIgnoreCase) >= 0 ? ResumePhrase :
                        HoldPhrase.Length > 0 && text.IndexOf(HoldPhrase, StringComparison.OrdinalIgnoreCase) >= 0 ? HoldPhrase :
                        StopFollowPhrase.Length > 0 && text.IndexOf(StopFollowPhrase, StringComparison.OrdinalIgnoreCase) >= 0 ? StopFollowPhrase :
                        InvitePhrase.Length > 0 && text.IndexOf(InvitePhrase, StringComparison.OrdinalIgnoreCase) >= 0 ? InvitePhrase :
                        FollowMePhrase.Length > 0 && text.IndexOf(FollowMePhrase, StringComparison.OrdinalIgnoreCase) >= 0 ? FollowMePhrase :
                        null;
                    if (matchedPhrase == null) continue;

                    var commandSpeaker = ExtractSpeaker(text);
                    if (!IsAuthorized(fface, commandSpeaker))
                    {
                        Diagnostics.CombatDiag.Event("CHAT ignored '" + matchedPhrase + "' from " +
                            (commandSpeaker ?? "unknown") + " - not on whitelist");
                        continue;
                    }

                    if (ResetPhrase.Length > 0 &&
                        text.IndexOf(ResetPhrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _resetRequested = true;
                        Diagnostics.CombatDiag.Event("CHAT '" + ResetPhrase + "' from " +
                            (commandSpeaker ?? "unknown") + " - logic reset requested");
                    }
                    else if (ResumePhrase.Length > 0 &&
                        text.IndexOf(ResumePhrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (_holdRequested)
                            Diagnostics.CombatDiag.Event("CHAT '" + ResumePhrase + "' - hold cancelled");
                        _holdRequested = false;
                        _holdTimerStart = null;
                    }
                    else if (HoldPhrase.Length > 0 &&
                             text.IndexOf(HoldPhrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!_holdRequested)
                            Diagnostics.CombatDiag.Event("CHAT '" + HoldPhrase + "' - hold armed");
                        _holdRequested = true;
                        _holdTimerStart = null;
                    }
                    else if (StopFollowPhrase.Length > 0 &&
                             text.IndexOf(StopFollowPhrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!string.IsNullOrEmpty(Config.Instance.FollowedPlayer))
                        {
                            Diagnostics.CombatDiag.Event("CHAT '" + StopFollowPhrase + "' - follow cleared");
                            Config.Instance.FollowedPlayer = string.Empty;
                            AppServices.SendFollowChanged();
                            AppServices.InformUser("Follow cleared by chat command.");
                        }
                    }
                    else if (InvitePhrase.Length > 0 &&
                             text.IndexOf(InvitePhrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var speaker = ExtractSpeaker(text);
                        string self = null;
                        try { self = fface.Player.Name; } catch { }

                        if (string.IsNullOrEmpty(speaker))
                        {
                            Diagnostics.CombatDiag.Event("CHAT invite request ignored - could not parse speaker: " + text);
                        }
                        else if (string.Equals(speaker, self, StringComparison.OrdinalIgnoreCase))
                        {
                            Diagnostics.CombatDiag.Event("CHAT invite request ignored - speaker is self");
                        }
                        else if (_inviteStage > 0)
                        {
                            // The same player asking again while we are already
                            // waiting on their accept almost always means the
                            // invite never reached them - a /pcmd add rejected
                            // server-side produces no popup and no error the
                            // bot can observe. Re-send instead of discarding,
                            // so a silently dropped invite is recoverable
                            // without a logic reset. Observed: invite sent into
                            // a full party, then two further requests ignored
                            // while the sequence waited out its 60s window.
                            if (_inviteStage == 3 &&
                                string.Equals(speaker, _inviteName, StringComparison.OrdinalIgnoreCase) &&
                                !PartyIsFull(fface))
                            {
                                Diagnostics.CombatDiag.Event("CHAT '" + InvitePhrase + "' from " + speaker +
                                    " again - re-sending /pcmd add " + _inviteName);
                                fface.Windower.SendString("/pcmd add " + _inviteName);
                                _inviteStamp = DateTime.Now;
                            }
                            else
                            {
                                Diagnostics.CombatDiag.Event("CHAT invite request from " + speaker +
                                    " ignored - invite sequence already in progress");
                            }
                        }
                        else if (!PlayerInZone(fface, speaker))
                        {
                            // Requester is out of zone: an invite cannot
                            // reach them and the whole sequence (release,
                            // resummon) would run for nothing. Tell them to
                            // retry once they arrive; nothing is armed.
                            Diagnostics.CombatDiag.Event("CHAT '" + InvitePhrase + "' from " + speaker +
                                " - rejected, not in zone");
                            // Rate-limit the reply: belt-and-braces against
                            // any echo re-triggering the phrase.
                            if (DateTime.Now > _lastZoneReject.AddSeconds(30))
                            {
                                _lastZoneReject = DateTime.Now;
                                fface.Windower.SendString("/tell " + speaker +
                                    " Resend the join request when in zone. Then I will start the process.");
                                AppServices.InformUser("Invite from {0} rejected - not in zone.", speaker);
                            }
                        }
                        else
                        {
                            Diagnostics.CombatDiag.Event("CHAT '" + InvitePhrase + "' from " + speaker +
                                " - holding pulls, will release trusts and invite");
                            _inviteName = speaker;
                            _inviteStage = 1;
                            _inviteStamp = DateTime.Now;
                            // Arm the pull hold so nothing new is engaged
                            // while the invite sequence runs.
                            _holdRequested = true;
                            _holdTimerStart = null;
                            AppServices.InformUser("Invite requested by {0}.", speaker);
                        }
                    }
                    else if (FollowMePhrase.Length > 0 &&
                             text.IndexOf(FollowMePhrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var speaker = ExtractSpeaker(text);
                        string self = null;
                        try { self = fface.Player.Name; } catch { }

                        if (string.IsNullOrEmpty(speaker))
                        {
                            Diagnostics.CombatDiag.Event("CHAT follow request ignored - could not parse speaker: " + text);
                        }
                        else if (string.Equals(speaker, self, StringComparison.OrdinalIgnoreCase))
                        {
                            Diagnostics.CombatDiag.Event("CHAT follow request ignored - speaker is self");
                        }
                        else
                        {
                            Diagnostics.CombatDiag.Event("CHAT '" + FollowMePhrase + "' from " + speaker + " - now following");
                            Config.Instance.FollowedPlayer = speaker;
                            AppServices.SendFollowChanged();
                            AppServices.InformUser("Now following {0} (chat command).", speaker);
                        }
                    }
                }

                _processed = entries.Count;
            }
            catch
            {
                // Chat access is best-effort; never break the FSM.
            }
        }

        /// <summary>
        ///     Pulls the speaker name out of a raw chat line. Handles the
        ///     common decorations: "(Name) msg" party, "&lt;Name&gt; msg"
        ///     linkshell, "Name&gt;&gt; msg" tell, "Name : msg" say.
        /// </summary>
        private static string ExtractSpeaker(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            text = text.Trim();

            string candidate = null;

            if (text.Length > 2 && (text[0] == '(' || text[0] == '<'))
            {
                var close = text.IndexOf(text[0] == '(' ? ')' : '>', 1);
                if (close > 1) candidate = text.Substring(1, close - 1);
            }

            if (candidate == null)
            {
                var tell = text.IndexOf(">>", StringComparison.Ordinal);
                if (tell > 0) candidate = text.Substring(0, tell);
            }

            if (candidate == null)
            {
                var say = text.IndexOf(" : ", StringComparison.Ordinal);
                if (say > 0) candidate = text.Substring(0, say);
            }

            if (candidate == null) return null;

            // FFXI character names are letters only; strip decorations.
            var name = new string(candidate.Trim().Where(char.IsLetter).ToArray());
            if (name.Length < 3 || name.Length > 16) return null;
            return name;
        }

        /// <summary>
        ///     Returns true exactly once per reset request. The FSM calls
        ///     this each pass and performs the reset when it fires.
        /// </summary>
        public static bool ConsumeResetRequest()
        {
            if (!_resetRequested) return false;
            _resetRequested = false;
            return true;
        }

        /// <summary>Clears any armed or active pull hold and any pending
        /// invite sequence (used by logic reset).</summary>
        public static void ClearHold()
        {
            _holdRequested = false;
            _holdTimerStart = null;
            _inviteStage = 0;
            _inviteName = null;
            _inviteEnmityLogged = DateTime.MinValue;
            _invitePartyLogged = DateTime.MinValue;
        }

        /// <summary>
        ///     True while a party-invite sequence is running; blocks trust
        ///     summoning so the invited player can take a party slot before
        ///     the trusts refill it.
        /// </summary>
        public static bool SuppressTrustSummons()
        {
            return _inviteStage > 0;
        }

        /// <summary>
        ///     True when a player character with this name is present in
        ///     the local entity table (i.e. in the same zone, within entity
        ///     range). Scans the PC index range of the unit array.
        /// </summary>
        private static bool PlayerInZone(IMemoryAPI fface, string name)
        {
            try
            {
                for (var id = 0; id < UserSettings.Constants.UnitArrayMax; id++)
                {
                    if (!fface.NPC.IsActive(id)) continue;
                    if (fface.NPC.NPCType(id) != NpcType.PC) continue;
                    if (string.Equals(fface.NPC.Name(id), name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        ///     True when every party slot is occupied. FFXI rejects
        ///     "/pcmd add" on a full party, and does so silently as far as the
        ///     bot can tell, so the invite sequence must confirm a slot is
        ///     genuinely free before issuing the command.
        /// </summary>
        private static bool PartyIsFull(IMemoryAPI fface)
        {
            try
            {
                return fface.PartyMember.Values.Count(x => x.UnitPresent) >= 6;
            }
            catch
            {
                return false;
            }
        }

        private static bool InvitedPlayerInParty(IMemoryAPI fface)
        {
            try
            {
                return fface.PartyMember.Values.Any(x =>
                    x.UnitPresent &&
                    string.Equals(x.Name, _inviteName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Approximates "something still has enmity on us", using the same
        ///     rule SummonTrustsState gates trust summons on: any live mob in
        ///     a fighting state that is our claim, party-claimed and nearby, or
        ///     unclaimed and close. Kept deliberately identical - if release
        ///     and resummon used different rules, the invite could dismiss the
        ///     trusts into a fight the trust gate then refuses to resummon them
        ///     out of, which is exactly the deadlock this fixes.
        /// </summary>
        private static IUnit EnmityBlocker()
        {
            try
            {
                if (UnitService.Units == null) return null;
                return UnitService.Units
                    .Where(x => x.NpcType.Equals(NpcType.Mob))
                    .FirstOrDefault(x =>
                        !x.IsDead && x.Status.Equals(Status.Fighting) &&
                        (x.MyClaim ||
                         x.PartyClaim && x.Distance < 30 ||
                         !x.IsClaimed && x.Distance < 12));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        ///     Drives the invite sequence. Called once per FSM pass:
        ///     combat ends -> /retr all -> short pause -> /pcmd add name ->
        ///     wait for the player to join (or time out) -> normal logic
        ///     (including trust resummons) resumes.
        /// </summary>
        public static void ProcessInvite(IMemoryAPI fface)
        {
            if (_inviteStage == 0) return;

            try
            {
                switch (_inviteStage)
                {
                    case 1: // Waiting for the current fight to finish.
                        if (!fface.Player.Status.Equals(Status.Standing)) return;

                        // Status.Standing alone is NOT "the fight is over" - it
                        // only means we are not currently engaged. Any
                        // disengage (EndState, or the engage-recycle watchdog)
                        // flips us to Standing while the mob is still alive, at
                        // melee range, and holding enmity on the party.
                        // Releasing trusts there strips every healer and DD
                        // mid-fight, and the bot then can neither win nor
                        // resummon - because SummonTrustsState gates on that
                        // same enmity. Observed: /retr all went out 1.5s after
                        // an ENGAGE-RECYCLE while an Apex Eft sat at d:1.8 on
                        // our own claim; the bot soloed it for 2m18s at
                        // trustsInParty:0, burning four more recycles before
                        // trusts finally came back. Use the identical enmity
                        // approximation so release and resummon agree.
                        var blocker = EnmityBlocker();
                        if (blocker != null)
                        {
                            if (DateTime.Now < _inviteStamp.AddSeconds(InviteCombatWaitSeconds))
                            {
                                if (DateTime.Now > _inviteEnmityLogged.AddSeconds(15))
                                {
                                    _inviteEnmityLogged = DateTime.Now;
                                    Diagnostics.CombatDiag.Event(string.Format(
                                        "INVITE waiting to release trusts - enmity {0}[{1}] d:{2:F1} party:{3} mine:{4}",
                                        blocker.Name, blocker.Id, blocker.Distance,
                                        blocker.PartyClaim, blocker.MyClaim));
                                }
                                return;
                            }

                            Diagnostics.CombatDiag.Event(string.Format(
                                "INVITE enmity {0}[{1}] still up after {2}s - releasing trusts anyway",
                                blocker.Name, blocker.Id, InviteCombatWaitSeconds));
                        }

                        Diagnostics.CombatDiag.Event("INVITE releasing all trusts (/retr all)");
                        fface.Windower.SendString("/retr all");
                        _inviteStage = 2;
                        _inviteStamp = DateTime.Now;
                        return;

                    case 2: // Wait for a slot to actually free, then invite.
                        // A fixed delay is not enough. /retr all is
                        // asynchronous - the trusts do not leave the party for
                        // ~3-4s - and FFXI rejects "/pcmd add" outright while
                        // all six slots are still occupied. The rejection is
                        // silent from the bot's side: no popup for the
                        // invitee, no error we can read, and the sequence then
                        // sits in stage 3 burning the whole 60s accept window
                        // on a command the server already threw away.
                        // Observed: party still 6/6 at 20:36:18.161, /pcmd add
                        // sent at 20:36:18.500, trusts finally left at
                        // 20:36:19.175 - 0.7s too late. The invitee saw
                        // nothing and re-requested twice.
                        if (DateTime.Now < _inviteStamp.AddSeconds(1)) return;

                        if (PartyIsFull(fface))
                        {
                            if (DateTime.Now < _inviteStamp.AddSeconds(InvitePartyDrainSeconds))
                            {
                                if (DateTime.Now > _invitePartyLogged.AddSeconds(5))
                                {
                                    _invitePartyLogged = DateTime.Now;
                                    Diagnostics.CombatDiag.Event(
                                        "INVITE waiting for a free party slot after /retr all");
                                }
                                return;
                            }

                            // Slots never freed. Fail loudly instead of firing
                            // a command the server will reject and then waiting
                            // 60s on an accept that can never come.
                            Diagnostics.CombatDiag.Event(
                                "INVITE party still full after " + InvitePartyDrainSeconds +
                                "s - aborting invite for " + _inviteName);
                            AppServices.InformUser("Invite to {0} aborted - party full.", _inviteName);
                            _inviteStage = 0;
                            _inviteName = null;
                            return;
                        }

                        Diagnostics.CombatDiag.Event("INVITE sending /pcmd add " + _inviteName);
                        fface.Windower.SendString("/pcmd add " + _inviteName);
                        _inviteStage = 3;
                        _inviteStamp = DateTime.Now;
                        return;

                    case 3: // Waiting for the player to accept.
                        if (InvitedPlayerInParty(fface))
                        {
                            Diagnostics.CombatDiag.Event("INVITE " + _inviteName + " joined the party - resuming");
                            AppServices.InformUser("{0} joined the party.", _inviteName);
                            _inviteStage = 0;
                            _inviteName = null;
                            return;
                        }
                        if (DateTime.Now > _inviteStamp.AddSeconds(60))
                        {
                            Diagnostics.CombatDiag.Event("INVITE " + _inviteName + " did not accept within 60s - resuming");
                            AppServices.InformUser("Invite to {0} timed out.", _inviteName);
                            _inviteStage = 0;
                            _inviteName = null;
                        }
                        return;
                }
            }
            catch
            {
                // Never let the invite sequence break the FSM.
            }
        }

        /// <summary>
        ///     True while new pulls must be suppressed. Once armed, the
        ///     hold applies immediately; the one-minute countdown starts
        ///     when the current fight ends (player leaves combat) and the
        ///     hold clears itself when the countdown expires.
        /// </summary>
        public static bool ShouldHoldPulls(IMemoryAPI fface)
        {
            // An invite in flight always holds pulls, independent of the hold
            // countdown. The two timers are unsynchronised: the hold countdown
            // starts when combat ends, while the invite's 60s accept window
            // starts ~3s later when /pcmd goes out - so the hold expires first
            // and the bot resumes pulling with the party slot still open and
            // trustsInParty:0. Observed: hold expired 16:03:58, bot engaged at
            // 16:04:00 with zero trusts, and the invite did not time out until
            // 16:04:08. Returning early here also defers the countdown until
            // the invite resolves, which leaves a clean window for the trust
            // resummon once the invited player is actually in the party.
            if (_inviteStage > 0) return true;

            if (!_holdRequested) return false;

            if (fface.Player.Status.Equals(Status.Fighting))
            {
                // Still fighting: countdown starts at the end of THIS fight.
                _holdTimerStart = null;
                return true;
            }

            if (_holdTimerStart == null)
            {
                _holdTimerStart = DateTime.Now;
                Diagnostics.CombatDiag.Event(
                    "CHAT hold pull: combat ended, holding pulls for " + HoldMinutes + " minute(s)");
            }

            if (DateTime.Now < _holdTimerStart.Value.AddMinutes(HoldMinutes)) return true;

            _holdRequested = false;
            _holdTimerStart = null;
            Diagnostics.CombatDiag.Event("CHAT hold pull expired - pulls resumed");
            return false;
        }
    }
}
