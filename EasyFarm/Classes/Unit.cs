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
using MemoryAPI;
using System;
using System.Linq;
using MemoryAPI.Navigation;

namespace EasyFarm.Classes
{
    public class Unit : IUnit
    {
        /// <summary>
        ///     Number of slots belonging to our own party. The PartyMember
        ///     dictionary is 16 entries wide because it also covers the two
        ///     other alliance parties (slots 6-15); claim ownership checks
        ///     must not reach into those.
        /// </summary>
        private const byte PartySlots = 6;

        /// <summary>
        ///     How close an unclaimed mob in combat must be before we treat it
        ///     as having aggroed us. FFXI aggro and link ranges sit well inside
        ///     this, and anything genuinely on us closes to melee rather than
        ///     staying in combat at range.
        /// </summary>
        private const double AggroDistance = 20;

        public Unit(IMemoryAPI fface, int id)
        {
            // Set this unit's session data. 
            _fface = fface;

            // Set the internal id. 
            Id = id;

            // Set the NPC information.
            _npc = _fface.NPC;
        }

        /// <summary>
        ///     Holds all the game's data.
        /// </summary>
        private readonly IMemoryAPI _fface;

        /// <summary>
        ///     Holds the data about units.
        /// </summary>
        private readonly INPCTools _npc;

        /// <summary>
        ///     The unit's id.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        ///     The unit's claim id; zero for unclaimed.
        /// </summary>
        public int ClaimedId
        {
            get { return _npc.ClaimedID(Id); }
        }

        /// <summary>
        ///     The unit's distace from the player.
        /// </summary>
        public double Distance
        {
            get { return _npc.Distance(Id); }
        }

        /// <summary>
        ///     The unit's position.
        /// </summary>
        public Position Position
        {
            get
            {
                var position = _npc.GetPosition(Id);

                return Helpers.ToPosition(
                    position.X, 
                    position.Y, position.Z, 
                    position.H);
            }
        }

        /// <summary>
        ///     The unit's health as a percent.
        /// </summary>
        public short HppCurrent
        {
            get { return _npc.HPPCurrent(Id); }
        }

        /// <summary>
        ///     Whether this unit is active.
        /// </summary>
        public bool IsActive
        {
            get { return _npc.IsActive(Id); }
        }

        /// <summary>
        ///     Whether this unit is claimed by some player.
        /// </summary>
        public bool IsClaimed
        {
            get { return _npc.IsClaimed(Id); }
        }

        /// <summary>
        ///     Whether this unit is visible to the player.
        /// </summary>
        public bool IsRendered
        {
            get { return _npc.IsRendered(Id); }
        }

        /// <summary>
        ///     The unit's name.
        /// </summary>
        public string Name
        {
            get { return _npc.Name(Id); }
        }

        /// <summary>
        ///     The unit's npc type
        /// </summary>
        public NpcType NpcType
        {
            get { return _npc.NPCType(Id); }
        }

        /// <summary>
        ///     The unit's x coordinate.
        /// </summary>
        public float PosX
        {
            get { return _npc.PosX(Id); }
        }

        /// <summary>
        ///     The unit's y coordinate.
        /// </summary>
        public float PosY
        {
            get { return _npc.PosY(Id); }
        }

        /// <summary>
        ///     The unit's z coordinate.
        /// </summary>
        public float PosZ
        {
            get { return _npc.PosZ(Id); }
        }

        /// <summary>
        ///     The unit's status.
        /// </summary>
        public Status Status
        {
            get { return _npc.Status(Id); }
        }

        public bool MyClaim
        {
            // Using EliteApi.PartyMember[0].ServerID until EliteApi.Player.PlayerServerID is fixed. 
            get { return ClaimedId == _fface.PartyMember[0].ServerID; }
        }

        /// <summary>
        ///     If the unit has aggroed our player.
        /// </summary>
        public int TargetingIndex => _npc.TargetingIndex(Id);

        public bool HasAggroed
        {
            get
            {
                if (Status != Status.Fighting) return false;

                // Our own claim is unambiguous.
                if (MyClaim) return true;

                // Anyone else's claim is their fight, not ours. Deliberately
                // includes party members' claims: treating those as aggro on
                // us would make TargetIsAttacker true for them, which in turn
                // exempts them from the claimless-fight watchdog - the exact
                // situation that watchdog exists to catch.
                if (IsClaimed) return false;

                // Unclaimed and in combat. This branch used to return true
                // unconditionally, with no reference to distance or to who the
                // mob was actually hitting. A stranger's mob whose claim
                // lapsed between server ticks - already worked down to 16% -
                // therefore read as an attacker from 29.7 yalms away, and the
                // aggro override in SetTargetState bypasses MobFilter by
                // design, so that one false positive was enough to send the
                // bot across the zone to steal it on the first pass after
                // startup.
                //
                // TargetingIndex would settle this exactly, but EliteAPI never
                // populates it - it read 0 in all 57,797 samples across every
                // diag log to date - so fall back to proximity, which is the
                // only signal actually available here.
                return Distance < AggroDistance;
            }
        }

        /// <summary>
        ///     If the unit is dead.
        /// </summary>
        public bool IsDead
        {
            get { return Status == Status.Dead1 || Status == Status.Dead2 || HppCurrent <= 0; }
        }

        /// <summary>
        ///     If a member of our own party has claim on the unit. Alliance
        ///     members are deliberately excluded.
        /// </summary>
        public bool PartyClaim
        {
            get
            {
                var claimed = ClaimedId;
                if (claimed == 0) return false;

                // Only the six real party slots, and only slots that are
                // actually occupied.
                //
                // Two defects lived here. The dictionary holds SIXTEEN
                // entries - 0-5 are our party, 6-15 are the other alliance
                // parties - so looping to PartyMember.Count treated every
                // alliance member's claim as a party claim. Worse, there was
                // no UnitPresent check: EliteAPI reads the raw party struct
                // and FFXI does not zero a slot when someone leaves, so
                // ServerID keeps returning the departed player's id
                // indefinitely. Any mob claimed by that person - long gone
                // from the party - still reported PartyClaim true.
                //
                // Consequences: UnitFilters lets those mobs through the
                // PartyFilter pull check, so the bot steals strangers' mobs;
                // and the SummonTrustsState enmity gate treats their fights
                // as ours and refuses to summon trusts. Every other
                // PartyMember consumer in the codebase already filters on
                // UnitPresent - this was the one that did not.
                for (byte i = 0; i < PartySlots; i++)
                {
                    IPartyMemberTools member;
                    if (!_fface.PartyMember.TryGetValue(i, out member)) continue;
                    if (member == null || !member.UnitPresent) continue;
                    if (member.ServerID == 0) continue;
                    if (member.ServerID == claimed) return true;
                }

                return false;
            }
        }

        /// <summary>
        ///     The vertical distance between this unit and our player.
        /// </summary>
        public double YDifference
        {
            get { return Math.Abs(PosY - _fface.Player.PosY); }
        }

        public bool IsPet
        {
            get
            {
                var playerIds = Enumerable.Range(0, 2048)
                    .Where(x => _npc.NPCType(x) == NpcType.PC)
                    .ToList();

                return playerIds.Any(x => _npc.PetID(x) == Id);
            }
        }
    }
}