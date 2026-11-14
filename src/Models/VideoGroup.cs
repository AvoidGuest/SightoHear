using System.Collections.Generic;

namespace SightoHear.Models
{
    /// <summary>
    /// 视频页按日期分组的分组容器。
    ///
    /// ★ Native AOT 必需：<see cref="Items"/> 会被 XAML 的
    /// &lt;CollectionViewSource IsSourceGrouped="True" ItemsPath="Items"/&gt; 以**经典 {Binding} 反射**方式读取。
    /// AOT 下没有运行时反射来生成 ICustomProperty，必须用 CsWinRT 源生成器在编译期生成属性访问器，
    /// 否则一进视频页就抛：
    ///   NotSupportedException: ICustomProperty support used by XAML binding for type
    ///   'SightoHear.Models.VideoGroup' (property 'Items') requires the type to marked with
    ///   'WinRT.GeneratedBindableCustomPropertyAttribute'
    /// 并导致进程 0xC000027B 闪退。
    /// 新增/修改绑定属性后无需改此特性（无参构造 = 为全部 public 可读属性生成访问器）。
    ///
    /// ★★ 注意：带此特性的类型**必须声明为 partial**（且所有父类型也必须是 partial），
    /// 否则 CsWinRT 源生成器只报诊断 CS8089「has attribute GeneratedBindableCustomProperty
    /// but it or a parent type isn't marked partial」而**不生成任何代码**，
    /// 运行时依旧抛上面的 NotSupportedException —— 这是最容易漏的一步。
    /// </summary>
    [WinRT.GeneratedBindableCustomProperty]
    public partial class VideoGroup
    {
        public string Header { get; set; } = string.Empty;
        public List<MediaItem> Items { get; set; } = new();
    }
}
