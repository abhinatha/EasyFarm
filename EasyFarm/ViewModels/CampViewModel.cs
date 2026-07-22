// ///////////////////////////////////////////////////////////////////
// View model for the Camp tab: record a stationary camp position the
// bot returns to after each kill. Overrides route patrolling.
// ///////////////////////////////////////////////////////////////////

using System.Windows.Input;
using EasyFarm.Classes;
using EasyFarm.Infrastructure;
using EasyFarm.UserSettings;
using GalaSoft.MvvmLight.Command;
using MemoryAPI;
using MemoryAPI.Navigation;

namespace EasyFarm.ViewModels
{
    public class CampViewModel : ViewModelBase
    {
        public CampViewModel()
        {
            SetCampCommand = new RelayCommand(SetCamp);
            ClearCampCommand = new RelayCommand(ClearCamp);
            ViewName = "Camp";
        }

        public ICommand SetCampCommand { get; set; }

        public ICommand ClearCampCommand { get; set; }

        public bool IsCampEnabled
        {
            get { return Config.Instance.IsCampEnabled; }
            set
            {
                Config.Instance.IsCampEnabled = value;
                RaisePropertyChanged();
            }
        }

        public double CampRadius
        {
            get { return Config.Instance.CampRadius; }
            set
            {
                Config.Instance.CampRadius = value;
                RaisePropertyChanged();
            }
        }

        public string CampDescription
        {
            get
            {
                if (!Config.Instance.IsCampSet) return "No camp set.";
                var pos = Config.Instance.CampPosition;
                return string.Format("Camp: X {0:F1}  Z {1:F1}  ({2})",
                    pos.X, pos.Z, Config.Instance.CampZone);
            }
        }

        private void SetCamp()
        {
            if (FFACE == null)
            {
                AppServices.InformUser("Attach to the game before setting a camp.");
                return;
            }

            var pos = FFACE.Player.Position;
            Config.Instance.CampPosition = new Position
            {
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                H = pos.H
            };
            Config.Instance.CampZone = FFACE.Player.Zone;
            Config.Instance.IsCampSet = true;

            RaisePropertyChanged(nameof(CampDescription));
            AppServices.InformUser("Camp set at current position.");
        }

        private void ClearCamp()
        {
            Config.Instance.IsCampSet = false;
            Config.Instance.IsCampEnabled = false;

            RaisePropertyChanged(nameof(CampDescription));
            RaisePropertyChanged(nameof(IsCampEnabled));
            AppServices.InformUser("Camp cleared.");
        }
    }
}
