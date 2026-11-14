using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace SightoHear.Models
{
    /// <summary>
    /// 图库的"一行"，是 ItemsRepeater 的最小虚拟化单元。
    /// 仅该组第一行才显示日期标题 Header。
    ///
    /// ★ Native AOT 必需：GalleryPage 的
    /// &lt;CollectionViewSource IsSourceGrouped="True" ItemsPath="Items"/&gt; 会用经典 {Binding}
    /// 反射读取 <see cref="Items"/>，AOT 下无运行时反射可用，必须由 CsWinRT 源生成器
    /// 在编译期生成 ICustomProperty 访问器，否则进图库页会 0xC000027B 闪退
    /// （与 VideoGroup 同一根因，详见 VideoGroup.cs 注释）。
    /// ★ 必须 partial，否则源生成器不产出代码（诊断 CS8089）。
    /// </summary>
    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class GalleryRow
    {
        public string Header { get; init; } = string.Empty;
        public IReadOnlyList<MediaItem> Items { get; init; } =
            Array.Empty<MediaItem>();
        public Visibility HeaderVisibility =>
            string.IsNullOrEmpty(Header)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }
}
