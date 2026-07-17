# 为 Files_MacOS 贡献代码

欢迎提交 macOS 文件管理、兼容性、性能、稳定性、本地化和界面体验方面的改进。

## 开发环境

- macOS 13 或更高版本
- .NET 10 SDK
- Xcode Command Line Tools
- 推荐使用 JetBrains Rider

请使用 `Files.MacOS.slnx`，并将 `Files.App.MacOS` 设置为启动项目。Xcode 主要用于调试 `Native/FilesMacOSBridge.m` 或处理签名，不用于打开解决方案。

## 提交改动

1. 一个提交或拉取请求只处理一类相关问题。
2. UI 文本必须放入 `Strings` 本地化资源。
3. 原生功能应通过现有 Objective-C Bridge 和托管互操作层实现。
4. 文件操作、权限、废纸篓、剪贴板、拖放和签名改动需要手动回归验证。
5. 提交前执行：

```bash
dotnet build src/Files.App.MacOS/Files.App.MacOS.csproj \
  -c Debug \
  -v:quiet \
  -clp:ErrorsOnly
git diff --check
```

请在拉取请求中说明复现步骤、验证结果，以及无法在本机验证的限制。
