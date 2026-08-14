using System.Threading;
using System.Diagnostics;
using SightoHear.Helpers;

namespace SightoHear.Services
{
    /// <summary>
    /// 集中管理页面生命周期，每次页面离开时递增全局 generation，
    /// 所有异步操作、ContainerContentChanging、静态事件回调
    /// 通过检查 generation 是否匹配来判断当前页面是否仍处于活跃状态。
    /// </summary>
    public static class PageLifetimeService
    {
        private static int _globalGeneration;
        private static string? _activePageId;

        /// <summary>当前全局 generation。页面离开时递增，陈旧操作据此失效。</summary>
        public static int CurrentGeneration => Volatile.Read(ref _globalGeneration);

        /// <summary>判断某个 generation 是否仍与当前全局 generation 匹配。</summary>
        public static bool IsActive(int generation) => generation == CurrentGeneration;

        /// <summary>
        /// 页面离开时调用，递增全局 generation 使所有陈旧异步操作立即失效。
        /// 应在 Unloaded 事件中调用。
        /// </summary>
        public static void OnNavigatingAway()
        {
            int newGen = Interlocked.Increment(ref _globalGeneration);
            _activePageId = null;
            // 诊断日志：记录 generation 递增（不携带堆栈，避免首次加载 System.Diagnostics.StackTrace 失败导致崩溃）
            AppLogger.Debug($"[Lifetime] OnNavigatingAway → gen={newGen}");
        }

        /// <summary>
        /// 页面进入时调用，记录当前活跃页面标识（仅用于日志和选择性过滤）。
        /// 应在 Loaded 事件中调用。
        /// </summary>
        public static void OnNavigatedTo(string pageId)
        {
            _activePageId = pageId;
        }

        /// <summary>当前活跃页面的标识，可能为 null。</summary>
        public static string? ActivePageId => _activePageId;
    }
}
