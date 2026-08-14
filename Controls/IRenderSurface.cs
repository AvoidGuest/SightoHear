using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using Windows.Foundation;

namespace SightoHear.Controls
{
    /// <summary>
    /// 无垂直同步（VSync）限制的 Win2D 渲染表面抽象。
    ///
    /// 替代 <see cref="CanvasAnimatedControl"/> 作为各渲染器 / 控制器的参数类型：
    /// 渲染器只依赖该接口获得尺寸 / DPI / 设备 / 交换链与绘制会话，
    /// 不关心底层是哪个控件、跑在哪个线程，改动集中、可控。
    ///
    /// 实现此接口的控件内部使用「CanvasSwapChain + CanvasSwapChainPanel + 自建紧密渲染循环」，
    /// 每次循环 Present(0) 且绝不调用 WaitForVerticalBlank()，从而让"跟随系统 / 默认 GPU"
    /// 也能跑满帧率（不受显示器刷新率锁定），且与 GPU 实例数量无关，适用于所有电脑。
    /// </summary>
    public interface IRenderSurface : ICanvasResourceCreatorWithDpi
    {
        /// <summary>表面当前尺寸（DIP，物理像素 = DIP × DpiScale）。</summary>
        Size Size { get; }

        /// <summary>当前 DPI 缩放比例（物理像素 / DIP）。</summary>
        float DpiScale { get; }

        /// <summary>渲染管线是否已就绪可绘制（交换链创建成功且尺寸有效）。</summary>
        bool ReadyToDraw { get; }

        /// <summary>底层交换链（渲染器需要 ResizeBuffers / Present 等高级操作时使用）。</summary>
        CanvasSwapChain? SwapChain { get; }

        /// <summary>是否暂停渲染循环（true 时渲染线程休眠等待，不空转 CPU）。</summary>
        bool Paused { get; set; }

        /// <summary>UI 线程调度队列（后台线程结果回 UI 线程的唯一关口）。</summary>
        DispatcherQueue DispatcherQueue { get; }

        /// <summary>停止渲染线程并释放 GPU 资源（页面卸载时调用）。</summary>
        void RemoveFromVisualTree();

        /// <summary>设备（首次或重建）就绪，需重新创建 GPU 资源（位图 / 命令列表等）。</summary>
        event FreeRunCreateResourcesHandler? CreateResources;

        /// <summary>每帧逻辑更新（携带真实时钟计时的流逝时间）。</summary>
        event FreeRunUpdateHandler? Update;

        /// <summary>每帧绘制（提供本帧绘制会话）。</summary>
        event FreeRunDrawHandler? Draw;

        /// <summary>表面尺寸变化（布局系统驱动）。</summary>
        event SizeChangedEventHandler? SizeChanged;
    }

    /// <summary>CreateResources 事件委托（sender 为渲染表面）。</summary>
    public delegate void FreeRunCreateResourcesHandler(IRenderSurface sender, FreeRunCreateResourcesEventArgs e);

    /// <summary>Update 事件委托（sender 为渲染表面）。</summary>
    public delegate void FreeRunUpdateHandler(IRenderSurface sender, FreeRunUpdateEventArgs e);

    /// <summary>Draw 事件委托（sender 为渲染表面）。</summary>
    public delegate void FreeRunDrawHandler(IRenderSurface sender, FreeRunDrawEventArgs e);

    /// <summary>
    /// CreateResources 事件参数（对应 <see cref="CanvasAnimatedControl.CreateResources"/>）。
    /// Win2D 官方事件参数类不可构造，故自定义；本自建管线在渲染线程同步触发，
    /// 事件处理器内应只做资源创建（可自行启动异步加载，无需等待）。
    /// </summary>
    public sealed class FreeRunCreateResourcesEventArgs
    {
    }

    /// <summary>
    /// Update 事件参数（对应 <see cref="CanvasAnimatedControl.Update"/>）。
    /// <see cref="ElapsedTime"/> 由渲染循环用真实时钟（Stopwatch）测量，
    /// 即 Present 到 Present 的实际间隔——保证 HUD 帧率统计真实，不依赖 Update 回调次数。
    /// </summary>
    public sealed class FreeRunUpdateEventArgs
    {
        /// <summary>距上次 Update 的真实流逝时间（渲染循环 Stopwatch 计时）。</summary>
        public TimeSpan ElapsedTime { get; init; }

        /// <summary>自渲染循环启动以来的累计时间（总时间）。</summary>
        public TimeSpan TotalTime { get; init; }
    }

    /// <summary>
    /// Draw 事件参数（对应 <see cref="CanvasAnimatedControl.Draw"/>）。
    /// <see cref="DrawingSession"/> 由 <see cref="CanvasSwapChain.CreateDrawingSession(Color)"/> 创建，
    /// 本帧绘制结束后由渲染循环统一 Present(0)。
    /// </summary>
    public sealed class FreeRunDrawEventArgs
    {
        /// <summary>本帧绘制会话（像素单位，背景已清为 ClearColor）。</summary>
        public CanvasDrawingSession DrawingSession { get; init; } = null!;
    }
}
