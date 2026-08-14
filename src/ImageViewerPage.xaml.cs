using SightoHear.Helpers;
using SightoHear.Models;
using SightoHear.ImageViewer;
using SightoHear.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;
using Microsoft.UI.Dispatching;

namespace SightoHear
{
    /// <summary>
    /// 图片全屏查看器（缩放/平移/旋转渲染管线移植自 FlyPhotos：
    /// Win2D FreeRunCanvas + 视图状态模型 + 弹簧动画 + mipmap 渲染）。
    /// 所有缩放/平移/旋转交互由 <see cref="CanvasDisplayController"/> 在 GPU 渲染线程完成，
    /// 本页只负责：加载图片、转发指针/键盘事件、保留原有工具栏/全屏/沉浸等周边逻辑。
    /// </summary>
    public sealed partial class ImageViewerPage : Page
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        // --- 渲染控制器（W2D 渲染管线） ---
        private CanvasDisplayController? _controller;
        private int _loadGeneration;          // 图片加载竞态保护：仅最新一次加载生效
        private CanvasBitmap? _pendingBitmap; // 设备就绪前暂存的已加载位图
        private bool _deviceReadyOnce;        // 首次设备就绪已处理
        private bool _isFitMode = true;
        private bool _isOneToOne;
        private int _currentZoomPercent = 100;

        // --- 图片列表与导航 ---
        private MediaItem? _currentItem;
        private List<MediaItem> _playlist = new();
        private int _currentIndex = -1;
        private SlideDirection _slideDirection = SlideDirection.None; // 切图滑入方向（消费后重置）

        // --- 上次打开记录控制 ---
        // 查看封面等临时预览场景为 true：不写入 LastImagePath/LastImageTime，
        // 避免覆盖主页"上次打开"大卡片中的音乐/视频记录。
        private bool _skipLastOpenedRecording;

        // --- 窗口状态 ---
        private AppWindow _appWindow = null!;
        private Windows.Graphics.SizeInt32 _previousSize;
        private Windows.Graphics.PointInt32 _previousPosition;
        private bool _previousPositionSet;

        private bool _isImmersiveViewerMode;
        private bool _isImageFullScreen;
        private bool _isWindowClosing;
        private bool _isPageUnloading;

        // --- 窗口拖动 ---
        private bool _isDraggingWindow;
        private NativePoint _lastWindowDragPoint;

        // --- 图片拖动 ---
        private bool _isDraggingImage;
        private Point _lastDragPoint;
        private uint _activeImagePointerId;

        public ImageViewerPage()
        {
            InitializeComponent();
            // 应用 Win2D GPU 选择（手动指定时使用自定义渲染设备；跟随系统时为 null 走共享设备）
            ViewerCanvas.CustomDevice = Win2DDeviceManager.CustomDevice;
            ViewerCanvas.MaxFps = 1000; // 限制最大帧率 1000 帧/秒，避免 GPU 空转
            Unloaded += ImageViewerPage_Unloaded;
            Loaded += ImageViewerPage_Loaded;
            // ★ 资源诊断：登记 Win2D 画布（ViewerCanvas 渲染管线）
            ResourceDiagnosticsService.RegisterCanvas();
        }

        private void ImageViewerPage_Loaded(object sender, RoutedEventArgs e)
        {
            IntPtr hWnd = WindowNative.GetWindowHandle(App.MainWindow);
            WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // 用精确拖拽矩形替换主窗口标题栏默认拖拽区（参考 MainWindow 汉堡/返回按钮的做法）：
            // 只有空白处可拖动窗口/双击最大化，按钮区排除在系统拖拽外、由 XAML 正常响应点击。
            TitleToolbar.SizeChanged += TitleToolbar_SizeChanged;
            UpdateImageViewerTitleBarDragRegions();

            if (App.MainWindow != null)
                App.MainWindow.Closed += MainWindow_Closed;

            RootGrid.KeyDown += RootGrid_KeyDown;
            RootGrid.IsTabStop = true;
            RootGrid.Focus(FocusState.Programmatic);

            // 初始化 Win2D 渲染管线（移植自 FlyPhotos）
            _controller = new CanvasDisplayController(ViewerCanvas);
            _controller.DeviceReady += OnDeviceReady;
            _controller.OnZoomChanged += percent => { _currentZoomPercent = percent; UpdateZoomText(); };
            _controller.OnFitToScreenStateChanged += isFitted => { _isFitMode = isFitted; UpdateZoomText(); };
            _controller.OnOneToOneStateChanged += isOneToOne => { _isOneToOne = isOneToOne; };
            ViewerCanvas.Paused = true; // 无图时不跑渲染循环

            // 若 CreateResources 在控制器构造前已触发（页面挂载即触发），此处兜底安装暂存位图
            TryInstallPendingBitmap();

            // 应用查看器背景色设置（黑色/深灰/浅灰）
            ApplyViewerBackground();

            // 自动全屏（图库设置）：打开图片时直接进入全屏模式
            if (App.SettingsHelper.GalleryAutoFullScreen && !_isImageFullScreen)
            {
                // 延迟到布局完成后切换 Presenter，避免全屏前窗口尺寸未稳定
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => EnterImageFullScreen());
            }
        }

        /// <summary>
        /// 根据图库设置应用查看器背景色：0=黑色（默认），1=深灰，2=浅灰。
        /// 同时同步 Win2D 画布的清屏色（交换链不支持真正透明，必须与背景一致）。
        /// </summary>
        private void ApplyViewerBackground()
        {
            int index = App.SettingsHelper.GalleryViewerBackground;
            Windows.UI.Color color = index switch
            {
                1 => Windows.UI.Color.FromArgb(255, 0x1E, 0x1E, 0x1E),   // 深灰
                2 => Windows.UI.Color.FromArgb(255, 0x3C, 0x3C, 0x3C),   // 浅灰
                _ => Windows.UI.Color.FromArgb(255, 0x10, 0x10, 0x10),   // 黑色（默认）
            };

            var brush = new SolidColorBrush(color);
            RootGrid.Background = brush;
            ImageViewport.Background = brush;
            ViewerCanvas.Background = brush;
            ViewerCanvas.ClearColor = color;

            // 深灰/浅灰背景下工具栏底色略作区分
            TitleToolbar.Background = index switch
            {
                1 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2A, 0x2A, 0x2A)),
                2 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4A, 0x4A, 0x4A)),
                _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)),
            };
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is ImageViewerArgs args)
            {
                _playlist = args.Playlist ?? new List<MediaItem>();
                _currentIndex = args.StartIndex;
                // 查看封面等临时预览场景：跳过"上次打开"记录
                _skipLastOpenedRecording = args.SkipLastOpenedRecording;
                if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    _currentItem = _playlist[_currentIndex];
                    LoadImage(_currentItem);
                }
            }

            EnterImmersiveViewerMode();
            AppLogger.Info($"进入图片查看器 列表{_playlist.Count}项 起始索引{_currentIndex}, 当前文件={_currentItem?.FileName}");
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _isWindowClosing = true;
        }

        private void ImageViewerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isPageUnloading) return;
            _isPageUnloading = true;

            TitleToolbar.SizeChanged -= TitleToolbar_SizeChanged;

            // 恢复主窗口标题栏默认拖拽区域
            try
            {
                (App.MainWindow as MainWindow)?.RestoreTitleBarDragRegions();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "恢复主窗口标题栏拖拽区域失败");
            }

            RootGrid.KeyDown -= RootGrid_KeyDown;

            if (App.MainWindow != null)
                App.MainWindow.Closed -= MainWindow_Closed;

            _pendingBitmap?.Dispose();
            _pendingBitmap = null;

            // 停止 W2D 渲染线程并释放 GPU 资源
            if (_controller != null)
            {
                _controller.DeviceReady -= OnDeviceReady;
                _controller.Dispose();
                _controller = null;
            }

            // ★ 资源诊断：注销 Win2D 画布
            ResourceDiagnosticsService.UnregisterCanvas();

            if (_isWindowClosing)
            {
                AppLogger.Debug("ImageViewerPage_Unloaded: 窗口正在关闭，跳过退出全屏/沉浸模式");
                return;
            }

            if (_isImageFullScreen)
                ExitImageFullScreen();
            if (_isImmersiveViewerMode)
                ExitImmersiveViewerMode();
        }

        // --- 图片加载（CanvasBitmap，共享 GPU 设备） ---

        private void LoadImage(MediaItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                return;

            _currentItem = item;

            // 查看封面等临时预览场景不记录"上次打开"，避免覆盖主页大卡片中的音乐/视频记录
            if (!_skipLastOpenedRecording)
            {
                App.SettingsHelper.LastImagePath = item.FilePath;
                App.SettingsHelper.LastImageTime = DateTime.Now;
                App.SettingsHelper.Save();
            }

            TitleText.Text = item.FileName;
            IndexText.Text = $"{_currentIndex + 1} / {_playlist.Count}";

            var gen = ++_loadGeneration;

            // 消费切图滑入方向（仅本次加载生效；首次加载/设备重建为 None，无滑入动画）
            var slide = _slideDirection;
            _slideDirection = SlideDirection.None;

            UpdateNavigationButtons();
            _ = LoadBitmapAsync(item, gen, slide);
        }

        private async System.Threading.Tasks.Task LoadBitmapAsync(MediaItem item, int gen, SlideDirection slide)
        {
            try
            {
                // 等待控件设备就绪（首次进入时 CreateResources 可能尚未完成）。
                // 必须用控件自身（ViewerCanvas 实现 ICanvasResourceCreatorWithDpi）加载位图，
                // 保证与渲染管线内部设备一致——否则 Draw 时跨设备 DrawImage 会抛
                // COMException 并 fail-fast（0xc000027b，与 MusicPlayerPage 的加载方式保持一致）。
                for (var i = 0; i < 100 && !ViewerCanvas.ReadyToDraw; i++)
                {
                    if (_isPageUnloading || gen != _loadGeneration) return;
                    await System.Threading.Tasks.Task.Delay(50); // 最多等待 5 秒
                }
                if (_isPageUnloading || gen != _loadGeneration) return;

                var bitmap = await CanvasBitmap.LoadAsync(ViewerCanvas, item.FilePath);

                // 竞态检查：期间可能已切换到别的图片
                if (gen != _loadGeneration || _isPageUnloading)
                {
                    bitmap.Dispose();
                    return;
                }

                if (_controller == null || !ViewerCanvas.ReadyToDraw)
                {
                    // 渲染管线尚未就绪：暂存，待设备就绪后安装
                    _pendingBitmap?.Dispose();
                    _pendingBitmap = bitmap;
                    return;
                }

                InstallBitmapNow(bitmap, slide);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"图片加载失败: {item.FilePath}");
            }
        }

        /// <summary>渲染管线就绪后尝试安装暂存位图（Loaded 兜底 + DeviceReady 两处调用，幂等）。</summary>
        private void TryInstallPendingBitmap()
        {
            if (_controller == null || !ViewerCanvas.ReadyToDraw) return;
            if (_pendingBitmap == null) return;

            _deviceReadyOnce = true; // 首次安装可能经由此处完成（CreateResources 早于控制器构造）
            var bmp = _pendingBitmap;
            _pendingBitmap = null;
            InstallBitmapNow(bmp);
        }

        private void InstallBitmapNow(CanvasBitmap bitmap, SlideDirection slide = SlideDirection.None)
        {
            if (_controller == null)
            {
                bitmap.Dispose();
                return;
            }

            // 图片像素尺寸；旋转状态由控制器内部维护（每张图从 0 开始）
            var size = new Size(bitmap.Bounds.Width, bitmap.Bounds.Height);
            _controller.SetSource(bitmap, size, 0, slide);
        }

        /// <summary>Win2D 设备（首次或重建）就绪：安装暂存位图，或重新加载当前图片。</summary>
        private void OnDeviceReady()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isPageUnloading) return;

                var isFirst = !_deviceReadyOnce;
                _deviceReadyOnce = true;

                if (_pendingBitmap != null)
                {
                    // 首次：pending 位图由当前设备加载，直接安装
                    var bmp = _pendingBitmap;
                    _pendingBitmap = null;
                    InstallBitmapNow(bmp);
                }
                else if (!isFirst && _currentItem != null)
                {
                    // 设备重建（如 DPI/全屏切换导致设备丢失）：旧 GPU 位图已失效，
                    // 连同暂存的旧设备位图一并丢弃，重新解码加载
                    _pendingBitmap?.Dispose();
                    _pendingBitmap = null;
                    LoadImage(_currentItem);
                }
            });
        }

        // --- 视图操作（转发到渲染控制器） ---

        private void UpdateZoomText()
        {
            ZoomText.Text = _isFitMode ? "适应" : $"{_currentZoomPercent}%";
        }

        private void FitToWindow()
        {
            _controller?.FitToScreen(true);
        }

        private void ZoomToHundred()
        {
            _controller?.ZoomToHundred();
        }

        private void PreviousImage()
        {
            if (_currentIndex <= 0)
                return;

            _currentIndex--;
            _slideDirection = App.SettingsHelper.GallerySlideAnimation
                ? SlideDirection.Previous
                : SlideDirection.None;
            LoadImage(_playlist[_currentIndex]);
        }

        private void NextImage()
        {
            if (_currentIndex + 1 >= _playlist.Count)
                return;

            _currentIndex++;
            _slideDirection = App.SettingsHelper.GallerySlideAnimation
                ? SlideDirection.Next
                : SlideDirection.None;
            LoadImage(_playlist[_currentIndex]);
        }

        private void UpdateNavigationButtons()
        {
            bool hasPrev = _currentIndex > 0;
            bool hasNext = _currentIndex < _playlist.Count - 1;

            PreviousButton.IsEnabled = hasPrev;
            PreviousButton.Visibility = hasPrev ? Visibility.Visible : Visibility.Collapsed;
            PreviousButton.Opacity = hasPrev ? 0.65 : 0;
            PrevNavButton.IsEnabled = hasPrev;
            PrevNavButton.Visibility = hasPrev ? Visibility.Visible : Visibility.Collapsed;

            NextButton.IsEnabled = hasNext;
            NextButton.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Opacity = hasNext ? 0.65 : 0;
            NextNavButton.IsEnabled = hasNext;
            NextNavButton.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;

            // 按钮可见性变化会改变中列布局（TitleToolbar 尺寸不变，SizeChanged 不会触发），
            // 需延迟到布局完成后刷新拖拽矩形，避免中列按钮落入残留拖拽区被系统拦截
            DispatcherQueue.TryEnqueue(() => UpdateImageViewerTitleBarDragRegions());
        }

        // --- 指针交互（移植自 FlyPhotos：拖动平移、滚轮锚定缩放、双击 1:1/Fit 切换） ---

        private void ViewerCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ViewerCanvas);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            _isDraggingImage = true;
            _activeImagePointerId = e.Pointer.PointerId;
            _lastDragPoint = point.Position;

            ViewerCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void ViewerCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingImage || e.Pointer.PointerId != _activeImagePointerId)
                return;

            var point = e.GetCurrentPoint(ViewerCanvas);
            if (!point.Properties.IsLeftButtonPressed)
            {
                _isDraggingImage = false;
                return;
            }

            // 拖动平移：直接生效、跟手（内部换算 DPI 后交给渲染控制器）
            double dx = point.Position.X - _lastDragPoint.X;
            double dy = point.Position.Y - _lastDragPoint.Y;
            _lastDragPoint = point.Position;

            _controller?.Pan(dx, dy);
            e.Handled = true;
        }

        private void ViewerCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndImageDrag(e);
        }

        private void ViewerCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            EndImageDrag(e);
        }

        private void ViewerCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingImage = false;
        }

        private void EndImageDrag(PointerRoutedEventArgs e)
        {
            if (!_isDraggingImage)
                return;

            _isDraggingImage = false;
            try
            {
                ViewerCanvas.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
            }

            e.Handled = true;
        }

        private void ViewerCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(ViewerCanvas);
            var props = point.Properties;
            var delta = props.MouseWheelDelta;

            // 精度触控板（delta 非 120 整数倍）走直接缩放；普通滚轮走弹簧动画（光标锚定）
            if (Math.Abs(delta) % 120 != 0)
                _controller?.ZoomAtPointPrecision(delta, point.Position);
            else
                _controller?.ZoomAtPoint(delta > 0 ? ZoomDirection.In : ZoomDirection.Out, point.Position);

            e.Handled = true;
        }

        private void ViewerCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            int action = App.SettingsHelper.GalleryViewerDoubleClickAction;
            if (action == 2)
            {
                // 无操作
                e.Handled = true;
                return;
            }

            if (action == 1)
            {
                // 切换上一张 / 下一张（左半区域上一张，右半区域下一张）
                var point = e.GetPosition(ViewerCanvas);
                if (point.X < ViewerCanvas.ActualWidth / 2)
                    PreviousImage();
                else
                    NextImage();
                e.Handled = true;
                return;
            }

            // 默认：双击图片在 100% 与 适应窗口 之间切换（锚定光标位置）
            var p = e.GetPosition(ViewerCanvas);
            if (_isOneToOne)
                _controller?.FitToScreen(true);
            else
                _controller?.ZoomToHundred(p);

            e.Handled = true;
        }

        private void ImageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ImageViewportClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            UpdateNavigationButtons();
        }

        // --- 键盘（保留原有键位，新增 FlyPhotos 常用键位） ---

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.F11)
            {
                ToggleImageFullScreen();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Escape)
            {
                if (_isImageFullScreen)
                    ExitImageFullScreen();
                // ★ 覆盖层内还有上一页（如"音乐播放器 → 图片查看器"）：先返回上一页
                else if (Frame.CanGoBack)
                    Frame.GoBack();
                else if (App.MainWindow is MainWindow mw && mw.IsPlayerOverlayActive)
                    mw.HidePlayerOverlay();

                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Left)
            {
                PreviousImage();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Right)
            {
                NextImage();
                e.Handled = true;
                return;
            }

            // 缩放：0/F = 适应窗口，A = 100%，Up/Down = 放大/缩小
            if (e.Key == VirtualKey.Number0 || e.Key == VirtualKey.NumberPad0 || e.Key == VirtualKey.F)
            {
                FitToWindow();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.A)
            {
                ZoomToHundred();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Up)
            {
                _controller?.ZoomByKeyboard(ZoomDirection.In);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Down)
            {
                _controller?.ZoomByKeyboard(ZoomDirection.Out);
                e.Handled = true;
                return;
            }

            // 旋转：L = 左转，R = 右转
            if (e.Key == VirtualKey.L)
            {
                _controller?.RotateBy90(false);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.R)
            {
                _controller?.RotateBy90(true);
                e.Handled = true;
            }
        }

        // --- 标题栏拖拽区域（参考 MainWindow 的 SetTitleBarDragRegions 做法） ---
        // 用 SetDragRectangles 精确划定"空白区域"为系统拖拽区，按钮区排除在外：
        //   · 空白处：系统原生处理按住拖动 / 双击最大化 / 右键菜单（体验最流畅）
        //   · 按钮区：系统不拦截鼠标事件，XAML 正常响应 Click，绝不触发标题栏指令
        // 进入页面时替换主窗口默认拖拽矩形，退出页面时由 MainWindow 恢复。

        private void TitleToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateImageViewerTitleBarDragRegions();
        }

        private void UpdateImageViewerTitleBarDragRegions()
        {
            if (_appWindow == null || _isPageUnloading) return;

            // 全屏模式下标题栏隐藏，清空拖拽区域（防止全屏下误触发窗口操作）
            if (_isImageFullScreen)
            {
                try
                {
                    _appWindow.TitleBar.SetDragRectangles(Array.Empty<Windows.Graphics.RectInt32>());
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "清空标题栏拖拽区域失败");
                }
                return;
            }

            if (TitleToolbar.ActualWidth <= 0 || TitleToolbar.ActualHeight <= 0) return;

            double scale = TitleToolbar.XamlRoot?.RasterizationScale ?? 1.0;
            int height = (int)Math.Round(TitleToolbar.ActualHeight * scale);

            var rects = new List<Windows.Graphics.RectInt32>();

            // 左列空白：返回按钮右侧 → 中间导航区左侧（覆盖文件名与空白，可拖动/双击最大化）
            double leftStart = GetElementRightInTitlebar(BackButton) + 4;
            double leftEnd = GetElementLeftInTitlebar(CenterPanel) - 4;
            AddTitleBarDragRect(rects, leftStart, leftEnd, height, scale);

            // 中列空白：上一张/下一张按钮都可见时，两者之间的索引文本区域
            if (PreviousButton.Visibility == Visibility.Visible &&
                NextButton.Visibility == Visibility.Visible)
            {
                double centerStart = GetElementRightInTitlebar(PreviousButton) + 4;
                double centerEnd = GetElementLeftInTitlebar(NextButton) - 4;
                AddTitleBarDragRect(rects, centerStart, centerEnd, height, scale);
            }

            try
            {
                _appWindow.TitleBar.SetDragRectangles(rects.ToArray());
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "设置标题栏拖拽区域失败");
            }
        }

        private static void AddTitleBarDragRect(
            List<Windows.Graphics.RectInt32> rects, double startX, double endX, int height, double scale)
        {
            double width = endX - startX;
            if (width <= 0) return;

            rects.Add(new Windows.Graphics.RectInt32
            {
                X = (int)Math.Round(startX * scale),
                Y = 0,
                Width = (int)Math.Round(width * scale),
                Height = height
            });
        }

        /// <summary>元素左边缘相对标题栏的 X 坐标（DIP）。</summary>
        private double GetElementLeftInTitlebar(FrameworkElement element) =>
            element.TransformToVisual(TitleToolbar).TransformPoint(new Point(0, 0)).X;

        /// <summary>元素右边缘相对标题栏的 X 坐标（DIP）。</summary>
        private double GetElementRightInTitlebar(FrameworkElement element) =>
            element.TransformToVisual(TitleToolbar).TransformPoint(new Point(element.ActualWidth, 0)).X;

        // --- 标题栏窗口拖动（原有逻辑保留） ---

        private void TitleToolbar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsInteractiveToolbarElement(e.OriginalSource as DependencyObject))
                return;

            if (!e.GetCurrentPoint(TitleToolbar).Properties.IsLeftButtonPressed || !GetCursorPos(out _lastWindowDragPoint))
                return;

            _isDraggingWindow = true;
            TitleToolbar.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private static bool IsInteractiveToolbarElement(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is ButtonBase)
                    return true;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private void TitleToolbar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingWindow || _appWindow == null ||
                !e.GetCurrentPoint(TitleToolbar).Properties.IsLeftButtonPressed ||
                !GetCursorPos(out NativePoint currentPoint))
                return;

            int deltaX = currentPoint.X - _lastWindowDragPoint.X;
            int deltaY = currentPoint.Y - _lastWindowDragPoint.Y;
            if (deltaX == 0 && deltaY == 0)
                return;

            Windows.Graphics.PointInt32 position = _appWindow.Position;
            _appWindow.Move(new Windows.Graphics.PointInt32 { X = position.X + deltaX, Y = position.Y + deltaY });
            _lastWindowDragPoint = currentPoint;
            e.Handled = true;
        }

        private void TitleToolbar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndWindowDrag(e);
        }

        private void TitleToolbar_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            EndWindowDrag(e);
        }

        private void TitleToolbar_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingWindow = false;
        }

        private void EndWindowDrag(PointerRoutedEventArgs e)
        {
            if (!_isDraggingWindow)
                return;

            _isDraggingWindow = false;
            try
            {
                TitleToolbar.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
            }
            e.Handled = true;
        }

        // --- 沉浸 / 全屏（原有逻辑保留） ---

        private void EnterImmersiveViewerMode()
        {
            if (_isImmersiveViewerMode) return;
            _isImmersiveViewerMode = true;
            (App.MainWindow as MainWindow)?.EnterPlayerFullScreen();
        }

        private void ExitImmersiveViewerMode()
        {
            if (!_isImmersiveViewerMode) return;
            _isImmersiveViewerMode = false;
            try
            {
                (App.MainWindow as MainWindow)?.ExitPlayerFullScreen();
            }
            catch (COMException ex)
            {
                AppLogger.Error(ex, "ExitImmersiveViewerMode: 窗口已关闭，忽略退出沉浸模式操作");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "ExitImmersiveViewerMode: 未知错误");
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isImageFullScreen)
            {
                ExitImageFullScreen();
                return;
            }

            // ★ 覆盖层内还有上一页（如"音乐播放器 → 图片查看器"）：先返回上一页，
            //   而不是直接关闭整个覆盖层；仅当覆盖层第一页时才关闭覆盖层
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
                return;
            }

            if (App.MainWindow is MainWindow mw)
            {
                mw.HidePlayerOverlay();
                return;
            }

            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e) => PreviousImage();

        private void NextButton_Click(object sender, RoutedEventArgs e) => NextImage();

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => _controller?.ZoomByKeyboard(ZoomDirection.Out);

        private void ZoomInButton_Click(object sender, RoutedEventArgs e) => _controller?.ZoomByKeyboard(ZoomDirection.In);

        private void FitButton_Click(object sender, RoutedEventArgs e) => FitToWindow();

        private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => _controller?.RotateBy90(false);

        private void RotateRightButton_Click(object sender, RoutedEventArgs e) => _controller?.RotateBy90(true);

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();

            var openWithItem = new MenuFlyoutItem { Text = "打开方式" };
            openWithItem.Click += (s, args) => _ = OpenWithExternalAsync();
            menu.Items.Add(openWithItem);

            var copyPathItem = new MenuFlyoutItem { Text = "复制路径" };
            copyPathItem.Click += (s, args) => CopyFilePath();
            menu.Items.Add(copyPathItem);

            var showInExplorerItem = new MenuFlyoutItem { Text = "在文件资源管理器中显示" };
            showInExplorerItem.Click += (s, args) => OpenFileLocation();
            menu.Items.Add(showInExplorerItem);

            var propertiesItem = new MenuFlyoutItem { Text = "属性" };
            propertiesItem.Click += (s, args) => _ = ShowPropertiesAsync();
            menu.Items.Add(propertiesItem);

            var options = new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
            };
            menu.ShowAt(MoreButton, options);
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleImageFullScreen();
        }

        private void ToggleImageFullScreen()
        {
            if (_isImageFullScreen)
                ExitImageFullScreen();
            else
                EnterImageFullScreen();
        }

        private void EnterImageFullScreen()
        {
            if (_isImageFullScreen) return;
            _isImageFullScreen = true;

            if (_appWindow != null && !_previousPositionSet)
            {
                _previousSize = _appWindow.Size;
                _previousPosition = _appWindow.Position;
                _previousPositionSet = true;
            }

            TitleToolbar.Visibility = Visibility.Collapsed;
            ToolbarRow.Height = new GridLength(0);
            UpdateImageViewerTitleBarDragRegions(); // 全屏：清空拖拽区域，防止误触发窗口操作

            if (_appWindow != null)
            {
                var fullScreenPresenter = FullScreenPresenter.Create();
                _appWindow.SetPresenter(fullScreenPresenter);
            }

            // 全屏后重新获取焦点，防止键盘事件（ESC / F11）因焦点丢失而无法投递
            RootGrid.Focus(FocusState.Programmatic);

            UpdateFullScreenButtonIcon("\uE73F");
        }

        private void ExitImageFullScreen()
        {
            if (!_isImageFullScreen) return;
            _isImageFullScreen = false;

            if (!_isPageUnloading && !_isWindowClosing)
            {
                TitleToolbar.Visibility = Visibility.Visible;
                ToolbarRow.Height = new GridLength(48);
                UpdateFullScreenButtonIcon("\uE740");
                UpdateImageViewerTitleBarDragRegions(); // 退出全屏：恢复空白区拖拽矩形
            }

            if (_appWindow != null)
            {
                try
                {
                    var overlappedPresenter = OverlappedPresenter.Create();
                    _appWindow.SetPresenter(overlappedPresenter);

                    if (_previousPositionSet)
                    {
                        _appWindow.Resize(_previousSize);
                        _appWindow.Move(_previousPosition);
                        _previousPositionSet = false;
                    }
                }
                catch (COMException ex)
                {
                    AppLogger.Error(ex, "ExitImageFullScreen: 窗口已关闭，忽略设置 Presenter");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "ExitImageFullScreen: 未知错误");
                }
            }
        }

        private void UpdateFullScreenButtonIcon(string glyph)
        {
            if (FullScreenButton.Content is FontIcon fontIcon)
                fontIcon.Glyph = glyph;
        }

        // --- 文件操作（原有逻辑保留） ---

        private void OpenFileLocation()
        {
            if (_currentItem == null) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer",
                    Arguments = $"/select,\"{_currentItem.FilePath}\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void CopyFilePath()
        {
            if (_currentItem == null) return;
            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(_currentItem.FilePath);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            }
            catch { }
        }

        private async System.Threading.Tasks.Task OpenWithExternalAsync()
        {
            if (_currentItem == null) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_currentItem.FilePath);
                var options = new LauncherOptions { DisplayApplicationPicker = true };
                await Launcher.LaunchFileAsync(file, options);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "打开方式");
            }
        }

        private async System.Threading.Tasks.Task ShowPropertiesAsync()
        {
            if (_currentItem == null) return;

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "名称: " + _currentItem.FileName, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "路径: " + _currentItem.FilePath, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "大小: " + FormatFileSize(_currentItem.FileSize), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "修改日期: " + _currentItem.DateModified.ToString("yyyy-MM-dd HH:mm:ss"), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });

            if (_currentItem.PixelWidth > 0 && _currentItem.PixelHeight > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"图片尺寸: {_currentItem.PixelWidth} × {_currentItem.PixelHeight}",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                });
            }

            var dialog = new ContentDialog
            {
                Title = "属性",
                Content = panel,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot
            };

            await DialogService.ShowAsync(dialog, XamlRoot);
        }

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return string.Format("{0:0.##} {1}", size, sizes[order]);
        }
    }

    public class ImageViewerArgs
    {
        public List<MediaItem> Playlist { get; set; } = new();
        public int StartIndex { get; set; } = 0;

        /// <summary>
        /// 是否跳过"上次打开"记录（LastImagePath/LastImageTime）。
        /// 用于音乐播放器"查看封面"等临时预览场景：查看封面不应覆盖主页大卡片的"上次打开"记录。
        /// </summary>
        public bool SkipLastOpenedRecording { get; set; }
    }
}
