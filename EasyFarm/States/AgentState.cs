using EasyFarm.Classes;
using EasyFarm.UserSettings;
using MemoryAPI;

namespace EasyFarm.States
{
    public class AgentState : BaseState
    {
        protected AgentState(StateMemory stateMemory)
        {
            Memory = stateMemory;
        }

        public IConfig Config
        {
            get { return Memory.Config;}
            set { Memory.Config = value; }
        }

        /// <summary>
        ///     Retrieves aggroing creature.
        /// </summary>
        protected IUnitService UnitService
        {
            get { return Memory.UnitService; }
            set { Memory.UnitService = value; }
        }

        public IUnitFilters UnitFilters
        {
            get { return Memory.UnitFilters; }
            set { Memory.UnitFilters = value; }
        }

        public StateMemory Memory { get; }

        public IMemoryAPI EliteApi
        {
            get { return Memory.EliteApi; }
            set { Memory.EliteApi = value; }
        }

        public bool IsFighting
        {
            get { return Memory.IsFighting; }
            set { Memory.IsFighting = value; }
        }

        public IUnit Target
        {
            get { return Memory.Target; }
            set { Memory.Target = value; }
        }

        /// <summary>
        ///     True while the player is engaged and the current target is
        ///     still a live, active unit. Used to keep the fight "sticky":
        ///     transient MobFilter failures (distance / waypoint radius /
        ///     claim flicker) must not cause a target switch mid-fight,
        ///     which is what leads to engaging multiple enemies.
        /// </summary>
        protected bool IsEngagedWithLiveTarget =>
            Target != null &&
            Target.IsActive &&
            !Target.IsDead &&
            EliteApi.Player.Status.Equals(Status.Fighting);

        /// <summary>
        ///     True when the current target is a live mob that is actively
        ///     attacking us. Such targets are treated as valid even when
        ///     they fail MobFilter (detection distance, camp radius): a mob
        ///     already on our hate list is our problem no matter where it
        ///     stands, and filtering it out lets it kill the trusts while
        ///     the bot fights fresh mobs.
        ///     Deliberately NOT gated on TargetBlacklist. A blacklist entry
        ///     means "do not PULL this", never "do not DEFEND against this".
        ///     Observed death: a stalemate-blacklisted mob kept hitting the
        ///     player, the blacklist hid it from every target path, the bot
        ///     stood at Tgt:none for 74s and died at full trust HP because
        ///     disengaging also idles the trusts.
        /// </summary>
        protected bool TargetIsAttacker =>
            Target != null &&
            Target.IsActive &&
            !Target.IsDead &&
            Target.HasAggroed &&
            Target.Distance < 30;

        public Executor Executor
        {
            get { return Memory.Executor; }
            set { Memory.Executor = value; }
        }
    }
}