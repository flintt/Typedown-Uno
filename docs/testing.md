# 测试

Uno 版的编辑器页面、自动化接口和图片上传代码大多来自 Windows 原生版（`Tools/sync-*.sh` 同步过来），那部分的测试在 Windows 版仓库里，见那边的 `docs/testing.md`。这里测的是 Uno 宿主自己的部分：文件读写、窗口、键盘焦点、拖放、主题、设置，以及它和页面、自动化接口的衔接。

| 层 | 在哪里 | 测什么 | CI |
| --- | --- | --- | --- |
| 可靠性测试 | `Tests/Typedown.Uno.ReliabilityTests` | 原子写入与恢复副本、编码和换行往返、会话快照、Linux 凭据存储、备份路径兼容 | 每次构建 |
| 生成文件检查 | `Tools/Translations/generate.py --check`、`packaging/check-app-assets.sh` | 翻译表与生成的 `Locales/*.cs` 一致、应用图标等资源齐全 | 每次构建 |
| 编辑器文字检查 | `Tools/check-editor-strings.py` | 编辑器页面请求的界面文字宿主都提供了 | 否 |
| 桌面检查 | `Tools/*-check.sh` | 真实窗口里的按键、点击、拖放和截图：下面逐个列出 | 否，本地跑 |
| 自动化检查 | Windows 版仓库的 `Tools/AutomationE2E/linux` | 在真实桌面上用 `typedownctl`、MCP 和 Python 客户端读写；Linux 专有的文件名、链接、大文档；与 Windows 一致的等价场景 | 否 |

## 运行

```bash
dotnet run --project Tests/Typedown.Uno.ReliabilityTests/Typedown.Uno.ReliabilityTests.csproj -c Release
python3 Tools/Translations/generate.py --check
```

桌面检查先构建（`dotnet build Typedown.Uno.sln`，读文字的检查还要 `Typedown.Cli`），再直接运行脚本，例如 `bash Tools/drop-check.sh`。

- 每个脚本在 Xvfb 加 xfwm4 窗口管理器里启动程序，用一份临时的独立配置（`HOME`、`XDG_*` 都指向临时目录），不碰本机的 Typedown。需要 `Xvfb`、`xfwm4`、`xdotool`，部分脚本还要 `xwininfo`、ImageMagick 或带 GTK 3 的 python3，脚本开头写明。
- 一定要带窗口管理器：没有它，焦点、光标和窗口激活的问题看不出来。
- 默认运行 Debug 构建。要换程序位置或 Xvfb 显示号，多数脚本用第 1、2 个参数；`image-upload-check.sh` 用环境变量 `APP`、`DISPLAY_NUMBER`。
- 结果看输出里的 `PASS` / `FAIL` 行；检查多项的脚本最后还有一行 `OVERALL`。失败时附带程序日志的最后几行，有的还保留截图目录。

## 桌面检查

每个脚本开头的注释说明它防的是哪一个问题、怎样算通过。

<!-- BEGIN generated: checks -->
- `drop-check.sh`: Files dragged in from a file manager (a GTK drag source here, as Thunar or Nautilus are): two images dropped on the editor go in at the caret (copied next to the document, as Settings > Images says), a document dropped on the tab strip opens, and typing still reaches the editor afterwards (the drop layer over the editor takes no keys).
- `focus-after-dialog-check.sh`: After the settings dialog is closed with Escape, the keyboard is the editor's again: a letter typed without a click reaches the document, and Ctrl+, opens the settings again.
- `image-paste-check.sh`: A picture copied in Chromium ("Copy image": the picture as image/png and an <img> as text/html, no plain text) and pasted with Ctrl+V goes into the document: saved next to it, as Settings > Images says, and linked.
- `image-upload-check.sh`: File > Upload local images on Linux, with real keys: each local picture goes up once (a command of the person's own here; the S3 path is the Windows edition's code, tested there against a real bucket), every use of it changes to its address, a web image, a missing file and code stay; an undo and a second run take every address from the upload history without calling the command again.
- `reading-copy-check.sh`: Reading mode, with real keys and the page's own context menu: Ctrl+Z after an edit changes nothing (the history's undo replaced the text there), Ctrl+A selects the document, Copy puts the Markdown on the clipboard and Copy as plain text the text without it.
- `reveal-change-check.sh`: reveal: "change" scrolls a change made off screen into view; "document" leaves the page where it was.
- `settings-revision-check.sh`: settingsRevision moves with the exposed settings only.
- `theme-switch-check.sh`: The theme changed while the app runs must look the way it does after a restart.
- `windows-check.sh`: Two windows and dialogs, as the reader meets them.
<!-- END generated: checks -->

## 保持本文与脚本一致

上面的列表由 `Tools/testing-index.py` 从脚本开头的注释生成，CI 的“Check generated assets”一步检查它：

```bash
python3 Tools/testing-index.py           # 文档落后于脚本时失败，并给出差异
python3 Tools/testing-index.py --write   # 更新文档
```

新加检查脚本时命名为 `Tools/<名称>-check.sh`，第一段注释写清它检查什么。
