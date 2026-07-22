// ///////////////////////////////////////////////////////////////////
// Combat diagnostics logger. Writes a per-session file to
// logs\diag-<timestamp>.txt containing a 1-second snapshot of the
// player / target / trust party / nearby aggro state, state machine
// transitions, engage / trust events, and a full mob + status-effect
// dump on death. Purpose: post-mortem analysis of AFK deaths.
// ///////////////////////////////////////////////////////////////////

using System;
using System.IO;
using System.Linq;
using System.Text;
using EasyFarm.States;
using MemoryAPI;

namespace EasyFarm.Diagnostics
{
    public static class CombatDiag
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static IMemoryAPI _fface;
        private static StateMemory _memory;

        private static DateTime _lastSnapshot = DateTime.MinValue;
        private static int _lastHpp = -1;
        private static bool _wasDead;

        public static void Init(IMemoryAPI fface, StateMemory memory)
        {
            lock (Sync)
            {
                _fface = fface;
                _memory = memory;

                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                try { Directory.CreateDirectory(dir); } catch { }
                _path = Path.Combine(dir, "diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");

                Event("SESSION START");
            }
        }

        /// <summary>One-off event line (state changes, engages, trust actions, gate reasons).</summary>
        public static void Event(string message)
        {
            WriteLine("EVT  " + message);
        }

        /// <summary>
        ///     Called once per FSM pass. Emits a snapshot at most once per
        ///     second, an immediate line on HP drops >= 10%, and a full
        ///     dump on the alive->dead transition.
        /// </summary>
        public static void Tick()
        {
            if (_fface == null) return;

            try
            {
                var player = _fface.Player;
                var hpp = player.HPPCurrent;
                var isDead = player.Status == Status.Dead1 || player.Status == Status.Dead2 || hpp <= 0;

                // Death edge: dump everything once.
                if (isDead && !_wasDead)
                {
                    _wasDead = true;
                    DeathDump();
                    return;
                }
                if (!isDead) _wasDead = false;

                // Sudden HP drop: log immediately, don't wait for throttle.
                var bigDrop = _lastHpp >= 0 && _lastHpp - hpp >= 10;

                if (!bigDrop && DateTime.Now < _lastSnapshot.AddSeconds(1)) return;
                _lastSnapshot = DateTime.Now;
                _lastHpp = hpp;

                WriteLine((bigDrop ? "DROP " : "SNAP ") + Snapshot());
            }
            catch (Exception ex)
            {
                WriteLine("DIAGERR Tick: " + ex.Message);
            }
        }

        private static string Snapshot()
        {
            var sb = new StringBuilder();
            var player = _fface.Player;

            sb.AppendFormat("HP:{0}% MP:{1}% TP:{2} St:{3} Pos:({4:F1},{5:F1})",
                player.HPPCurrent, player.MPPCurrent, player.TPCurrent,
                player.Status, player.PosX, player.PosZ);

            // Current target.
            var target = _memory != null ? _memory.Target : null;
            if (target != null && target.Id != 0)
                sb.AppendFormat(" | Tgt:{0}[{1}] hp:{2}% d:{3:F1} ydiff:{4:F1} st:{5} claim:{6} mine:{7}",
                    target.Name, target.Id, target.HppCurrent, target.Distance,
                    target.YDifference, target.Status, target.ClaimedId, target.MyClaim);
            else
                sb.Append(" | Tgt:none");

            if (_memory != null) sb.Append(" | Fighting:" + _memory.IsFighting);

            // Trust party composition.
            sb.Append(" | Party:[");
            var first = true;
            foreach (var pm in _fface.PartyMember.Values.Where(x => x.UnitPresent))
            {
                if (!first) sb.Append(", ");
                first = false;
                string npc;
                try { npc = pm.NpcType == NpcType.NPC ? "T" : "P"; }
                catch { npc = "?"; }
                sb.AppendFormat("{0}({1}):{2}%", pm.Name, npc, pm.HPPCurrent);
            }
            sb.Append("]");

            // Mobs currently fighting us / close unclaimed fighters.
            if (_memory != null && _memory.UnitService != null)
            {
                var aggro = _memory.UnitService.MobArray
                    .Where(x => x.IsActive && !x.IsDead && x.HasAggroed && (x.MyClaim || x.Distance < 20))
                    .Take(6)
                    .Select(x => string.Format("{0}[{1}] d:{2:F1} hp:{3}% tgt:{4}",
                        x.Name, x.Id, x.Distance, x.HppCurrent,
                        Classes.PartyIndex.DescribeTargeting(_fface, x)))
                    .ToList();
                sb.Append(" | AggroUs:" + (aggro.Any() ? string.Join("; ", aggro) : "none"));
            }

            return sb.ToString();
        }

        private static void DeathDump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================== DEATH ====================");
            try { sb.AppendLine("Last: " + Snapshot()); } catch { }

            try
            {
                var effects = _fface.Player.StatusEffects;
                sb.AppendLine("StatusEffects: " + (effects != null && effects.Any()
                    ? string.Join(", ", effects.Select(x => x.ToString()))
                    : "none"));
            }
            catch (Exception ex) { sb.AppendLine("StatusEffects: err " + ex.Message); }

            try
            {
                sb.AppendLine("Mobs within 30:");
                foreach (var mob in _memory.UnitService.MobArray
                    .Where(x => x.IsActive && x.Distance < 30)
                    .OrderBy(x => x.Distance))
                {
                    sb.AppendFormat("  {0}[{1}] d:{2:F1} hp:{3}% st:{4} claim:{5} mine:{6} aggro:{7} dead:{8}",
                        mob.Name, mob.Id, mob.Distance, mob.HppCurrent, mob.Status,
                        mob.ClaimedId, mob.MyClaim, mob.HasAggroed, mob.IsDead);
                    sb.AppendLine();
                }
            }
            catch (Exception ex) { sb.AppendLine("MobDump: err " + ex.Message); }

            sb.AppendLine("===============================================");
            WriteLine(sb.ToString());
        }

        private static void WriteLine(string line)
        {
            try
            {
                lock (Sync)
                {
                    if (_path == null) return;
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
                }
            }
            catch
            {
                // Never let diagnostics take down the FSM.
            }
        }
    }
}
