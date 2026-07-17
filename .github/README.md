<div align="center">

# Files_MacOS

面向 macOS 打造的现代文件管理器

支持标签页、双栏浏览、网格与详细信息视图，并尽可能提供贴近访达的视觉和操作体验。

</div>

## 项目说明

`Files_MacOS` 专注于 macOS，不再维护或发布 Windows 版本。项目使用 C#、.NET 10、Uno Platform 与原生 AppKit 桥接开发，在保留现代文件管理体验的同时，充分利用 macOS 的系统能力。

项目目前处于持续开发阶段，欢迎提交问题、改进建议和代码贡献。

## 功能亮点

- 标签页、历史记录以及鼠标中键关闭标签页
- 网格视图、详细信息视图与双栏浏览
- 可自定义详细信息列、排序字段和排序方向
- 访达风格侧边栏、系统强调色及中英文界面
- macOS 原生文件图标、应用图标和缩略图
- 文件复制、剪切、粘贴、重命名、拖放与多选
- 废纸篓、永久删除、压缩文件和可移动驱动器弹出
- 快速查看、共享、打开方式以及在终端中打开
- 鼠标侧键前进/后退、键盘快捷定位和常用快捷键
- 窗口、标签页、布局与用户设置持久化

## 界面预览

### 详细信息视图

![Files_MacOS 详细信息视图](./assets/FilesMacOS-Details.png)

### 系统磁盘浏览

![Files_MacOS 系统磁盘浏览](./assets/FilesMacOS-SystemDrive.png)

## 安装

当前提供 Apple Silicon（ARM64）DMG 安装包。下载最新的 `Files_MacOS-0.1.1-macos-arm64.dmg` 后：

1. 双击挂载 DMG。
2. 将 `Files.app` 拖入“应用程序”。
3. 从“应用程序”中启动 Files_MacOS。

## 从源码构建

### 环境要求

- macOS
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Xcode Command Line Tools

### Debug 构建

```bash
dotnet build src/Files.App.MacOS/Files.App.MacOS.csproj \
  -c Debug \
  -r osx-arm64
```

### Release 发布

```bash
dotnet publish src/Files.App.MacOS/Files.App.MacOS.csproj \
  -f net10.0-desktop \
  -c Release \
  -r osx-arm64
```

生成的应用位于：

```text
src/Files.App.MacOS/bin/Release/net10.0-desktop/osx-arm64/Files.app
```

更多构建和发布说明：

- [macOS 移植规划](../docs/macos-porting-plan.md)
- [macOS 移植状态](../docs/macos-porting-status.md)
- [macOS 打包说明](../docs/macos-publishing.md)

## 技术栈

- C# / .NET 10
- Uno Platform / WinUI 风格 XAML
- AppKit、CoreText、Quick Look 与 NSWorkspace 原生桥接
- ARM64 macOS 应用与 DMG 打包

## 贡献

提交代码前请先阅读 [贡献指南](./CONTRIBUTING.md) 和项目根目录的 `AGENTS.md`。提交应聚焦 macOS 功能、兼容性、性能、稳定性或界面体验。

## 许可证

许可证信息请参阅仓库中的 [LICENSE-MIT](../LICENSE-MIT) 与 [LICENSE-MPL](../LICENSE-MPL)。
