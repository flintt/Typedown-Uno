# Typedown · Uno Platform 版

[Typedown](https://github.com/flintt/Typedown) 的跨平台移植：同一套 C# + XAML（Uno Platform，Skia 桌面端）在 **Linux / Windows / macOS** 上运行，编辑内核（React + Muya）与原版完全相同。

## 功能

- 所见即所得 Markdown 编辑（表格、公式、代码块、图表、任务列表、脚注……），源代码模式、专注/打字机/阅读模式
- 多标签：文件树单击预览、双击固定；Ctrl+Tab 切换；关闭时逐个询问保存；启动时恢复上次会话
- 侧边栏：文件树、大纲（点击跳转）、文件夹全文搜索（含文件名）
- 文件：打开/保存/另存为（系统对话框）、原子写入、外部修改检测、自动保存、最近文件、记住每个文件的光标和滚动位置
- 查找：Ctrl+F 页内查找（高亮、上一个/下一个）
- 主题：跟随系统 / 浅色 / 深色 / 纯黑；界面语言：中文（简体）/ English
- 导出 HTML；一键分享到 HedgeDoc（1.x，匿名或邮箱登录，可生成只读链接）
- 设置对话框（Ctrl+,）：字号、行高、编辑区宽度、列表缩进、表格对齐、拼写检查等，改动即时生效

## 运行

从 [Releases](../../releases) 下载对应平台的压缩包，解压后运行 `Typedown.Uno`（Windows 为 `Typedown.Uno.exe`）。可把 `.md` 文件路径作为参数传入。

| 平台 | 依赖 |
|---|---|
| Linux x64 | `libgtk-3-0`、`libwebkit2gtk-4.1-0`（Ubuntu 24.04 / Debian 12 及以上）；**用 `sudo ./install-linux.sh` 安装**，它会建好下面说的符号链接 |
| Windows 10/11 x64 | WebView2 运行时（Windows 11 自带） |
| macOS（Apple Silicon） | 无；首次运行需右键 → 打开 |

设置、会话和运行日志（`debug.log`）保存在 `~/.local/share/Typedown.Uno/`（Windows：`%LOCALAPPDATA%\Typedown.Uno\`）。

### Linux 安装说明（重要）

Uno 的 GTK 网页视图按**不带版本号**的库名（`libgdk-3.so`、`libsoup-3.0.so`、`libwebkit2gtk-4.1.so` 等）做 P/Invoke，
而这些符号链接只随 `-dev` 包安装。普通桌面上缺了它们，程序能打开、菜单能点，但**编辑区一直空白**
（网页视图初始化永远不会完成）。`install-linux.sh` 会检测并补上这些链接（不需要装 `-dev` 包）：

```
tar -xzf Typedown-linux-x64.tar.gz -C /tmp/typedown && sudo ./install-linux.sh /tmp/Typedown-linux-x64.tar.gz
typedown ~/notes/a.md
```

程序启动时也会自检，缺哪个库会写进 `debug.log` 并显示在状态栏。

## 快捷键

Ctrl+N 新建标签 · Ctrl+O 打开 · Ctrl+Shift+O 打开文件夹 · Ctrl+S 保存 · Ctrl+Shift+S 另存为 · Ctrl+W 关闭标签 · Ctrl+Tab / Ctrl+Shift+Tab 切换标签 · Ctrl+F 查找 · Ctrl+Shift+F 文件夹搜索 · Ctrl+Shift+B 侧边栏 · Ctrl+Shift+R 阅读模式 · Ctrl+/ 源代码模式 · Ctrl+, 设置。编辑快捷键（加粗、斜体、撤销……）由编辑器本身处理。

## 开发

```
export PATH=$HOME/.dotnet:$PATH          # .NET 9 SDK
dotnet build Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop
dotnet run --project Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -- some.md
# 无显示器：xvfb-run -a dotnet run ...   截图：import -window root shot.png
dotnet publish Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -c Release -r linux-x64 --self-contained -o out
```

结构见 `PORT_PLAN.md`。编辑器构建产物在 `Typedown.Uno/Assets/Editor/`（来自原仓库 `Dev/Typedown/Resources/Statics`，加上 `uno-bridge.js` 桥接脚本）；更新编辑器时把原仓库的构建产物复制过来并保留 `uno-bridge.js` 及 `index.html` 里对它的引用。

## 已知限制

- 图片插入/上传、PDF 导出、打印、拖放打开文件尚未实现（HTML 导出可用，可在浏览器里打印为 PDF）。
- 单窗口；多个文档以标签形式打开。
- Linux 上编辑器是一个原生 WebKit 窗口，因此无法与 XAML 控件重叠动画；对话框会覆盖在它上面。
