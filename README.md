# Typedown · Uno Platform 版

[Typedown](https://github.com/flintt/Typedown) 的跨平台移植：同一套 C# + XAML（Uno Platform，Skia 桌面端）在 **Linux / Windows / macOS** 上运行，编辑内核（React + Muya）与原版完全相同。

## 功能

- 所见即所得 Markdown 编辑（表格、公式、代码块、图表、任务列表、脚注……），源代码模式、专注/打字机/阅读模式
- 多标签：文件树单击预览、双击固定；Ctrl+Tab 切换；关闭时逐个询问保存；启动时恢复上次会话
- 侧边栏：文件树、大纲（点击跳转）、文件夹全文搜索（含文件名）
- 文件：打开/保存/另存为（系统对话框）、原子写入、外部修改检测、自动保存、最近文件、记住每个文件的光标和滚动位置
- 查找：Ctrl+F 页内查找（高亮、上一个/下一个）
- 主题：跟随系统 / 浅色 / 深色 / 纯黑；界面语言：中文（简体）/ English
- 图片：插入本地图片、粘贴截图、拖入图片文件——都会按设置复制到文档旁的 `${filename}.assets` 并写成相对路径
- 拖放：把 .md 文件拖进窗口打开为标签，拖文件夹打开为工作区
- 文件树右键：新建文件/文件夹、重命名、删除、复制路径、在文件管理器中打开、刷新；侧栏宽度可拖拽
- 导出 HTML、打印/导出 PDF（在浏览器中打印）；一键分享到 HedgeDoc（1.x，匿名或邮箱登录，可生成只读链接）
- 设置对话框（Ctrl+,）：字号、行高、编辑区宽度、列表缩进、表格对齐、拼写检查等，改动即时生效

## 运行

从 [Releases](../../releases) 下载对应平台的包。`.NET 运行时已经打进包里`，不需要另外安装。

| 平台 | 发布格式 | 运行时依赖（系统提供） |
|---|---|---|
| Linux x64 | `.deb`、`.AppImage`、`.tar.gz`（便携） | `libgtk-3-0`、`libwebkit2gtk-4.1-0`、`libx11-6`；Ubuntu 22.04+/Debian 12+ 装上即可（`.deb` 会自动拉） |
| Windows 10/11 x64 | `.zip`（便携，解压即用） | WebView2 运行时（Win11 自带，Win10 多数已随 Edge 安装） |
| macOS（Apple Silicon） | `.tar.gz` | 无；首次运行右键 → 打开（未签名） |

Linux 安装：`sudo dpkg -i typedown_*.deb` 或给 AppImage 加执行权限直接运行；便携版解压后 `./Typedown.Uno`。

设置、会话和运行日志（`debug.log`）保存在 `~/.local/share/Typedown.Uno/`（Windows：`%LOCALAPPDATA%\Typedown.Uno\`）。

### Linux 说明

Uno 的 GTK 网页视图按**不带版本号**的库名（`libgdk-3.so`、`libsoup-3.0.so`、`libwebkit2gtk-4.1.so`）做 P/Invoke，
而这些符号链接只随 `-dev` 包安装；缺了它们，程序能开、菜单能点，但**编辑区一直空白**。
本程序启动时会装一个库名解析器，自动映射到带版本号的 `.so.0`，所以**不需要装 `-dev` 包、也不用建符号链接**。
（`install-linux.sh` 仍然保留，用于从 tar.gz 做系统级安装。）

Wayland 桌面下 GTK 网页视图需要 X11，启动脚本已经设好 `GDK_BACKEND=x11`。
运行日志在 `~/.local/share/Typedown.Uno/debug.log`，启动时会记录环境和缺失的库。

## 快捷键

全部快捷键都可以在 设置 → 快捷键 里改（点输入框按新组合，Esc 清除，↺ 恢复默认）。默认值：
Ctrl+N 新建标签 · Ctrl+P 打印/导出 PDF · Ctrl+O 打开 · Ctrl+Shift+O 打开文件夹 · Ctrl+S 保存 · Ctrl+Shift+S 另存为 · Ctrl+W 关闭标签 · Ctrl+Tab / Ctrl+Shift+Tab 切换标签 · Ctrl+F 查找 · Ctrl+Shift+F 文件夹搜索 · Ctrl+Shift+B 侧边栏 · Ctrl+Shift+R 阅读模式 · Ctrl+/ 源代码模式 · Ctrl+, 设置。编辑快捷键（加粗、斜体、撤销……）由编辑器本身处理。

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

- **文件对话框用的是程序内置的选择器**（Linux）。Uno 的系统选择器依赖 XDG desktop portal，很多桌面上装不全，结果是对话框根本不出现，所以这里一律用自绘的选择器；Windows / macOS 仍用系统原生对话框。
- PDF 走"在浏览器中打印"，没有直接生成 PDF（WebKitGTK 的打印 API 没有通过 Uno 暴露出来）。
- 图床上传未实现（按需求暂不做）。
- 单窗口；多个文档以标签形式打开。
- Linux 上编辑器是一个原生 WebKit 窗口，因此无法与 XAML 控件重叠动画；对话框会覆盖在它上面。
