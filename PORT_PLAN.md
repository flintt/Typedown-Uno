# Typedown → Uno Platform 移植计划

目标：把 [flintt/Typedown](https://github.com/flintt/Typedown)（WinUI 2 / XAML Islands / WebView2，仅 Windows）移植到 Uno Platform，
在 **Linux（X11/GTK）、Windows、macOS** 上运行同一套 C# + XAML 代码。

## 现状（原仓库）

| 层 | 技术 | 代码量 | 移植策略 |
|---|---|---|---|
| 编辑器 | React + Muya（MarkText 内核），`Dev/Typedown.Editor` | ~23.5k 行 TS/JS | **原样复用**。构建产物（`Statics/`）直接放进 Uno 应用的 `Assets/Editor/` |
| Typedown.Core | UWP 类库：ViewModel、页面、控件、对话框、设置、资源 | ~15k 行 C# + ~6.7k 行 XAML | ViewModel 逻辑搬过来并去掉 UWP/PInvoke 依赖；XAML 逐页移植（WinUI 2 → WinUI 3 命名空间，`muxc:` → 内置） |
| Typedown 宿主 | .NET Core 3.1 Win32 窗口（私有 Typedown.XamlUI）、WebView2、OLE 拖放、DWM | ~2.8k 行 | **不移植**。窗口/标题栏/拖放/剪贴板/文件对话框全部改用 Uno 提供的跨平台 API |

## 关键技术事实（已验证）

- Uno 6.7，`net9.0-desktop`（Skia），Fluent 主题。脚手架在本机 Linux 上编译通过，Xvfb 下可运行。
- **WebView2 on Linux**：Uno 用 WebKit2GTK（4.0/4.1，本机 4.1）实现，包 `Uno.WinUI.WebView`（UnoFeature `WebView`）。
  - 支持：`ExecuteScriptAsync`、`NavigateToString`、`SetVirtualHostNameToFolderMapping`（映射到 `file://<app>/<folder>`）、`WebMessageReceived`。
  - JS→宿主：Linux/macOS 走 `webkit.messageHandlers.unoWebView.postMessage(string)`，Windows 走原生 `chrome.webview.postMessage`。
  - 宿主→JS：没有跨平台的 `PostWebMessage`，统一用 `ExecuteScriptAsync` 调 `window.__unoDeliver(json)`。
  - 编辑器只认 `window.chrome.webview`，所以在 `Assets/Editor/uno-bridge.js` 里做一个垫片，把两边接起来（Windows 上垫片直接透传给原生对象）。
  - 不支持：`WebResourceRequested`（Linux）。Wayland 需要 `GDK_BACKEND=x11`。
- 消息协议与原版一致：`{type:"invoke",id,name,args}` / `{type:"diffmsg",name,args,diff,start,end}` / 回复 `{name:id,args:{code,data}}`。

## 里程碑

1. **M1 编辑器跑起来**（✅ 2026-09-22）：WebView2 加载 `Assets/Editor/index.html`，垫片桥接消息，宿主应答 `GetSettings / GetCurrentTheme / ContentLoaded / GetStringResources`，能打字并收到 `MarkdownChange`。
2. **M2 文件**（✅ 除多窗口）：打开/保存/另存为（Uno `FileOpenPicker`/`FileSavePicker`，Linux 走 GTK 对话框）、外部修改监视、原子写入、光标/滚动记忆、最近文件。
3. **M3 壳**（✅ 菜单/标签/侧栏/查找）：菜单栏（`MenuBar`）、标题、状态栏、多标签（`TabView`）、侧栏（文件树 `TreeView`、大纲、文件夹搜索）。
4. **M4 设置**（✅ 设置对话框、主题、zh-Hans/en）：设置页（通用/外观/编辑器/导出/快捷键）、主题（浅色/深色/纯黑）、本地化（zh-Hans/zh-Hant/en 起步）。
5. **M5 导出与分享**（✅ HTML、HedgeDoc；PDF/打印未做）：HTML/PDF（WebKit 打印或 `ExecuteScript` 生成 HTML 后交给系统）、分享到 HedgeDoc。
6. **M6 打包**（✅ GitHub Actions：linux-x64 / win-x64 / osx-arm64 自包含包，tag 发布 Release）：Linux AppImage/.deb（`dotnet publish` + `uno-publish`），Windows/macOS 自包含包；CI（GitHub Actions ubuntu + windows + macos）。

## 不做 / 降级

- Mica、云母、DWM 圆角、自绘标题栏：交给各平台原生窗口。
- 原生 OLE 拖放：改用 Uno 的 `AllowDrop` / `DragOver` / `Drop`（各平台由 Uno 实现）。
- 图床上传（PowerShell/PicGo）：暂不做。
- Win7/8：不支持。

## 目录约定

```
Typedown.Uno/            单项目（Uno.Sdk），net9.0-desktop
  Assets/Editor/         编辑器构建产物（来自原仓库 Dev/Typedown/Resources/Statics）+ uno-bridge.js
  Services/              EditorTransport（消息协议）、文件、设置等
  ViewModels/            从 Typedown.Core 迁移
  Views/                 XAML 页面/控件
PORT_PLAN.md             本文件
```

## 本机开发（Linux）

```
export PATH=$HOME/.dotnet:$PATH
dotnet build Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop
xvfb-run -a dotnet run --project Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop   # 无显示器时
```
依赖：`libgtk-3-0 libwebkit2gtk-4.1-0`（Ubuntu 24.04）。

## 里程碑之后的待办

- 图片：插入本地图片（复制到文档目录）、粘贴图片、拖放文件到窗口打开
- PDF 导出 / 打印（WebKitGTK 有打印 API，Uno 未暴露；可先导出 HTML 交给浏览器）
- 多窗口；侧边栏宽度拖拽；文件树右键菜单（新建/重命名/删除）
- Windows/macOS 上的实际运行验证（本机只验证了 Linux）
- 键盘焦点：在网页编辑器内按快捷键由 `uno-bridge.js` 转发（Ctrl+S/O/N/W/Tab/,//，Ctrl+Shift+S/O/F/B/R），页内查找栏也在网页里
