using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace SightoHear
{
    /// <summary>
    /// 回收站管理页面，基于 $I 文件解析实现。
    /// 提供回收站内容查看、搜索、还原、永久删除及清空功能。
    /// </summary>
    public sealed partial class RecycleBinPage : Page
    {
        private List<RecycleBinItem> _allItems = new();
        private List<RecycleBinItem> _filteredItems = new();
        private CancellationTokenSource? _loadCts;
        private DispatcherTimer? _debounceTimer;
        private bool _isMultiSelectMode;
        private readonly HashSet<string> _selectedMetaPaths = new(StringComparer.OrdinalIgnoreCase);
        private const int SearchDebounceMs = 300;
        // 复选框点击抑制窗口：记录复选框 PointerPressed 时间戳，用于抑制由复选框输入触发的 ItemClick，
        // 避免点击复选框或双击卡片时发生重复切换（参考 MusicPage 的成熟方案）
        private const double CheckboxInputSuppressMs = 800;
        private string? _lastCheckboxPointerMetaPath;
        private long _lastCheckboxPointerTimestamp;

        public RecycleBinPage()
        {
            InitializeComponent();
            Loaded += RecycleBinPage_Loaded;
            Unloaded += RecycleBinPage_Unloaded;
        }

        private void RecycleBinPage_Loaded(object sender, RoutedEventArgs e)
        {
            _ = LoadRecycleBinAsync();
        }

        private void RecycleBinPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
            _debounceTimer?.Stop();
        }

        /// <summary>
        /// 异步加载回收站内容。
        /// </summary>
        private async Task LoadRecycleBinAsync()
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            try
            {
                StatusText.Text = "正在加载...";
                ItemsList.ItemsSource = null;

                // 在后台线程读取回收站，避免阻塞 UI
                var items = await Task.Run(() => RecycleBinService.GetItems(), token);

                if (token.IsCancellationRequested)
                    return;

                _allItems = items.OrderByDescending(i => i.DeletedDate).ToList();
                _filteredItems = _allItems;

                // 回到 UI 线程更新界面
                ItemsList.ItemsSource = _filteredItems;
                UpdateStatusText();
                UpdateEmptyState();
                AppLogger.Info($"回收站加载完成: 共 {_allItems.Count} 项");
            }
            catch (OperationCanceledException)
            {
                // 加载被取消，静默处理
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RecycleBinPage] 加载失败: {ex}");
                StatusText.Text = "加载失败";
                AppLogger.Error(ex, "回收站加载失败");
            }
        }

        /// <summary>
        /// 刷新回收站内容。
        /// </summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("用户手动刷新回收站");
            await LoadRecycleBinAsync();
        }

        /// <summary>
        /// 回收站信息提示按钮点击事件，打开 TeachingTip。
        /// </summary>
        private void RecycleBinInfoButton_Click(object sender, RoutedEventArgs e)
        {
            RecycleBinTeachingTip.IsOpen = true;
        }

        /// <summary>
        /// 搜索框文本变化（防抖处理）。
        /// </summary>
        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            _debounceTimer?.Stop();

            if (string.IsNullOrWhiteSpace(sender.Text))
            {
                _filteredItems = _allItems;
                ItemsList.ItemsSource = _filteredItems;
                UpdateStatusText();
                UpdateEmptyState();
                return;
            }

            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
            };
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                ApplyFilter(sender.Text);
            };
            _debounceTimer.Start();
        }

        /// <summary>
        /// 应用搜索过滤。
        /// </summary>
        private void ApplyFilter(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                _filteredItems = _allItems;
            }
            else
            {
                string lowerKeyword = keyword.ToLowerInvariant();
                _filteredItems = _allItems
                    .Where(i => i.FileName.ToLowerInvariant().Contains(lowerKeyword)
                             || i.OriginalPath.ToLowerInvariant().Contains(lowerKeyword))
                    .ToList();
            }

            ItemsList.ItemsSource = _filteredItems;
            UpdateStatusText();
            UpdateEmptyState();
        }

        /// <summary>
        /// 切换多选模式。
        /// </summary>
        private void MultiSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isMultiSelectMode)
                ExitMultiSelectMode();
            else
                EnterMultiSelectMode();
        }

        /// <summary>
        /// 进入多选模式。
        /// </summary>
        private void EnterMultiSelectMode()
        {
            _isMultiSelectMode = true;
            _selectedMetaPaths.Clear();
            MultiSelectButton.IsChecked = true;
            BatchActionBar.Visibility = Visibility.Visible;
            UpdateCheckBoxVisibility();
            UpdateSelectedCountText();
        }

        /// <summary>
        /// 退出多选模式。
        /// </summary>
        private void ExitMultiSelectMode()
        {
            _isMultiSelectMode = false;
            _selectedMetaPaths.Clear();
            MultiSelectButton.IsChecked = false;
            BatchActionBar.Visibility = Visibility.Collapsed;
            SelectAllCheckBox.IsChecked = false;
            UpdateCheckBoxVisibility();
            UpdateSelectedCountText();
        }

        /// <summary>
        /// 更新所有 CheckBox 的可见性。
        /// </summary>
        private void UpdateCheckBoxVisibility()
        {
            Visibility checkBoxVisibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;

            foreach (var item in ItemsList.Items)
            {
                if (ItemsList.ContainerFromItem(item) is ListViewItem container)
                {
                    var checkBox = FindCheckBox(container);
                    if (checkBox != null)
                    {
                        checkBox.Visibility = checkBoxVisibility;
                    }
                }
            }
        }

        /// <summary>
        /// 在可视化树中递归查找 CheckBox。
        /// </summary>
        private CheckBox? FindCheckBox(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is CheckBox checkBox)
                    return checkBox;

                var result = FindCheckBox(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        /// <summary>
        /// 更新选中数量文本。
        /// </summary>
        private void UpdateSelectedCountText()
        {
            SelectedCountText.Text = $"已选择 {_selectedMetaPaths.Count} 项";
        }

        /// <summary>
        /// 项 CheckBox 选中状态变化。
        /// </summary>
        private void ItemCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is RecycleBinItem item)
            {
                _selectedMetaPaths.Add(item.MetaFilePath ?? string.Empty);
                UpdateSelectedCountText();
            }
        }

        /// <summary>
        /// 项 CheckBox 取消选中。
        /// </summary>
        private void ItemCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is RecycleBinItem item)
            {
                _selectedMetaPaths.Remove(item.MetaFilePath ?? string.Empty);
                UpdateSelectedCountText();
            }
        }

        /// <summary>
        /// 防止 CheckBox 点击事件与 ListViewItem 选中冲突，并记录本次指针输入，
        /// 用于抑制后续由复选框输入触发的 ItemClick，避免重复切换。
        /// </summary>
        private void ItemCheckBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is CheckBox { DataContext: RecycleBinItem item })
            {
                _lastCheckboxPointerMetaPath = item.MetaFilePath ?? string.Empty;
                _lastCheckboxPointerTimestamp = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// 列表项点击：多选模式下点击卡片任意位置即切换该卡片复选框的勾选状态。
        /// 非多选模式下不做任何处理。
        /// </summary>
        private void ItemsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!_isMultiSelectMode)
                return;

            if (e.ClickedItem is not RecycleBinItem item)
                return;

            // 若本次 ItemClick 来自复选框本身（刚在复选框上按下过），则抑制，
            // 勾选/取消勾选已由复选框的 Checked/Unchecked 事件完成，避免重复切换。
            if (IsRecentCheckboxInput(item.MetaFilePath ?? string.Empty, out _))
                return;

            ToggleItemSelection(item);
        }

        /// <summary>
        /// 切换指定回收站项的选中状态，并同步复选框与选中计数。
        /// </summary>
        private void ToggleItemSelection(RecycleBinItem item)
        {
            string key = item.MetaFilePath ?? string.Empty;
            if (_selectedMetaPaths.Contains(key))
                _selectedMetaPaths.Remove(key);
            else
                _selectedMetaPaths.Add(key);

            if (ItemsList.ContainerFromItem(item) is ListViewItem container)
            {
                var checkBox = FindCheckBox(container);
                if (checkBox != null)
                    checkBox.IsChecked = _selectedMetaPaths.Contains(key);
            }

            UpdateSelectedCountText();
        }

        /// <summary>
        /// 判断指定项是否在抑制窗口内刚发生过复选框指针输入（即本次 ItemClick 来自复选框）。
        /// </summary>
        private bool IsRecentCheckboxInput(string metaPath, out double elapsedMs)
        {
            elapsedMs = _lastCheckboxPointerTimestamp == 0
                ? double.PositiveInfinity
                : (Stopwatch.GetTimestamp() - _lastCheckboxPointerTimestamp) * 1000.0 / Stopwatch.Frequency;

            return elapsedMs <= CheckboxInputSuppressMs &&
                   string.Equals(_lastCheckboxPointerMetaPath, metaPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 卡片双击：非多选模式下弹出还原确认弹窗；多选模式下双击由两次 ItemClick 完成勾选切换，此处仅拦截手势。
        /// </summary>
        private async void ItemCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // 双击事件源来自复选框时忽略（复选框已有自己的双击处理）
            if (e.OriginalSource is DependencyObject source && FindVisualAncestor<CheckBox>(source) != null)
            {
                e.Handled = true;
                return;
            }

            // 多选模式下双击卡片会触发两次 ItemClick（勾选→取消），此处仅拦截手势冒泡，不再弹窗
            if (_isMultiSelectMode)
            {
                e.Handled = true;
                return;
            }

            if (sender is FrameworkElement { DataContext: RecycleBinItem item } element)
            {
                e.Handled = true;
                await RestoreItemAsync(item, $"确定要还原 {item.FileName} 吗？");
            }
        }

        /// <summary>
        /// 复选框双击：拦截手势，避免与卡片双击逻辑冲突。
        /// </summary>
        private void ItemCheckBox_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>
        /// 向上查找指定类型的可视化祖先。
        /// </summary>
        private static T? FindVisualAncestor<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? current = child;
            while (current != null)
            {
                if (current is T target)
                    return target;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        /// <summary>
        /// 右键菜单：还原选中项。
        /// </summary>
        private async void RestoreItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextMenuItem() is not RecycleBinItem item)
                return;

            await RestoreItemAsync(item);
        }

        /// <summary>
        /// 右键菜单：永久删除选中项。
        /// </summary>
        private async void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextMenuItem() is not RecycleBinItem item)
                return;

            await PermanentlyDeleteItemAsync(item);
        }

        /// <summary>
        /// 右键菜单：查看选中项的属性详情。
        /// </summary>
        private async void ItemProperties_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextMenuItem() is not RecycleBinItem item)
                return;

            await ShowItemProperties(item);
        }

        /// <summary>
        /// 展示回收站项的属性详情弹窗（纯文字形式，与音乐页面属性弹窗风格一致）。
        /// 展示文件类型、原始位置、大小、删除时间、创建时间、属性。
        /// </summary>
        private async Task ShowItemProperties(RecycleBinItem item)
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"文件类型：{item.FileTypeText}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"原始位置：{item.OriginalPath}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"大小：{item.SizeText}" });
            content.Children.Add(new TextBlock { Text = $"删除时间：{item.DeletedDateText}" });
            content.Children.Add(new TextBlock { Text = $"创建时间：{item.CreationTimeText}" });
            content.Children.Add(new TextBlock { Text = $"属性：{item.AttributesText}", TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = "回收站属性",
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot
            };
            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        /// <summary>
        /// 批量还原选中项。
        /// </summary>
        private async void RestoreSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMetaPaths.Count == 0)
                return;

            var selectedItems = _allItems
                .Where(i => _selectedMetaPaths.Contains(i.MetaFilePath ?? string.Empty))
                .ToList();

            if (selectedItems.Count == 0)
                return;

            string message = $"确定要还原选中的 {selectedItems.Count} 个文件吗？";
            var dialog = new ContentDialog
            {
                Title = "还原文件",
                Content = message,
                PrimaryButtonText = "还原",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.None
            };

            if (await DialogService.ShowAsync(dialog, XamlRoot, useThemeColorButton: true) != ContentDialogResult.Primary)
                return;

            int restored = 0;
            foreach (var item in selectedItems)
            {
                if (RecycleBinService.RestoreItem(item))
                    restored++;
            }

            await ShowResultDialogAsync("还原完成", $"成功还原 {restored} 个文件。");
            AppLogger.Info($"批量还原完成: 成功 {restored}/{selectedItems.Count}");

            ExitMultiSelectMode();
            await LoadRecycleBinAsync();
        }

        /// <summary>
        /// 批量永久删除选中项。
        /// </summary>
        private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMetaPaths.Count == 0)
                return;

            var selectedItems = _allItems
                .Where(i => _selectedMetaPaths.Contains(i.MetaFilePath ?? string.Empty))
                .ToList();

            if (selectedItems.Count == 0)
                return;

            string message = $"确认要永久删除选中的 {selectedItems.Count} 个本地磁盘文件吗？此操作不可撤销，无法反悔。";
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = message,
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.None
            };

            if (await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: false) != ContentDialogResult.Primary)
                return;

            int deleted = RecycleBinService.PermanentlyDeleteItems(selectedItems);

            await ShowResultDialogAsync("删除完成", $"已永久删除 {deleted} 个文件。");
            AppLogger.Warning($"批量永久删除完成: 删除 {deleted}/{selectedItems.Count} 个文件");

            ExitMultiSelectMode();
            await LoadRecycleBinAsync();
        }

        /// <summary>
        /// 取消多选模式。
        /// </summary>
        private void CancelMultiSelectButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
        }

        /// <summary>
        /// 清空回收站。
        /// </summary>
        private async void EmptyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_allItems.Count == 0)
            {
                await ShowResultDialogAsync("回收站为空", "回收站中没有文件。");
                return;
            }

            string message = $"回收站中现有 {_allItems.Count} 个文件，确定要全部永久删除吗？此操作不可撤销。";
            var dialog = new ContentDialog
            {
                Title = "清空回收站？",
                Content = message,
                PrimaryButtonText = "清空",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.None
            };

            if (await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: false) != ContentDialogResult.Primary)
                return;

            int deleted = RecycleBinService.EmptyRecycleBin();

            await ShowResultDialogAsync("清空完成", $"已清空回收站，共删除 {deleted} 个文件。");
            AppLogger.Warning($"回收站已清空: 共删除 {deleted} 个文件");

            await LoadRecycleBinAsync();
        }

        /// <summary>
        /// 列表右键点击，显示上下文菜单。
        /// </summary>
        private void ItemsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is FrameworkElement element && element.DataContext is RecycleBinItem item)
            {
                ItemsList.SelectedItem = item;
                var menu = Resources["ItemContextMenu"] as MenuFlyout;
                if (menu != null)
                {
                    var point = e.GetPosition(ItemsList);
                    menu.ShowAt(ItemsList, point);
                }
            }
        }

        /// <summary>
        /// 获取上下文菜单对应的数据项。
        /// </summary>
        private RecycleBinItem? GetContextMenuItem()
        {
            if (ItemsList.SelectedItem is RecycleBinItem selectedItem)
            {
                return selectedItem;
            }
            return null;
        }

        /// <summary>
        /// 还原单个文件。可通过 message 参数自定义确认弹窗文案（如双击卡片时的简短确认）。
        /// </summary>
        private async Task RestoreItemAsync(RecycleBinItem item, string? message = null)
        {
            string confirmMessage = message ?? $"确定要还原文件“{item.FileName}”到原始位置吗？\n原始位置: {item.OriginalPath}";
            var dialog = new ContentDialog
            {
                Title = "还原文件",
                Content = confirmMessage,
                PrimaryButtonText = "还原",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.None
            };

            if (await DialogService.ShowAsync(dialog, XamlRoot, useThemeColorButton: true) != ContentDialogResult.Primary)
                return;

            bool success = RecycleBinService.RestoreItem(item);

            if (success)
            {
                await ShowResultDialogAsync("还原成功", $"文件“{item.FileName}”已还原。");
                AppLogger.Info($"还原成功: {item.FileName}");
                await LoadRecycleBinAsync();
            }
            else
            {
                await ShowResultDialogAsync("还原失败", $"无法还原文件“{item.FileName}”，请检查原始位置是否可用。");
                AppLogger.Warning($"还原失败: {item.FileName}");
            }
        }

        /// <summary>
        /// 永久删除单个文件。
        /// </summary>
        private async Task PermanentlyDeleteItemAsync(RecycleBinItem item)
        {
            string message = $"确认要永久删除本地磁盘文件“{item.FileName}”吗？此操作不可撤销，无法反悔。";
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = message,
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.None
            };

            if (await DialogService.ShowAsync(dialog, XamlRoot, isFileDelete: false) != ContentDialogResult.Primary)
                return;

            bool success = RecycleBinService.PermanentlyDeleteItem(item);

            if (success)
            {
                await ShowResultDialogAsync("删除成功", $"文件“{item.FileName}”已永久删除。");
                AppLogger.Warning($"永久删除成功: {item.FileName}");
                await LoadRecycleBinAsync();
            }
            else
            {
                await ShowResultDialogAsync("删除失败", $"无法删除文件“{item.FileName}”。");
                AppLogger.Warning($"永久删除失败: {item.FileName}");
            }
        }

        /// <summary>
        /// 显示操作结果对话框。
        /// </summary>
        private async Task ShowResultDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定"
            };

            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        /// <summary>
        /// 更新状态栏文本。
        /// </summary>
        private void UpdateStatusText()
        {
            if (_filteredItems.Count == _allItems.Count)
            {
                StatusText.Text = $"共 {_allItems.Count} 项";
            }
            else
            {
                StatusText.Text = $"共 {_allItems.Count} 项，当前显示 {_filteredItems.Count} 项";
            }
        }

        /// <summary>
        /// 更新空状态显示。
        /// </summary>
        private void UpdateEmptyState()
        {
            bool isEmpty = _filteredItems.Count == 0;
            ItemsList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            EmptyStateText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 列表加载完成，更新 CheckBox 可见性。
        /// </summary>
        private void ItemsList_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateCheckBoxVisibility();
        }

        /// <summary>
        /// 全选 CheckBox 选中。
        /// </summary>
        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isMultiSelectMode) return;
            foreach (var item in _filteredItems)
            {
                _selectedMetaPaths.Add(item.MetaFilePath ?? string.Empty);
                if (ItemsList.ContainerFromItem(item) is ListViewItem container)
                {
                    var cb = FindCheckBox(container);
                    if (cb != null) cb.IsChecked = true;
                }
            }
            UpdateSelectedCountText();
        }

        /// <summary>
        /// 全选 CheckBox 取消选中。
        /// </summary>
        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_isMultiSelectMode) return;
            _selectedMetaPaths.Clear();
            foreach (var item in _filteredItems)
            {
                if (ItemsList.ContainerFromItem(item) is ListViewItem container)
                {
                    var cb = FindCheckBox(container);
                    if (cb != null) cb.IsChecked = false;
                }
            }
            UpdateSelectedCountText();
        }
    }
}
