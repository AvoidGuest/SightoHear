using SightoHear.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SightoHear.Helpers
{
    /// <summary>
    /// 统一的「添加项目」弹窗工具，供音乐歌单和视频收藏夹使用。
    /// 解决了旧版弹窗右边距过大、列表项无悬停反馈的问题。
    /// </summary>
    public static class ItemPickerDialog
    {
        /// <summary>
        /// 显示一个带搜索/全选功能的项目选择弹窗。
        /// </summary>
        /// <param name="xamlRoot">当前页面的 XamlRoot</param>
        /// <param name="title">弹窗标题（如"添加歌曲""添加视频"）</param>
        /// <param name="options">待选择的项目列表</param>
        /// <param name="itemTemplate">列表项的 DataTemplate，DataContext 为 AddItemOption</param>
        /// <param name="selectAllButton">「全选」按钮（外部创建，内部接管逻辑）</param>
        /// <returns>用户选中的项目列表；若取消返回 null</returns>
        public static async Task<List<MediaItem>?> ShowAsync(
            XamlRoot xamlRoot,
            string title,
            List<AddItemOption> options,
            DataTemplate itemTemplate)
        {
            var listView = new ListView
            {
                ItemsSource = options,
                ItemTemplate = itemTemplate,
                SelectionMode = ListViewSelectionMode.None,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // ★ 修复 Win11 下添加弹窗列表项悬停/按下颜色反色、抽搐的问题：
            //   ListViewItem 默认模板自带 PointerOver/Pressed 容器级背景反馈，
            //   与 DataTemplate 内卡片自身（GetAddItemCardBrush 主题字典刷子）的反馈
            //   叠加竞争，导致颜色异常。这里将容器反馈背景置为透明，
            //   让卡片单独负责悬停/按下反馈。
            listView.Resources["ListViewItemBackgroundPointerOver"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            listView.Resources["ListViewItemBackgroundPressed"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            listView.Resources["ListViewItemBackgroundSelectedPointerOver"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            listView.Resources["ListViewItemBackgroundSelectedPressed"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            listView.ItemContainerStyle = new Style(typeof(ListViewItem))
            {
                Setters =
                {
                    new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                    new Setter(ListViewItem.PaddingProperty, new Thickness(0)),
                    new Setter(ListViewItem.MarginProperty, new Thickness(0, 0, 0, 6))
                }
            };

            // 全选按钮
            var selectAllButton = new Button
            {
                Content = "全选",
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0)
            };

            selectAllButton.Click += (_, _) =>
            {
                bool anyUnselected = options.Any(opt => !opt.IsSelected);
                foreach (var opt in options)
                    opt.IsSelected = anyUnselected;
                selectAllButton.Content = anyUnselected ? "取消全选" : "全选";
                // 刷新 ListView 以更新 CheckBox 状态
                listView.ItemsSource = null;
                listView.ItemsSource = options;
            };

            // 头部区域：标题 + 全选按钮
            var headerGrid = new Grid
            {
                ColumnSpacing = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(titleBlock, 0);
            headerGrid.Children.Add(titleBlock);

            Grid.SetColumn(selectAllButton, 1);
            headerGrid.Children.Add(selectAllButton);

            // 空状态
            if (options.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    Text = "没有可添加的项目",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.72,
                    Margin = new Thickness(0, 40, 0, 0)
                };
                Grid.SetRow(emptyText, 1);

                var root = new Grid
                {
                    Width = 480,
                    Height = 300,
                    Padding = new Thickness(6, 0, 6, 0),
                    RowSpacing = 0
                };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.Children.Add(headerGrid);
                root.Children.Add(emptyText);

                var dialog = new ContentDialog
                {
                    Content = root,
                    PrimaryButtonText = "添加",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot,
                    Padding = new Thickness(0)
                };

                var result = await DialogService.ShowAsync(dialog, xamlRoot);
                return result == ContentDialogResult.Primary ? new List<MediaItem>() : null;
            }

            // 主布局：固定宽度 480，不被文件名长度撑开
            var mainGrid = new Grid
            {
                Width = 480,
                Height = 520,
                Padding = new Thickness(6, 0, 6, 0),
                RowSpacing = 0
            };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            mainGrid.Children.Add(headerGrid);
            Grid.SetRow(listView, 1);
            mainGrid.Children.Add(listView);

            var dialogMain = new ContentDialog
            {
                Content = mainGrid,
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                Padding = new Thickness(0)
            };

            var dialogResult = await DialogService.ShowAsync(dialogMain, xamlRoot);
            if (dialogResult != ContentDialogResult.Primary)
                return null;

            var selectedItems = options
                .Where(opt => opt.IsSelected)
                .Select(opt => opt.Item)
                .ToList();

            return selectedItems.Count > 0 ? selectedItems : null;
        }
    }
}
