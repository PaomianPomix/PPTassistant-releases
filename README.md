<p align="center">
  <img src="src/PPTAssistant.App/Assets/PPTA.png" alt="PPTAssistant 图标" width="480">
</p>

# PPTAssistant

PPTAssistant 是 Windows 上的 PowerPoint 放映助手，也提供无需打开 PowerPoint 的独立白板。启动后驻留系统托盘，在全屏放映时提供书写、擦除、翻页、缩略图导航和放大镜，并可在结束放映时将板书保存到课件。

[下载安装包](https://github.com/PaomianPomix/PPTassistant-releases/releases/latest) · [1.1.0 更新说明](docs/release-notes/1.1.0.md) · [使用指南](docs/10-getting-started.md) · [开发指南](docs/11-development.md) · [配置参考](docs/13-configuration.md)

## 功能

- 悬浮工具栏与屏幕两侧翻页按钮，支持缩略图跳页。
- 鼠标、触摸与压感笔书写；可调笔宽、预设颜色、自定义颜色和最近使用颜色。
- 整笔擦除，长按橡皮清空当前页板书。
- 可拖动放大镜，支持 1–3 倍缩放。
- 内置多页白板：最多 100 页、黑白背景、逐页撤销重做和本机自动保存；从放映工具栏或托盘打开。
- 白板适配 4K、不同 Windows DPI、横屏与竖屏；操作集中在底部，与设置页保持一致的 Notion 风格，按屏幕像素密度导出 PNG。
- 板书按幻灯片保存；写入课件前自动备份，并提供异常退出后的板书恢复入口。
- 分类和搜索设置、75%–200% 放映界面缩放、当前用户开机自启和手动检查更新。

## 安装与使用

需要 Windows 10/11 x64；放映辅助功能推荐使用桌面版 PowerPoint 2016 或更高版本及已保存的 `.pptx` 文件，独立白板无需 PowerPoint。Windows 安装包包含 .NET 运行时，无需另装 .NET。

1. 在 [Releases](https://github.com/PaomianPomix/PPTassistant-releases/releases) 下载 `PPTAssistant-<版本>-win-x64-Setup.exe` 并安装。
2. 启动 PPTAssistant，在系统托盘中找到应用图标。
3. 打开课件，按 **F5** 进入普通全屏放映，使用底部工具栏书写。
4. 结束放映后，按提示选择保留、丢弃或稍后处理板书。保留时会保存并暂时关闭课件、备份、写入板书，再重新打开。

设置从托盘菜单打开，更改自动保存。窗口放映和展台模式不显示工具栏。WPS 与旧版 Office 的兼容性应在实际使用环境中验证；完整操作和故障处理见 [使用指南](docs/10-getting-started.md)。

只需白板时，右键托盘选择“打开白板”即可。白板单独保存，下次打开可继续书写；已有白板文件可在新版中打开。升级后应用、托盘、快捷方式和安装程序统一使用本页展示的新图标。

## 从源码运行

在 Windows 上安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，然后在 PowerShell 中执行：

```powershell
git clone https://github.com/PaomianPomix/PPTassistant-releases.git PPTAssistant
Set-Location .\PPTAssistant
dotnet build .\src\PPTAssistant.sln -c Release
dotnet test .\src\PPTAssistant.Tests\PPTAssistant.Tests.csproj -c Release --no-build
dotnet run --project .\src\PPTAssistant.App\PPTAssistant.App.csproj -c Release --no-build
```

应用使用 C#、.NET 8 和 WPF，通过进程外 COM 与演示文稿应用交互。源码位于 `src/`，安装包构建入口为 `scripts/build-release.ps1`。

## 文档

| 文档 | 内容 |
| --- | --- |
| [使用指南](docs/10-getting-started.md) | 安装、首次放映、板书保存和常见问题 |
| [开发指南](docs/11-development.md) | 工程结构、构建、调试和 Windows 安装包 |
| [测试指南](docs/12-testing.md) | 自动化测试和手工功能检查 |
| [配置参考](docs/13-configuration.md) | 设置、配置文件、日志与备份路径 |

## 数据与更新

配置、日志、板书记录和课件备份保存在当前用户的本机目录。应用没有遥测或课件上传功能；仅在点击“检查更新”时访问 GitHub，并由用户打开发布页下载更新。文件位置见 [配置参考](docs/13-configuration.md)。

反馈问题时，请提供应用版本、Windows/PowerPoint 版本、复现步骤，以及相关日志；显示或输入问题请同时说明分辨率、Windows 缩放和输入设备。可在 [Issues](https://github.com/PaomianPomix/PPTassistant-releases/issues) 提交反馈。
