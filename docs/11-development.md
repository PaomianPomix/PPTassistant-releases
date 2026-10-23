# 开发指南

PPTAssistant 是 C# / .NET 8 / WPF 桌面应用，通过进程外 COM 挂接演示文稿应用。构建和运行需要 Windows；本页命令在仓库根目录的 PowerShell 中执行。

## 开发环境

- Windows x64、Git 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
- 编辑器可选 Visual Studio 2022 或其他 C# 编辑器，命令行构建不依赖 Visual Studio。
- 首次还原依赖需要访问 NuGet。
- 验证真实放映需要桌面版 PowerPoint 和一份可用于测试的 `.pptx`。
- 构建 Windows 安装包另需 [Inno Setup 6.3 或更高版本](https://jrsoftware.org/isdl.php)。

```powershell
git clone https://github.com/PaomianPomix/PPTassistant-releases.git PPTAssistant
Set-Location .\PPTAssistant
dotnet --info
dotnet restore .\src\PPTAssistant.sln
dotnet build .\src\PPTAssistant.sln -c Debug
dotnet test .\src\PPTAssistant.Tests\PPTAssistant.Tests.csproj -c Debug --no-build
dotnet run --project .\src\PPTAssistant.App\PPTAssistant.App.csproj -c Debug --no-build
```

应用启动后驻留托盘。重新构建前先结束放映并退出托盘应用，避免运行进程锁定输出 DLL。只有构建成功后才使用 `--no-build` 启动或测试；Release 构建和测试都应指定 `-c Release`。

## 工程结构

公开解决方案包含 8 个产品项目和 1 个测试项目。

| 项目 | 职责 |
| --- | --- |
| `PPTAssistant.App` | 进程入口、单实例、托盘、设置、日志与组件组装 |
| `PPTAssistant.Session` | 放映会话、工具操作编排、板书记录和写回流程 |
| `PPTAssistant.Host` | 宿主发现、COM 挂接、放映事件、翻页与课件操作 |
| `PPTAssistant.Overlay` | 透明覆盖层、输入分流、墨迹渲染与擦除 |
| `PPTAssistant.Toolbar` | 工具栏、翻页角区、色板、笔刷、缩略图和放大镜 |
| `PPTAssistant.Coords` | 坐标变换、DPI、几何、输入算法与布局度量 |
| `PPTAssistant.InkML` | 墨迹模型、颜色及 InkML 读写 |
| `PPTAssistant.Package` | OOXML 包读写、墨迹注入、备份与校验 |
| `PPTAssistant.Tests` | xUnit 自动化测试 |

工具栏通过事件向会话层表达操作意图，由会话层调用宿主和覆盖层。修改输入、翻页或保存行为时，应同时检查 UI 回调、会话参数和持久化配置的连接。

应用入口位于 [Program.cs](../src/PPTAssistant.App/Program.cs)，组件组装位于 [AppHost.cs](../src/PPTAssistant.App/AppHost.cs)。共享编译设置位于 [Directory.Build.props](../src/Directory.Build.props)：目标框架 `net8.0-windows`，启用 nullable、隐式 using 和 COM interop。界面主要由 C# 构建；样式与度量应优先复用现有定义。

## 构建 Windows 安装包

安装 .NET 8 SDK 与 Inno Setup 6.3 或更高版本后运行：

```powershell
.\scripts\build-release.ps1 -Version 1.0.0
```

使用非默认位置的工具时，可以显式指定路径：

```powershell
.\scripts\build-release.ps1 -Version 1.0.0 `
  -DotnetPath 'C:\Program Files\dotnet\dotnet.exe' `
  -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

脚本生成 Windows x64 自包含程序，安装包及校验和输出为：

- `artifacts/release/PPTAssistant-1.0.0-win-x64-Setup.exe`
- `artifacts/release/SHA256SUMS.txt`

最终用户无需另装 .NET。测试程序集不包含在应用安装包中。完整参数见 [build-release.ps1](../scripts/build-release.ps1)。

有仓库写入权限的维护者也可在 GitHub 的 **Actions → Build Windows installer → Run workflow** 输入版本号构建。工作流在 Windows 上运行单元测试、生成安装包，并检查安装、启动、卸载和用户数据保留；成功后可下载包含安装包及校验和的构建产物。

发布已验证的安装包时，运行 **Actions → Publish Windows release**，填写版本号、成功构建的运行 ID 和对应源码提交 SHA。工作流会验证构建结果与校验和，将同一安装包上传到正式 Release；已有版本不会被覆盖。

如需单独发布应用目录，可执行：

```powershell
dotnet publish .\src\PPTAssistant.App\PPTAssistant.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:Version=1.0.0 -o .\artifacts\publish\win-x64
```

发布版本号应与 GitHub Release 标签一致，例如程序 `1.0.0` 对应 `v1.0.0`。更新检查使用 [公开仓库](https://github.com/PaomianPomix/PPTassistant-releases) 的最新正式 Release；草稿和预发行版本不进入应用的正式更新通道。更改标签不会改变已经构建的程序版本。

## 开发与验证

- 新逻辑优先放在可独立测试的模块，UI 事件负责连接行为。
- 配置增加或改名时，同步修改默认值、加载容错、设置回调和 [配置参考](13-configuration.md)。
- 书写、擦除和坐标变化需检查不同 DPI、屏幕原点及输入设备；保存逻辑需检查备份、恢复和失败回滚。
- UI 修改需检查实际窗口尺寸、控件命中、键盘访问与焦点，不只检查编译结果。
- 不提交 `bin/`、`obj/`、SDK、构建产物或本机用户数据。

测试命令和手工检查见 [测试指南](12-testing.md)。提交问题或修改说明时，说明触发条件、修改后的行为、实际验证结果及尚未验证的环境。
