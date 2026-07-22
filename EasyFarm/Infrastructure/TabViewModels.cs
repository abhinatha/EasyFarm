using System;
using System.Collections.Generic;
using System.Linq;
using EasyFarm.ViewModels;
using Ninject;

namespace EasyFarm.Infrastructure
{
    public class TabViewModels
    {
        public IList<IViewModel> ViewModels = new List<IViewModel>();

        public TabViewModels(IKernel container)
        {
            foreach (KeyValuePair<Type, int> availableTab in AvailableTabs)
            {
                ViewModels.Add((IViewModel)container.Get(availableTab.Key));
            }

            ViewModels = ViewModels
                .Where(x => AvailableTabs.ContainsKey(x.GetType()))
                .OrderBy(x => AvailableTabs[x.GetType()])
                .ToList();
        }

        /// <summary>
        /// The available tabs in the main view with their given display order.
        /// </summary>
        public Dictionary<Type, int> AvailableTabs => new Dictionary<Type, int>
        {
            { typeof(BattlesViewModel), 1 },
            { typeof(TargetingViewModel), 2 },
            { typeof(RestingViewModel), 3 },
            { typeof(RoutesViewModel), 4 },
            { typeof(CampViewModel), 5 },
            { typeof(FollowViewModel), 6 },
            { typeof(LogViewModel), 7 },
            { typeof(WhitelistViewModel), 8 },
            { typeof(SettingsViewModel), 9 }
        };
    }
}