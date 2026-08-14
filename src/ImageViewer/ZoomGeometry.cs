using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace SightoHear.ImageViewer
{
    /// <summary>
    /// 缩放/适应相关的纯几何计算（移植自 FlyPhotos.Display.Controllers.Animation.ZoomGeometry）。
    /// 全部是参数的纯函数，无视图状态，供视图管理与动画路径共享同一套规则。
    /// </summary>
    internal static class ZoomGeometry
    {
        /// <summary>滚轮"吸附"缩放级别（StickyZoomLevels 开启时滚轮会落到这些档位上）。</summary>
        private static readonly float[] ZoomSnapPoints = [0.5f, 1.0f, 2.0f, 5.0f, 10.0f];

        /// <summary>适应窗口时四周保留的空白比例（百分比，100 = 不留白）。</summary>
        public const float FitPercentage = 100f;

        /// <summary>
        /// 核心锚点保持公式：缩放从 <paramref name="oldScale"/> 变为 <paramref name="newScale"/> 时，
        /// 返回让屏幕点 <paramref name="anchor"/> 始终压在同一个图片像素上的图片中心位置。
        /// </summary>
        public static Point AnchorPreservingPan(Point anchor, Point currentPos, float oldScale, float newScale)
        {
            var k = newScale / oldScale;
            return new Point(anchor.X - k * (anchor.X - currentPos.X),
                             anchor.Y - k * (anchor.Y - currentPos.Y));
        }

        /// <summary>
        /// 计算把 <paramref name="imageSize"/>（像素）完整放进 <paramref name="canvasSize"/>（像素）的缩放系数。
        /// 考虑当前旋转（90°/270° 时宽高互换）与适配留白；取两轴较小值保证图片完全可见。
        /// </summary>
        public static float CalculateScreenFitScale(Size canvasSize, Size imageSize, int imageRotation)
        {
            // 竖排方向（90°/270°）交换图片有效宽高
            var isVertical = (imageRotation % 180) != 0;
            var effectiveWidth = isVertical ? imageSize.Height : imageSize.Width;
            var effectiveHeight = isVertical ? imageSize.Width : imageSize.Height;

            // 扣除适配留白后的可用画布
            var paddedCanvasWidth = canvasSize.Width * (FitPercentage / 100.0f);
            var paddedCanvasHeight = canvasSize.Height * (FitPercentage / 100.0f);

            var horizontalScale = paddedCanvasWidth / effectiveWidth;
            var verticalScale = paddedCanvasHeight / effectiveHeight;

            // 取较小者 ⇒ 两个轴都不裁切
            return (float)Math.Min(horizontalScale, verticalScale);
        }

        /// <summary>
        /// 吸附缩放开启时，把新算出的 <paramref name="newScale"/> 吸附到缩放方向上第一个跨越的档位；
        /// 否则原样返回。
        /// </summary>
        public static float ApplyZoomSnap(float newScale, float oldScale, ZoomDirection direction)
        {
            if (direction == ZoomDirection.In)
            {
                float? snap = ZoomSnapPoints
                    .Where(s => s > oldScale && s <= newScale)
                    .Cast<float?>()
                    .FirstOrDefault();
                if (snap.HasValue) return snap.Value;
            }
            else
            {
                float? snap = ZoomSnapPoints
                    .Where(s => s < oldScale && s >= newScale)
                    .Cast<float?>()
                    .LastOrDefault();
                if (snap.HasValue) return snap.Value;
            }
            return newScale;
        }

        /// <summary>步进缩放的档位列表：动态"适应窗口"档 + 100% + 400%，去重升序。</summary>
        public static List<float> BuildZoomStops(float screenFitScale) =>
            new List<float> { screenFitScale, 1.0f, 4.0f }.Distinct().OrderBy(s => s).ToList();
    }

    /// <summary>缩放方向。</summary>
    internal enum ZoomDirection
    {
        In,
        Out
    }
}
