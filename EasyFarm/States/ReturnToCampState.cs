// ///////////////////////////////////////////////////////////////////
// Walks the player back to the recorded camp position after each
// detection / approach / engage / kill cycle when camp mode is on.
// Resting and trust summoning are handled by their own states, which
// also walk to camp first when camp mode is active.
// ///////////////////////////////////////////////////////////////////

using EasyFarm.Classes;
using MemoryAPI;

namespace EasyFarm.States
{
    public class ReturnToCampState : AgentState
    {
        public ReturnToCampState(StateMemory memory) : base(memory)
        {
        }

        public override bool Check()
        {
            if (!CampService.Active(Config)) return false;
            if (!CampService.InCampZone(EliteApi, Config)) return false;

            // Already home.
            if (CampService.AtCamp(EliteApi, Config)) return false;

            // Mid-fight or a valid target exists: the fight cycle owns movement.
            if (IsEngagedWithLiveTarget) return false;
            if (UnitFilters.MobFilter(EliteApi, Target, Config)) return false;
            if (!EliteApi.Player.Status.Equals(Status.Standing)) return false;

            // Rest / trust states walk to camp themselves and take priority.
            if (new RestState(Memory).Check()) return false;
            if (new SummonTrustsState(Memory).Check()) return false;

            return true;
        }

        public override void Run()
        {
            CampService.WalkToCamp(EliteApi, Config);
        }

        public override void Exit()
        {
            EliteApi.Navigator.Reset();
        }
    }
}
