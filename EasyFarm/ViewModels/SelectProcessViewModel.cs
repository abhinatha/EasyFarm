// ///////////////////////////////////////////////////////////////////
// This file is a part of EasyFarm for Final Fantasy XI
// Copyright (C) 2013 Mykezero
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
using EasyFarm.Infrastructure;
using GalaSoft.MvvmLight.Command;
using MahApps.Metro.Controls.Dialogs;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using EasyFarm.Classes;

namespace EasyFarm.ViewModels
{
    public class SelectProcessViewModel : ViewModelBase
    {
        /// <summary>
        ///     The name of the Processes to search for.
        /// </summary>
        private const string ProcessName = "pol";

        public SelectProcessViewModel(BaseMetroDialog dialog)
        {
            // When window is closed through X button.
            Processes = new ObservableCollection<ProcessEntry>();

            // Close window on when "Set character" is pressed.
            SelectCommand = new RelayCommand(async () => await OnSelect());
            RefreshCommand = new RelayCommand(OnRefresh);
            CancelCommand = new RelayCommand(async () => await OnCancel());

            OnRefresh();
            Dialog = dialog;
        }

        /// <summary>
        ///     If the user has selected a Processes.
        /// </summary>
        public bool IsProcessSelected { get; set; }

        /// <summary>
        ///     Toggles whether the program show only pol.exe processes or
        ///     all processes (in case they are targeting a private server).
        /// </summary>
        public RelayCommand RefreshCommand { get; set; }

        /// <summary>
        ///     Makes the binded window exit.
        /// </summary>
        public RelayCommand SelectCommand { get; set; }

        /// <summary>
        /// Exists the selection screen without selecting a character.
        /// </summary>
        public RelayCommand CancelCommand { get; set; }

        /// <summary>
        ///     The currently selected game session.
        /// </summary>
        public ProcessEntry SelectedEntry { get; set; }

        /// <summary>
        ///     Kept as a Process so the handlers that attach to the game are
        ///     unchanged; the grid now selects a ProcessEntry instead.
        /// </summary>
        public Process SelectedProcess
        {
            get { return SelectedEntry == null ? null : SelectedEntry.Process; }
            set
            {
                SelectedEntry = value == null
                    ? null
                    : Processes.FirstOrDefault(x => x.Process != null && x.Process.Id == value.Id);
            }
        }

        public ObservableCollection<ProcessEntry> Processes { get; set; }

        public BaseMetroDialog Dialog { get; }

        /// <summary>
        /// Refresh the processes.
        /// </summary>
        private void OnRefresh()
        {
            Processes.Clear();

            // Primary query: EVERY pol.exe, with no window-title filter.
            //
            // The old code required a non-empty MainWindowTitle here, which
            // silently hid the actual game clients. .NET derives
            // MainWindowTitle from MainWindowHandle, and it guesses that
            // handle by taking the first visible, ownerless, top-level window
            // belonging to the process. A booted FFXI client - DirectX window,
            // managed by Ashita or Windower - does not satisfy that heuristic,
            // so the handle comes back 0 and the title comes back empty.
            // Observed: three pol.exe processes, the two real 600MB/455MB game
            // sessions both filtered out, and only the 6MB PlayOnline launcher
            // shell left in the list.
            var found = Snapshot(Process.GetProcessesByName(ProcessName), false);

            // Fallback for private servers running under another executable
            // name. Here a window title IS required, otherwise the list would
            // be every process on the machine.
            if (found.Count == 0) found = Snapshot(Process.GetProcesses(), true);

            // Largest first: the loaded game sessions dwarf the launcher shell,
            // so this puts the ones you actually want at the top.
            foreach (var entry in found.OrderByDescending(x => x.MemoryMb))
                Processes.Add(entry);
        }

        /// <summary>
        ///     Captures id, executable name and window title ONCE, at
        ///     enumeration time.
        ///
        ///     Process.MainWindowTitle is a live call every time it is read: it
        ///     goes through a MainWindowHandle that the Process object resolves
        ///     and caches on first access. POL destroys and recreates its window
        ///     as the game boots, so that cached handle goes dead and every
        ///     later read returns an empty string. The grid used to bind
        ///     straight to Process.MainWindowTitle, which is why the filter here
        ///     could see a real title while the cell rendered blank moments
        ///     later - a row with a process id and an empty Character Name.
        ///     Snapshotting means the grid shows exactly what was filtered on.
        /// </summary>
        private static List<ProcessEntry> Snapshot(IEnumerable<Process> processes, bool requireTitle)
        {
            var entries = new List<ProcessEntry>();

            foreach (var process in processes)
            {
                try
                {
                    // Drop any stale cached handle before reading the title.
                    process.Refresh();

                    var title = process.MainWindowTitle;
                    if (requireTitle && string.IsNullOrWhiteSpace(title)) continue;

                    entries.Add(new ProcessEntry
                    {
                        Process = process,
                        Id = process.Id,
                        ExecutableName = process.ProcessName,
                        Title = string.IsNullOrWhiteSpace(title) ? "(no window title)" : title,
                        MemoryMb = (int)(process.WorkingSet64 / (1024L * 1024L))
                    });
                }
                catch
                {
                    // Exited between enumeration and inspection, or not
                    // readable at our privilege level. Skip it rather than
                    // listing a row that cannot be attached to.
                }
            }

            return entries;
        }

        /// <summary>
        ///     Cleans up Processes watcher resources.
        /// </summary>
        private async Task OnSelect()
        {
            // User made a choice to close this dialog.
            IsProcessSelected = true;
            await CloseDialog();
        }

        private async Task OnCancel()
        {
            IsProcessSelected = false;
            SelectedProcess = null;
            await CloseDialog();
        }

        private async Task CloseDialog()
        {
            await DialogCoordinator.Instance.HideMetroDialogAsync(App.Current.MainWindow.DataContext, Dialog);
        }
    }

    /// <summary>
    ///     A stable, already-read view of a candidate game process. Exists so
    ///     the grid never re-reads volatile Process members while it renders.
    /// </summary>
    public class ProcessEntry
    {
        public Process Process { get; set; }
        public int Id { get; set; }
        public string ExecutableName { get; set; }
        public string Title { get; set; }

        /// <summary>
        ///     Working set in MB. Shown because it is the only reliable way to
        ///     tell a loaded game session (hundreds of MB) from the PlayOnline
        ///     launcher shell (single digits) when neither reports a title.
        /// </summary>
        public int MemoryMb { get; set; }
    }
}