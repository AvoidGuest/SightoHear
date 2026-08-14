using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using SightoHear.Helpers;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SightoHear
{
    internal static class ImageCropDialog
    {
        private const double ViewportSize = 400;
        private const double MinSelSize = 50;
        private const double HandleSize = 16;

        public static async Task<string?> ShowAsync(XamlRoot xamlRoot, StorageFile file)
        {
            uint srcWidth = 400, srcHeight = 400;
#pragma warning disable CS0219 // imageLoaded 用于后续可能的错误提示路径
            bool imageLoaded = false;
#pragma warning restore CS0219

            AppLogger.Info($"[ImageCropDialog] 开始处理文件: {file.Name}");
            string filePath = file.Path; // 只取文件系统路径，完全不用 StorageFile 实例的 WinRT 方法
            Debug.WriteLine($"[ImageCropDialog] 文件路径: {filePath}");

            try
            {
                // 用 SkiaSharp 直接读取原图尺寸，避免 StorageFile.Properties.GetImagePropertiesAsync()
                using var fsProps = File.OpenRead(filePath);
                using var skProps = SKBitmap.Decode(fsProps);
                if (skProps != null)
                {
                    srcWidth = (uint)skProps.Width;
                    srcHeight = (uint)skProps.Height;
                }
                AppLogger.Info($"[ImageCropDialog] SkiaSharp 读取尺寸: {srcWidth}x{srcHeight}");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[ImageCropDialog] SkiaSharp 读取尺寸失败，使用默认 400x400");
            }

            if (srcWidth == 0 || srcHeight == 0)
            {
                AppLogger.Warning($"[ImageCropDialog] width/height 为 0，强制定为 400x400");
                srcWidth = 400;
                srcHeight = 400;
            }

            double scale = Math.Min(ViewportSize / srcWidth, ViewportSize / srcHeight);
            double dispWidth = srcWidth * scale;
            double dispHeight = srcHeight * scale;
            double offsetX = (ViewportSize - dispWidth) / 2;
            double offsetY = (ViewportSize - dispHeight) / 2;

            double selSize = Math.Min(dispWidth, dispHeight) * 0.8;
            double selLeft = offsetX + (dispWidth - selSize) / 2;
            double selTop = offsetY + (dispHeight - selSize) / 2;

            var dimBrush = new SolidColorBrush(ColorHelper.FromArgb(0x88, 0, 0, 0));

            var viewport = new Grid
            {
                Width = ViewportSize,
                Height = ViewportSize,
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, ViewportSize, ViewportSize) }
            };

            // 加载显示用的 BitmapImage（失败不影响弹窗展示）
            var bitmap = new BitmapImage();
            try
            {
                AppLogger.Info("[ImageCropDialog] 加载图片...");
                // 先读入内存，再从内存 BitmapImage 解码，完全避开任何 StorageFile/InMemoryRandomAccessStream 的 WinRT 兼容风险
                byte[] imgBytes;
                using (var fs = File.OpenRead(filePath))
                using (var ms = new MemoryStream())
                {
                    await fs.CopyToAsync(ms);
                    imgBytes = ms.ToArray();
                }
                using var memStream = new MemoryStream(imgBytes, writable: false);
                await bitmap.SetSourceAsync(memStream.AsRandomAccessStream());
                imageLoaded = true;
                AppLogger.Info("[ImageCropDialog] SetSourceAsync 成功");
                Debug.WriteLine("[ImageCropDialog] 预览图加载成功");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "[ImageCropDialog] SetSourceAsync 失败，弹窗继续（无预览图）");
                Debug.WriteLine($"[ImageCropDialog] 预览图加载失败: {ex.GetType().Name}: {ex.Message}");
            }

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var canvas = new Canvas { Width = ViewportSize, Height = ViewportSize };

            var dimLeft = new Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            var dimTop = new Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            var dimRight = new Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            var dimBottom = new Rectangle { Fill = dimBrush, IsHitTestVisible = false };

            var selectionRect = new Border
            {
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(2.5),
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x15, 255, 255, 255)),
                Width = selSize,
                Height = selSize
            };
            Canvas.SetLeft(selectionRect, selLeft);
            Canvas.SetTop(selectionRect, selTop);

            var handleBrush = new SolidColorBrush(Colors.White);
            var handleBorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x66, 0, 0, 0));
            var handles = new Border[4];
            for (int i = 0; i < 4; i++)
            {
                handles[i] = new Border
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    CornerRadius = new CornerRadius(3),
                    Background = handleBrush,
                    BorderBrush = handleBorderBrush,
                    BorderThickness = new Thickness(1),
                    Tag = i.ToString(),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new CompositeTransform { ScaleX = 1, ScaleY = 1 }
                };
            }

            canvas.Children.Add(dimLeft);
            canvas.Children.Add(dimTop);
            canvas.Children.Add(dimRight);
            canvas.Children.Add(dimBottom);
            canvas.Children.Add(selectionRect);
            foreach (var h in handles)
                canvas.Children.Add(h);

            viewport.Children.Add(image);
            viewport.Children.Add(canvas);

            // 确定主题色，给 TextBlock 显式设前景色（Popup 不继承父级 Foreground）
            bool isDark = xamlRoot.Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark;
            var textFg = new SolidColorBrush(isDark ? Colors.White : Colors.Black);

            var hintBlock = new TextBlock
            {
                Text = "拖动选框选择裁切区域，拖拽四角调整大小",
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = textFg
            };

            var stackPanel = new StackPanel { Spacing = 4 };
            var titleBlock = new TextBlock
            {
                Text = "裁剪封面",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = textFg
            };
            stackPanel.Children.Add(titleBlock);
            stackPanel.Children.Add(viewport);
            stackPanel.Children.Add(hintBlock);

            bool isDraggingRect = false;
            bool isDraggingHandle = false;
            int dragHandleIdx = 0;
            Point dragStartPointer = default;
            double dragStartL = selLeft, dragStartT = selTop, dragStartS = selSize;

            void UpdateLayout()
            {
                double l = Canvas.GetLeft(selectionRect);
                double t = Canvas.GetTop(selectionRect);
                double s = selectionRect.Width;

                dimLeft.Width = Math.Max(0, l);
                dimLeft.Height = ViewportSize;
                Canvas.SetLeft(dimLeft, 0);
                Canvas.SetTop(dimLeft, 0);

                dimTop.Width = s;
                dimTop.Height = Math.Max(0, t);
                Canvas.SetLeft(dimTop, l);
                Canvas.SetTop(dimTop, 0);

                dimRight.Width = Math.Max(0, ViewportSize - l - s);
                dimRight.Height = ViewportSize;
                Canvas.SetLeft(dimRight, Math.Min(ViewportSize, l + s));
                Canvas.SetTop(dimRight, 0);

                dimBottom.Width = s;
                dimBottom.Height = Math.Max(0, ViewportSize - t - s);
                Canvas.SetLeft(dimBottom, l);
                Canvas.SetTop(dimBottom, Math.Min(ViewportSize, t + s));

                double hh = HandleSize / 2;
                Canvas.SetLeft(handles[0], l - hh);
                Canvas.SetTop(handles[0], t - hh);
                Canvas.SetLeft(handles[1], l + s - hh);
                Canvas.SetTop(handles[1], t - hh);
                Canvas.SetLeft(handles[2], l + s - hh);
                Canvas.SetTop(handles[2], t + s - hh);
                Canvas.SetLeft(handles[3], l - hh);
                Canvas.SetTop(handles[3], t + s - hh);
            }

            void ClampAndApply(double left, double top, double size)
            {
                size = Math.Max(MinSelSize, Math.Min(size, Math.Min(dispWidth, dispHeight)));
                left = Math.Max(offsetX, Math.Min(offsetX + dispWidth - size, left));
                top = Math.Max(offsetY, Math.Min(offsetY + dispHeight - size, top));
                selectionRect.Width = size;
                selectionRect.Height = size;
                Canvas.SetLeft(selectionRect, left);
                Canvas.SetTop(selectionRect, top);
                UpdateLayout();
            }

            selectionRect.PointerPressed += (_, e) =>
            {
                isDraggingRect = true;
                isDraggingHandle = false;
                dragStartPointer = e.GetCurrentPoint(canvas).Position;
                dragStartL = Canvas.GetLeft(selectionRect);
                dragStartT = Canvas.GetTop(selectionRect);
                dragStartS = selectionRect.Width;
                selectionRect.CapturePointer(e.Pointer);
                e.Handled = true;
            };

            selectionRect.PointerMoved += (_, e) =>
            {
                if (!isDraggingRect) return;
                var pt = e.GetCurrentPoint(canvas).Position;
                ClampAndApply(dragStartL + (pt.X - dragStartPointer.X), dragStartT + (pt.Y - dragStartPointer.Y), dragStartS);
                e.Handled = true;
            };

            selectionRect.PointerReleased += (_, e) =>
            {
                if (!isDraggingRect) return;
                isDraggingRect = false;
                selectionRect.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            };

            selectionRect.PointerCanceled += (_, _) => { isDraggingRect = false; };

            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                var h = handles[i];

                h.PointerEntered += (_, _) =>
                {
                    if (!isDraggingHandle || dragHandleIdx != idx)
                        h.RenderTransform = new CompositeTransform { ScaleX = 1.4, ScaleY = 1.4 };
                };
                h.PointerExited += (_, _) =>
                {
                    if (!isDraggingHandle || dragHandleIdx != idx)
                        h.RenderTransform = new CompositeTransform { ScaleX = 1, ScaleY = 1 };
                };

                h.PointerPressed += (_, e) =>
                {
                    isDraggingHandle = true;
                    dragHandleIdx = idx;
                    dragStartPointer = e.GetCurrentPoint(canvas).Position;
                    dragStartL = Canvas.GetLeft(selectionRect);
                    dragStartT = Canvas.GetTop(selectionRect);
                    dragStartS = selectionRect.Width;
                    h.CapturePointer(e.Pointer);
                    e.Handled = true;
                };

                h.PointerMoved += (_, e) =>
                {
                    if (!isDraggingHandle || dragHandleIdx != idx) return;
                    var pt = e.GetCurrentPoint(canvas).Position;
                    double dx = pt.X - dragStartPointer.X;
                    double dy = pt.Y - dragStartPointer.Y;

                    double newL = dragStartL, newT = dragStartT, newS = dragStartS;

                    switch (idx)
                    {
                        case 0: // Top-Left
                            newS = Math.Max(MinSelSize, dragStartS - Math.Max(dx, dy));
                            newL = dragStartL + dragStartS - newS;
                            newT = dragStartT + dragStartS - newS;
                            break;
                        case 1: // Top-Right
                            newS = Math.Max(MinSelSize, dragStartS + Math.Max(dx, -dy));
                            newT = dragStartT + dragStartS - newS;
                            break;
                        case 2: // Bottom-Right
                            newS = Math.Max(MinSelSize, dragStartS + Math.Max(dx, dy));
                            break;
                        case 3: // Bottom-Left
                            newS = Math.Max(MinSelSize, dragStartS + Math.Max(-dx, dy));
                            newL = dragStartL + dragStartS - newS;
                            break;
                    }

                    ClampAndApply(newL, newT, newS);
                    e.Handled = true;
                };

                h.PointerReleased += (_, e) =>
                {
                    if (!isDraggingHandle || dragHandleIdx != idx) return;
                    isDraggingHandle = false;
                    h.ReleasePointerCapture(e.Pointer);
                    h.RenderTransform = new CompositeTransform { ScaleX = 1, ScaleY = 1 };
                    e.Handled = true;
                };

                h.PointerCanceled += (_, _) =>
                {
                    if (dragHandleIdx == idx)
                    {
                        isDraggingHandle = false;
                        h.RenderTransform = new CompositeTransform { ScaleX = 1, ScaleY = 1 };
                    }
                };
            }

            UpdateLayout();

            AppLogger.Info("[ImageCropDialog] 显示弹窗（Popup）...");
            bool confirmed = await ShowPopupAsync(xamlRoot, stackPanel);
            AppLogger.Info($"[ImageCropDialog] 弹窗结果: {confirmed}");

            if (!confirmed)
                return null;

            double finalL = Canvas.GetLeft(selectionRect);
            double finalT = Canvas.GetTop(selectionRect);
            double finalS = selectionRect.Width;

            double pixelX = (finalL - offsetX) / scale;
            double pixelY = (finalT - offsetY) / scale;
            double pixelSize = finalS / scale;

            pixelX = Math.Max(0, Math.Min(srcWidth - pixelSize, pixelX));
            pixelY = Math.Max(0, Math.Min(srcHeight - pixelSize, pixelY));

            Debug.WriteLine($"[ImageCropDialog] 裁剪参数: x={pixelX:F1} y={pixelY:F1} size={pixelSize:F1}");
            var result = await CropWithSkiaAsync(filePath, (uint)Math.Round(pixelX), (uint)Math.Round(pixelY), (uint)Math.Round(pixelSize));
            Debug.WriteLine($"[ImageCropDialog] 裁剪结果: {(result ?? "null")}");
            return result;
        }

        /// <summary>
        /// 用 Popup 代替 ContentDialog 显示裁剪界面（避免嵌套弹窗 COMException）。
        /// </summary>
        private static async Task<bool> ShowPopupAsync(XamlRoot xamlRoot, UIElement content)
        {
            var tcs = new TaskCompletionSource<bool>();

            // 全屏半透明遮罩
            var overlay = new Rectangle
            {
                Fill = new SolidColorBrush(ColorHelper.FromArgb(0x80, 0, 0, 0)),
                Width = xamlRoot.Size.Width,
                Height = xamlRoot.Size.Height
            };

            // 按钮
            var confirmButton = new Button
            {
                Content = "确定",
                MinWidth = 90,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            var cancelButton = new Button
            {
                Content = "取消",
                MinWidth = 90,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0)
            };

            confirmButton.Click += (_, _) => tcs.TrySetResult(true);
            cancelButton.Click += (_, _) => tcs.TrySetResult(false);

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            buttonRow.Children.Add(cancelButton);
            buttonRow.Children.Add(confirmButton);

            // 内容 + 按钮
            var outerStack = new StackPanel();
            outerStack.Children.Add(content);
            outerStack.Children.Add(buttonRow);

            // Card 面板（模拟 ContentDialog 外观）
            bool cardIsDark = xamlRoot.Content is FrameworkElement r && r.ActualTheme == ElementTheme.Dark;
            var card = new Border
            {
                Background = new SolidColorBrush(cardIsDark
                    ? ColorHelper.FromArgb(255, 43, 43, 43)
                    : ColorHelper.FromArgb(255, 249, 249, 249)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24),
                MaxWidth = 560,
                MinWidth = 440,
                Child = outerStack,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Popup 根容器：设定主题使子控件匹配正确颜色
            var popupRoot = new Grid
            {
                RequestedTheme = cardIsDark ? ElementTheme.Dark : ElementTheme.Light
            };
            popupRoot.Children.Add(overlay);
            popupRoot.Children.Add(card);

            var popup = new Popup
            {
                Child = popupRoot,
                XamlRoot = xamlRoot,
                IsLightDismissEnabled = false
            };

            popup.Closed += (_, _) => tcs.TrySetResult(false);
            popup.IsOpen = true;

            bool result = await tcs.Task;
            popup.IsOpen = false;
            return result;
        }

        /// <summary>
        /// 使用 SkiaSharp 做像素级精准裁剪，然后保存为 JPEG。
        /// 入参为本地文件系统路径，完全使用 .NET File I/O，不依赖 StorageFile WinRT API。
        /// </summary>
        private static async Task<string?> CropWithSkiaAsync(string filePath, uint x, uint y, uint size)
        {
            string? resultPath = null;
            try
            {
                // 用纯 .NET 路径获取本地目录，完全避开 WinRT ApplicationData
                var localFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SightoHear");
                var coverFolder = System.IO.Path.Combine(localFolder, "playlist_covers");
                Directory.CreateDirectory(coverFolder);
                resultPath = System.IO.Path.Combine(coverFolder, $"cover_{Guid.NewGuid():N}.jpg");

                // 用纯 .NET FileStream 读取原图字节，不使用 StorageFile.OpenReadAsync()
                byte[] imageBytes;
                using (var fs = File.OpenRead(filePath))
                using (var ms = new MemoryStream())
                {
                    await fs.CopyToAsync(ms);
                    imageBytes = ms.ToArray();
                }

                // CPU 密集的 SkiaSharp 解码+裁剪放在后台线程
                await Task.Run(() =>
                {
                    using var msDecode = new MemoryStream(imageBytes);
                    using var original = SKBitmap.Decode(msDecode);
                    if (original == null)
                        throw new InvalidOperationException("SkiaSharp 无法解码图片");

                    // 确保裁剪区域不超出原图边界
                    uint cropX = Math.Min(x, (uint)Math.Max(0, original.Width - (int)size));
                    uint cropY = Math.Min(y, (uint)Math.Max(0, original.Height - (int)size));
                    uint cropSize = Math.Min(size, (uint)Math.Min(original.Width - (int)cropX, original.Height - (int)cropY));

                    var srcRect = new SKRectI((int)cropX, (int)cropY, (int)(cropX + cropSize), (int)(cropY + cropSize));
                    using var cropped = new SKBitmap((int)cropSize, (int)cropSize);
                    using var skCanvas = new SKCanvas(cropped);
                    skCanvas.Clear(SKColors.Transparent);
                    skCanvas.DrawBitmap(original, srcRect, new SKRect(0, 0, cropSize, cropSize));

                    using var output = File.OpenWrite(resultPath);
                    cropped.Encode(output, SKEncodedImageFormat.Jpeg, 90);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageCropDialog] 裁剪保存失败: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                AppLogger.Error(ex, "ImageCropDialog: 裁剪保存失败");
                return null;
            }

            return File.Exists(resultPath) ? resultPath : null;
        }
    }
}
