<div align="center">

<img src="Assets/SightoHear.png" alt="SightoHear Logo" width="120">

# SightoHear

<h3>统合 Windows 的媒体增强体验！</h3>

**一款基于 WinUI 3 打造的现代化 Windows 多媒体管理播放器，将音乐、视频、图库与回收站四合一，提供流畅的原生 Fluent 体验与强大的增强播放能力。**

<div>
  <img src="https://img.shields.io/badge/Language-C%23-purple" alt="C#">
  <img src="https://img.shields.io/badge/Framework-WinUI%203-blue" alt="WinUI 3">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8">
  <img src="https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows" alt="Windows">
  <img src="https://img.shields.io/badge/License-GPL--3.0-blue" alt="License">
</div>

</div>

SightoHear 是一款面向 Windows 10 20H1+ 与 Windows 11 的原生桌面应用，使用 C# 与 WinUI 3 构建。它以「所见即所得」的媒体库为核心，让你在一个应用里完成本地音乐、视频、图片的浏览、管理与播放，并内置了回收站管理、逐字歌词、Anime4K 实时超分、运动补偿补帧等一系列增强能力。

## ✨ 功能亮点

### 🎵 音乐

- **全格式播放**：基于 FFmpegInteropX 解码，支持 FLAC、OPUS、WAV、AAC、M4A、OGG、WMA 等格式；系统解码失败时自动回退 FFmpeg，无损音质有保障。
- **逐字歌词**：支持 LRC（普通/增强）、QRC、KRC、YRC、TTML 格式的逐字歌词解析与渲染，附带翻译与音译（罗马音）双语合并。
- **多源歌词**：本地文件、音频嵌入标签，以及 QQ 音乐、网易云音乐、酷狗音乐、LrcLib 四大网络源并发检索、匹配度评分择优。
- **沉浸式播放器**：封面高斯模糊背景、GPU 流体背景、逐字扫色歌词动画，支持歌词延迟微调与手动滚动。
- **完善分类**：音乐 / 歌单 / 歌手 / 专辑 / 文件夹五类视图，支持列表、网格、瀑布流三种布局与多字段排序。

### 🎬 视频

- **双内核播放**：默认使用 MediaPlayerElement + FFmpegInteropX（MKV/MP4/AVI/MOV/WMV/FLV/WEBM 等）；可选 libmpv 内核解锁进阶能力。
- **Anime4K 实时超分**：超分模式集成 Anime4K shader 链，提供四档质量（Low/Medium/High/Ultra），观看低清动画也能锐化到高清。
- **运动补偿补帧**：基于 VapourSynth + MVTools/SVPFlow 光流补帧，提供四档补帧模式（×2 或目标 60fps），让画面更顺滑。
- **画中画**：可拖拽调整大小的置顶小窗，边看视频边做其他事。
- **智能播放行为**：记忆播放位置（续播）、自动播放下一个、自动播放、后台播放，全按文件路径记忆，随心配置。
- **快捷键自定义**：8 个内置行为支持单键或组合键绑定，还能「松开按键执行」。

### 🖼️ 图库

- **瀑布流浏览**：按日期分组的瀑布流与列表两种视图，渐进式加载不卡顿。
- **高性能查看器**：基于 Win2D 的图片查看引擎，缩放 / 平移 / 旋转带弹簧物理动画，支持滑动切换与沉浸式全屏。
- **缩略图缓存**：内存 LRU + 磁盘双重缓存，二次打开秒开。

### 🗑️ 回收站

- **系统级回收站**：直接解析 Windows `$Recycle.Bin` 元数据，跨盘符展示所有已删除文件。
- **完整管理**：搜索、还原、永久删除、清空、多选批量操作与属性查看，误删文件一键找回。

### ✨ 更多特色

- **迷你播放器**：全局底部悬浮迷你播放器，随时控制播放。
- **侧边栏快捷方式**：把常听的歌单、歌手、文件夹固定到侧边栏，一键直达。
- **媒体库管理**：勾选展示 / 隐藏指定文件夹，扫描结果实时过滤。
- **统一多选**：音乐、视频、图库、详情页、回收站共用一套多选交互。
- **外观定制**：浅色 / 深色主题，Mica / Acrylic 背景材质，主题色跟随封面实时变化。
- **文件关联与单实例**：双击媒体文件直接打开，重复启动自动合并到已运行窗口。
- **性能可观测**：Win2D 性能监测悬浮窗、GPU 显存监控、崩溃自检与日志系统，开发者友好。

## 🖥️ 系统要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 20H1 (19041) 及以上，或 Windows 11 |
| 架构 | x64 / x86 / ARM64（Anime4K 超分与运动补偿补帧仅支持 x64） |
| 运行时 | Windows App Runtime 2.x（随 MSIX 框架依赖自动加载） |

## 📥 下载安装

前往 [Releases](https://github.com/AvoidGuest/SightoHear/releases) 页面下载最新 `.msix` 安装包，双击即可安装。

> 若未开启旁加载，请先在「设置 → 系统 → 开发者选项」中打开**开发人员模式**或**旁加载应用**。

你也可以使用项目内置脚本自行打包安装：

```powershell
# 一键打包并安装（Beta 版）
Stop-Process -Name "SightoHear" -Force -ErrorAction SilentlyContinue
.\build-msix.ps1 -Install

# 打包正式版
.\build-msix.ps1 -Configuration Release -Platform x64
```

## 🛠️ 技术栈

SightoHear 由以下开源框架与库驱动：

- **C# / .NET 8**：核心语言与运行时。
- **WinUI 3 / Windows App SDK**：原生 Fluent 界面框架。
- **CommunityToolkit for WinUI**：LinedFlow、SettingsCard 等扩展控件。
- **Win2D**：高性能 2D 渲染（图片查看器、歌词、背景特效）。
- **ComputeSharp**：GPU 像素着色器（流体背景）。
- **FFmpegInteropX**：音视频解码内核。
- **libmpv**：超分模式的视频播放内核（OpenGL 渲染）。
- **SkiaSharp**：封面与缩略图的图像处理。
- **Serilog**：结构化日志系统。
- **Lyricify.Lyrics.Helper**：歌词解析与同步。
- **OpenTK / Silk.NET**：OpenGL 与 Direct3D11 绑定，支撑超分渲染链路。

## 🚀 从源码构建

### 环境要求

- Visual Studio 2022 或更高版本，或 VS Code + .NET 8 SDK
- 安装 **.NET 桌面开发** 工作负载（含 Windows App SDK）
- 支持平台：x64 / x86 / ARM64（日常开发统一使用 x64）

### 构建步骤

1. 克隆仓库：

   ```bash
   git clone https://github.com/AvoidGuest/SightoHear.git
   ```

2. 还原依赖：

   ```powershell
   dotnet restore SightoHear.csproj
   ```

3. 打包并安装（推荐）：

   ```powershell
   .\build-msix.ps1 -Install
   ```

4. 或非打包构建（调试用）：

   ```powershell
   dotnet build SightoHear.csproj -c Debug /p:Platform=x64 -v minimal
   ```

## 📄 开源致谢

SightoHear 站在众多优秀开源项目的肩膀上，特此致谢：

| 项目 | 用途 | 许可证 |
|------|------|--------|
| [FFmpegInteropX](https://github.com/ffmpeginteropx/FFmpegInteropX) / [FFmpeg](https://ffmpeg.org/) | 普通模式音视频解码内核 | Apache-2.0 / LGPL-2.1+ |
| [libmpv (mpv)](https://github.com/mpv-player/mpv) | 超分模式视频播放内核 | LGPL-2.1+ |
| [Anime4K](https://github.com/bloc97/Anime4K) | 实时动画超分 / 降噪算法 | MIT |
| [OpenTK](https://github.com/opentk/opentk) | OpenGL 绑定，GL 上下文与渲染 | MIT |
| [Silk.NET](https://github.com/dotnet/Silk.NET) | D3D11 / DXGI / OpenGL 原生绑定 | MIT |
| [Bili.Copilot](https://github.com/Richasy/Bili.Copilot) | 超分模式 mpv 封装移植参考 | GPL-3.0 |
| [BetterLyrics](https://github.com/jayfunc/BetterLyrics) | 歌词解析 / 渲染参考 | GPL-3.0 |
| [VapourSynth](https://github.com/vapoursynth/vapoursynth) | 运动补偿脚本运行环境 | LGPL-2.1 |
| [MVTools](https://github.com/dubhater/vapoursynth-mvtools) | 运动估计与补帧算法 | GPL-2.0 |
| [FFTW](https://www.fftw.org/) | 快速傅里叶变换库 | GPL-2.0+ |
| [K7sfunc](https://github.com/hooke007/K7sfunc) | MEMC 补帧脚本来源 | GPL-3.0 |

此外，本项目还使用了 Windows App SDK、CommunityToolkit、Win2D、ComputeSharp、SkiaSharp、Serilog、Lyricify.Lyrics.Helper 等 NuGet 库，感谢所有开源贡献者。

## ⚖️ License

本项目采用 **GNU General Public License v3.0（GPL-3.0）** 开源协议，详见 [LICENSE](LICENSE) 文件。

> 由于本项目移植 / 参考了 Bili.Copilot、BetterLyrics、K7sfunc 等 GPL-3.0 项目的代码，依据 GPL 传染条款，整体以 GPL-3.0 发布。

---

<div align="center">
<sub>软件全程用 AI 编写</sub>
<br>
<sub>软件图标参考了 Icons8 的部分素材，侵删</sub>
</div>
