# ClickSyncMouseTester

[English](#english) | [中文](#zh-cn)

<a id="english"></a>

## English

`ClickSyncMouseTester` is a Windows desktop toolkit for mouse testing and analysis. It is built with `VB.NET + WPF + .NET 10` and focuses on Raw Input capture, input-path inspection, chart-based analysis, and calibration workflows.

The current application includes five production pages:

- `PollingDashboard`: polling-rate monitoring and history charts.
- `MousePerformance`: single-device raw input capture, summary metrics, and a separate chart window.
- `KeyDetection`: timing inspection for mouse buttons, wheel input, and custom keys.
- `SensitivityMatching`: dual-mouse sensitivity matching and recommendation workflow.
- `AngleCalibration`: sensor angle measurement and trajectory-quality inspection.

## Architecture

The runtime structure is roughly:

```text
Application
  -> MainWindow
    -> ShellViewModel
      -> AppPageDescriptor[]
      -> CurrentPage
      -> singleton IRawInputBroker
        -> RawMouseSessionRouter (when needed)
          -> CaptureService
            -> Engine
```

Important implementation notes:

- The product name is now `ClickSyncMouseTester`.
- `RootNamespace` is still `WpfApp1` for compatibility with the current XAML and namespace layout.
- The repository does not currently include a standalone automated test project, although the structure still leaves room for one.

## Repository Layout

The following directories and files are the main source of truth for the project:

- `Controls/`: high-frequency drawing and custom chart controls.
- `Converters/`: UI-only binding converters.
- `docs/`: architecture, migration, and maintenance notes.
- `founts/`: bundled font assets used by the application.
- `Infrastructure/`: MVVM primitives and shared helpers.
- `Models/`: snapshots, enums, event args, and plain data models.
- `My Project/`: VB project metadata and resource glue.
- `Navigation/`: page descriptors and host contracts.
- `Properties/`: publish profiles and project settings.
- `Resources/`: themes, styles, localization, typography, and layout assets.
- `Services/`: Raw Input, Win32 integration, engines, capture services, and global managers.
- `ViewModels/`: shell and page view models.
- `Views/`: page and shell views.
- `build/`: packaging and publish scripts.
- `Application.xaml`, `Application.xaml.vb`, `MainWindow.xaml`, `MainWindow.xaml.vb`: application entry and main shell host.

## Build

Requirements:

- Windows 10 / 11
- `.NET 10 SDK`

Build locally:

```powershell
dotnet build .\ClickSyncMouseTester.vbproj
```

Run locally:

```powershell
dotnet run --project .\ClickSyncMouseTester.vbproj
```

Publish single-file builds:

```powershell
.\build\PublishSingleFile.ps1
.\build\PublishSingleFileRuntimeDependent.ps1
```

## License

The source code in this repository is licensed under the `GNU General Public License v2.0`. See [LICENSE](LICENSE) for the full text.

If you redistribute binaries built from this repository, you should also provide the corresponding source code and preserve the project license text and third-party notices.

Some bundled assets may use different licenses. Please also review `THIRD_PARTY_NOTICES.md` and the original license files inside `founts/`.



<a id="zh-cn"></a>

## 中文说明

`ClickSyncMouseTester` 是一个面向 Windows 的鼠标测试与分析桌面工具，基于 `VB.NET + WPF + .NET 10` 构建，主要用于 Raw Input 采集、输入链路观察、图表分析与校准辅助。

当前版本包含 5 个正式功能页：

- `PollingDashboard`：轮询率检测与历史曲线查看。
- `MousePerformance`：单设备原始输入采集、统计摘要与独立图表窗口。
- `KeyDetection`：鼠标按键、滚轮和自定义按键时序检测。
- `SensitivityMatching`：双鼠标灵敏度复制与推荐结果计算。
- `AngleCalibration`：传感器角度测量与轨迹质量观察。

## 当前架构

运行时结构大致如下：

```text
Application
  -> MainWindow
    -> ShellViewModel
      -> AppPageDescriptor[]
      -> CurrentPage
      -> singleton IRawInputBroker
        -> RawMouseSessionRouter（按需）
          -> CaptureService
            -> Engine
```

当前实现上的几个关键点：

- 产品名已经统一为 `ClickSyncMouseTester`。
- 为兼容现有 XAML 和命名空间，`RootNamespace` 仍保留为 `WpfApp1`。
- 仓库当前没有单独提交自动化测试工程，但结构设计仍然预留了测试扩展空间。

## 仓库结构

以下目录和文件是当前项目的主要源码与元数据：

- `Controls/`：高频绘制与自定义图表控件。
- `Converters/`：展示层绑定转换器。
- `docs/`：架构、迁移与维护文档。
- `founts/`：项目使用的字体资源。
- `Infrastructure/`：MVVM 基础设施与通用辅助。
- `Models/`：快照、枚举、事件参数与纯数据模型。
- `My Project/`：VB 项目资源与程序集元数据。
- `Navigation/`：页面描述与宿主契约。
- `Properties/`：发布配置与项目属性。
- `Resources/`：主题、样式、本地化、字体与布局资源。
- `Services/`：Raw Input、Win32 交互、算法引擎、采集服务与全局管理器。
- `ViewModels/`：Shell 与页面 ViewModel。
- `Views/`：页面与 Shell 视图。
- `build/`：打包与发布脚本。
- `Application.xaml`、`Application.xaml.vb`、`MainWindow.xaml`、`MainWindow.xaml.vb`：应用入口与主宿主窗口。

## 构建方式

环境要求：

- Windows 10 / 11
- `.NET 10 SDK`

本地构建：

```powershell
dotnet build .\ClickSyncMouseTester.vbproj
```

本地运行：

```powershell
dotnet run --project .\ClickSyncMouseTester.vbproj
```

发布单文件版本：

```powershell
.\build\PublishSingleFile.ps1
.\build\PublishSingleFileRuntimeDependent.ps1
```

## 开源协议

本项目代码部分采用 `GNU General Public License v2.0` 开源协议。完整协议文本见 [LICENSE](LICENSE)。

如果你分发基于本仓库构建的二进制程序，应同时提供对应源码，并保留本项目的许可证文本与第三方资源说明。

仓库中的第三方资源可能使用不同许可证，请同时查看 `THIRD_PARTY_NOTICES.md` 与 `founts/` 内原始授权文件。

