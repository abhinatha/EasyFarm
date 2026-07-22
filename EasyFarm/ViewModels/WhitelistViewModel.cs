// ///////////////////////////////////////////////////////////////////
// View model for the Whitelist tab: manages the list of character
// names allowed to issue chat commands. An empty list allows
// everyone; the bot's own character is always allowed.
// ///////////////////////////////////////////////////////////////////

using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using EasyFarm.Classes;
using EasyFarm.Infrastructure;
using EasyFarm.UserSettings;
using GalaSoft.MvvmLight.Command;

namespace EasyFarm.ViewModels
{
    public class WhitelistViewModel : ViewModelBase
    {
        private string _entryText = string.Empty;
        private string _selectedName;

        public WhitelistViewModel()
        {
            AddCommand = new RelayCommand(AddName);
            RemoveCommand = new RelayCommand(RemoveName);
            ViewName = "Whitelist";
        }

        public ICommand AddCommand { get; set; }

        public ICommand RemoveCommand { get; set; }

        public ObservableCollection<string> Names => Config.Instance.CommandWhitelist;

        public string EntryText
        {
            get { return _entryText; }
            set { Set(ref _entryText, value); }
        }

        public string SelectedName
        {
            get { return _selectedName; }
            set { Set(ref _selectedName, value); }
        }

        private void AddName()
        {
            var name = (EntryText ?? "").Trim();
            if (name.Length == 0) return;

            if (name.Any(c => !char.IsLetter(c)))
            {
                AppServices.InformUser("Character names contain letters only.");
                return;
            }

            if (Names.Any(x => string.Equals(x, name, System.StringComparison.OrdinalIgnoreCase)))
            {
                AppServices.InformUser("{0} is already on the whitelist.", name);
                return;
            }

            Names.Add(name);
            EntryText = string.Empty;
            AppServices.InformUser("{0} added to the command whitelist.", name);
        }

        private void RemoveName()
        {
            if (string.IsNullOrEmpty(SelectedName)) return;

            var name = SelectedName;
            Names.Remove(name);
            SelectedName = null;
            AppServices.InformUser("{0} removed from the command whitelist.", name);
        }
    }
}
