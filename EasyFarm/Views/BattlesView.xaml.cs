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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EasyFarm.Classes;
using EasyFarm.ViewModels;

namespace EasyFarm.Views
{
    /// <summary>
    ///     Interaction logic for BattlesView.xaml
    /// </summary>
    public partial class BattlesView
    {
        private Point _dragStart;
        private BattleAbility _dragItem;

        public BattlesView()
        {
            InitializeComponent();
        }

        private void Master_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var model = DataContext as BattlesViewModel;
            if (model == null) return;

            model.SelectedAbility = e.NewValue as BattleAbility;
            model.SelectedList = e.NewValue as BattleList;

            Details.DataContext = (e.NewValue as BattleAbility);
            Details.IsEnabled = model.SelectedAbility != null;
        }

        // ------------------------------------------------------------------
        // Drag to reorder abilities (e.g. trusts) within their parent list.
        // Summon / resummon order follows list order, so ordering matters.
        // ------------------------------------------------------------------

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                var match = current as T;
                if (match != null) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static BattleAbility GetAbilityAt(object originalSource)
        {
            var item = FindAncestor<TreeViewItem>(originalSource as DependencyObject);
            return item != null ? item.DataContext as BattleAbility : null;
        }

        private BattleList FindParentList(BattleAbility ability)
        {
            var model = DataContext as BattlesViewModel;
            if (model == null || ability == null) return null;

            foreach (var list in model.BattleLists)
                if (list.Actions.Contains(ability))
                    return list;

            return null;
        }

        private void Master_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Don't start drags from the enable checkbox.
            if (e.OriginalSource is CheckBox ||
                FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null)
            {
                _dragItem = null;
                return;
            }

            _dragStart = e.GetPosition(null);
            _dragItem = GetAbilityAt(e.OriginalSource);
        }

        private void Master_OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragItem == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var delta = _dragStart - e.GetPosition(null);
            if (System.Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                System.Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var item = _dragItem;
            _dragItem = null;
            DragDrop.DoDragDrop(Master, item, DragDropEffects.Move);
        }

        private void Master_OnDrop(object sender, DragEventArgs e)
        {
            var dragged = e.Data.GetData(typeof(BattleAbility)) as BattleAbility;
            if (dragged == null) return;

            var target = GetAbilityAt(e.OriginalSource);
            if (target == null || ReferenceEquals(target, dragged)) return;

            var sourceList = FindParentList(dragged);
            var targetList = FindParentList(target);

            // Only reorder within the same list (Trusts stays Trusts, etc).
            if (sourceList == null || !ReferenceEquals(sourceList, targetList)) return;

            var oldIndex = sourceList.Actions.IndexOf(dragged);
            var newIndex = sourceList.Actions.IndexOf(target);
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;

            sourceList.Actions.Move(oldIndex, newIndex);
            e.Handled = true;
        }
    }
}