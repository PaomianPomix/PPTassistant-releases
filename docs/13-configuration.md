# 配置参考

推荐通过托盘菜单“设置…”调整配置。设置页提供分类导航和搜索，更改自动保存；放映过程中不能打开设置。

## 设置分类

| 分类 | 可调整内容 |
| --- | --- |
| 放映与界面 | 75%–200% 的放映界面缩放，可恢复 100%，下次放映生效 |
| 画笔与书写 | 笔宽、笔色、最近使用颜色，以及压感笔是否始终用于书写 |
| 板书保存 | 结束放映时询问、自动保留或自动丢弃 |
| 启动与更新 | 当前用户开机自启、当前版本和手动检查更新 |

笔宽和笔色只影响之后的笔画。界面缩放不改变 Windows 缩放、笔宽、墨迹坐标或放大镜的取景倍率；屏幕空间不足时工具窗自动收小。

白板的黑白背景和页面内容在白板中调整。白板工具区也会结合界面缩放、屏幕分辨率与 Windows DPI 适配，并保留至少 44 DIP 的触摸目标；窄屏通过横向滚动显示更多工具，不需要手工填写画布分辨率。

## 文件位置

| 内容 | 路径 |
| --- | --- |
| 配置 | `%APPDATA%\PPTAssistant\config.json` |
| 应用日志 | `%LOCALAPPDATA%\PPTAssistant\logs\app.log` |
| 独立白板 | `%LOCALAPPDATA%\PPTAssistant\Whiteboard\board.pptboard` |
| 板书记录 | `%LOCALAPPDATA%\PPTAssistant\sessions\<课件路径哈希>\<时间>-<进程号>.jsonl` |
| 写回前备份 | `%LOCALAPPDATA%\PPTAssistant\backups\<课件名>-<时间>.pptx` |

托盘菜单可打开配置和日志目录。日志按约 2 MiB 滚转，旧日志保留为 `app.1.log`。板书记录会在启动时清理超过 7 天的文件；成功写回或选择丢弃后删除相应记录。备份不会替代用户自己的课件归档。

应用将设置保存到当前用户目录，不需要服务器、账号或密钥。运行中手动修改 JSON 不会自动重载，而且之后的界面操作可能覆盖手动修改；请先退出应用，编辑后重新启动。

## 配置字段

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| `Version` | `1` | 配置格式版本，无需修改 |
| `UiScale` | `1.0` | 放映界面缩放，范围 `0.75–2.0` |
| `PenWidthDips` | `6.0` | 基准笔宽，范围 `2–50` DIP |
| `PenColorHex` | `"#D91B1B"` | RGB 笔色，接受 `#RRGGBB` 或 `RRGGBB` |
| `RecentPenColors` | `[]` | 最近使用颜色，去重后最多 5 色 |
| `IgnoreTouchWhenPenPresent` | `true` | 发现真实笔设备后忽略触摸输入；仅通过 JSON 调整，重启生效 |
| `AllowMouseDrawing` | `true` | 允许鼠标绘制；仅通过 JSON 调整，重启生效 |
| `AlwaysRoutePenToInk` | `true` | 压感笔始终由 PPTAssistant 书写；关闭后跟随工具栏模式 |
| `WriteBackMode` | `"Ask"` | `Ask` 询问、`AutoRetain` 自动保留、`AutoDiscard` 自动丢弃；启动限制见下文 |
| `AutoStart` | `false` | 上次保存的自启状态快照；实际行为由 Windows 启动项决定 |
| `SurfaceMode` | `"Layered"` | 使用分层透明覆盖层，无需调整 |
| `LastLayoutProfile` | `null` | 保留字段，当前没有作用 |

模式值是 JSON 字符串，不是数字。保存文件中可能出现 `ParsedWriteBackMode`、`SidecarRootPath`、`BackupDirectoryPath`；它们为派生属性，不能通过编辑这些值改变模式或目录。

示例：

```json
{
  "Version": 1,
  "UiScale": 1.0,
  "PenWidthDips": 8.0,
  "PenColorHex": "#D91B1B",
  "RecentPenColors": ["#D91B1B", "#0085CD"],
  "IgnoreTouchWhenPenPresent": true,
  "AllowMouseDrawing": true,
  "AlwaysRoutePenToInk": true,
  "WriteBackMode": "Ask",
  "SurfaceMode": "Layered"
}
```

使用标准 JSON，不添加注释或尾随逗号。省略字段会使用默认值。文件无法读取、JSON 损坏或字段类型不匹配时，应用会使用默认配置并记录错误；数值越界的笔宽和界面比例分别回落到 `6.0` 和 `1.0`。

## 板书保存与恢复

默认 `Ask` 会在放映结束时询问是否保留板书。保留时先保存并关闭课件、创建备份、写入墨迹，再重新打开课件。选择取消或写回失败时保留板书记录。全部板书已擦除时不修改课件。

**当前限制：** 保存的 `WriteBackMode` 尚未传递给重启后的普通放映会话，因此每次启动后的放映会话仍从 `Ask` 开始。设置页显示已保存的模式，但仅打开页面不会应用它；在本次运行中先选择另一项，再选回所需模式即可生效。

手动启动时的残留板书恢复使用已保存的模式。登录自启会延后恢复；从托盘主动选择“恢复上次板书…”始终先询问，每次处理最新的一份记录。如果课件正在被占用，请先保存并关闭课件再恢复。

## 白板数据

白板文档独立自动保存，保留所有页面、当前页、各页参考尺寸、黑白背景和原生笔迹（含笔色、笔宽和压力），最多 100 页。每页的撤销/重做历史仅在当前进程中保留，重启后不恢复。换屏只调整显示，原始笔迹坐标与撤销记录不随之缩放；在额外空白区域书写时会扩展该页范围，擦除或撤销不会把参考范围缩回。

新版使用 v2 存档，支持每页独立宽高；读取 v1 的 1600×900 存档后，在下次成功保存时迁移。旧版程序不支持读取 v2 存档，计划回退应用版本时请先保留原白板文件备份。应用退出后可复制 `board.pptboard` 备份；恢复时先退出应用，再将备份放回相同位置。白板不受 PPT 板书写回模式或 7 天旁路日志清理影响。

“导出 PNG”保存当前页完整可书写区域和背景，不包含底部工具栏；输出像素数按当前屏幕的像素密度计算。图片可查看和分享，但不会保留可继续编辑的笔迹或其他页面，不能代替整个 `board.pptboard` 文档备份。

无法读取的白板文件会先保存为同目录的 `.unreadable-*` 副本，再打开空白文档；副本保存失败时不会覆盖原件。自动保存失败会显示提示并保留内存内容，再次修改或返回时重试。正常退出时若原目录仍不可写，应用尝试应急保存至 `%TEMP%\PPTAssistant-whiteboard-recovery-*.pptboard`，具体位置记录在日志。

## 开机自启

自启默认关闭，开启后在当前用户登录 Windows 时启动应用。实际状态读取当前用户注册表 `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` 中的 `PPTAssistant` 值。手改 JSON 中的 `AutoStart` 不会注册或删除 Windows 启动项。

直接运行 EXE 时，自启命令保存该 EXE 路径及 `--autostart` 参数。通过 `dotnet.exe` 启动开发版本时，应用会生成 `%LOCALAPPDATA%\PPTAssistant\autostart.vbs`，用相同运行时与 DLL 隐藏控制台启动。移动安装或构建目录后，应重新开启自启以更新路径。

如果 Windows 禁用了启动项，请在“Windows 设置 → 应用 → 启动”中启用；应用不会覆盖系统中的禁用状态。自启读写失败时，设置页显示错误并恢复实际状态。

## 更新检查

只有点击“检查更新”时，应用才通过 HTTPS 查询 [公开发布仓库](https://github.com/PaomianPomix/PPTassistant-releases) 的最新正式版本。检查不需要 GitHub 账号或令牌，不上传课件或板书。发现新版本后，可主动打开发布页下载。

应用不定时检查、不自动下载、不自动安装。预发行版本不进入正式更新通道。网络失败、超时、限流或没有正式发布时，设置页显示对应状态；关闭设置页会取消检查。更新源在代码中固定，不是 JSON 配置项。

## 开发入口

- [AppConfig.cs](../src/PPTAssistant.App/AppConfig.cs)：字段、默认值和配置读写。
- [AppHost.cs](../src/PPTAssistant.App/AppHost.cs)：设置回调、保存和会话联动。
- [SettingsWindow.cs](../src/PPTAssistant.App/SettingsWindow.cs)：设置分类和搜索。
- [AutoStartService.cs](../src/PPTAssistant.App/AutoStartService.cs)：Windows 启动项。
- [UpdateChecker.cs](../src/PPTAssistant.App/UpdateChecker.cs)：版本查询与比较。
- [AppReleaseInfo.cs](../src/PPTAssistant.App/AppReleaseInfo.cs)：公开更新源。
