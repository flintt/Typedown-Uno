# 上架 Ubuntu 商店（调研，2026-09-25）

## 结论

Ubuntu 24.04 的 App Center 只列 snap（和 Ubuntu 官方仓库的 deb），不支持 Flathub。要让用户在"商店"里搜到，只有 Snap Store 一条路。
闭源、私有仓库、本地构建后上传，都没有障碍：上传的是编好的 .snap，`license: Proprietary` 是官方取值，免费应用不收费。

| 渠道 | App Center 可见 | 账号 | 审核 | 闭源 |
|---|---|---|---|---|
| Snap Store | 是 | Ubuntu One，接受 Developer Terms | 自动审核；新名字注册人工审核（约 2 个工作日） | 可以 |
| Flathub | 否 | GitHub，PR | 志愿者人工审核，无时限 | 可以，但 2026-05 起禁止 AI 生成/辅助的内容进 manifest，含 AI 代码的应用须披露并由审核者裁量，本项目走不通 |
| Launchpad PPA | 否 | Launchpad + GPG | 无 | 只收源码包且公开，闭源别扭 |

来源：https://ubuntu.com/desktop/docs/en/24.04/explanation/snap-and-deb-packages/ ，https://canonical.com/legal/developer-terms-and-conditions ，https://forum.snapcraft.io/t/snap-license-metadata/856 ，https://docs.flathub.org/docs/for-app-authors/requirements

## Snap 怎么打

- `base: core24` + `extensions: [gnome]`。扩展接入 `gnome-46-2404` 平台 snap，里面已有 GTK 3、GLib、**WebKitGTK 4.1**（含辅助进程的 layout），并自动加 desktop/x11/wayland/opengl/gsettings 等 plug。**不要自己 stage libwebkit2gtk-4.1**：会和平台 snap 的辅助进程版本错配，WebView 卡死（tabularis、keryx 都踩过）。
- 用 `dump` 插件把现成的 `dotnet publish` 产物打进去，snapcraft 不碰源码。
- plug：`home`（读写 $HOME 非隐藏文件，自动连接）、`removable-media`（外接盘，用户要手动 `snap connect`）、`network`、`network-status`（WebKit 经 portal 查代理，缺了报 NotAllowed）。不需要 `browser-support`。
- strict 下其它路径只能经文件选择器 portal，得到的是 `/run/user/<uid>/doc/...` 路径，重启失效：**最近文件、会话恢复、文件树对这类路径要能容忍**。
- 打开浏览器走 `xdg-open` shim → snapd userd，`desktop` plug 已由扩展提供；包里不要自带 xdg-open。
- classic 不要想：编辑器不在允许类别，"只是难以 confine"会被拒。
- 平台 snap 里同样只有 `.so.0`，没有 `-dev` 的未版本化链接：要么启动脚本里像 deb 一样造链接，要么把 `Typedown.Uno` 主程序集也纳入 `InstallLibraryResolver`（更干净，deb/AppImage/snap 一并解决）。

来源：https://ubuntu.com/docs/snapcraft/latest/reference/extensions/gnome-extension/ ，https://github.com/TabularisDB/tabularis/pull/712 ，https://snapcraft.io/docs/explanation/snap-development/xdg-desktop-portals/ ，https://snapcraft.io/docs/reference/administration/reviewing-classic-confinement-snaps/

### snap/snapcraft.yaml

```yaml
name: typedown
title: Typedown
base: core24
version: '1.1.2'
summary: Markdown editor with the Typedown/MarkText engine
description: |
  Typedown is a Markdown editor. The .NET runtime is bundled; GTK 3 and
  WebKitGTK 4.1 come from the gnome-46-2404 platform snap.
license: Proprietary
grade: stable
confinement: strict
icon: snap/gui/typedown.png
website: https://github.com/flintt/Typedown-Uno
contact: https://github.com/flintt/Typedown-Uno/issues

platforms:
  amd64:

parts:
  typedown:
    plugin: dump
    # dotnet publish Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -c Release -r linux-x64 \
    #   --self-contained -p:PublishSingleFile=false -o publish/Typedown-linux-x64
    source: publish/Typedown-linux-x64
    source-type: local
    organize:
      '*': opt/typedown/
    stage-packages:
      - libicu74
      - libunwind8
      - liblttng-ust1t64
    prime:
      - -opt/typedown/**/*.pdb
      - -opt/typedown/**/*.xml

  launcher:
    plugin: dump
    source: snap/local
    organize:
      typedown-launch: bin/typedown-launch

apps:
  typedown:
    command: bin/typedown-launch
    extensions: [gnome]
    common-id: uk.mingdan.typedown
    plugs:
      - home
      - removable-media
      - network
      - network-status
    environment:
      GDK_BACKEND: x11
      WEBKIT_DISABLE_DMABUF_RENDERER: "1"
      DOTNET_CLI_TELEMETRY_OPTOUT: "1"
      DOTNET_NOLOGO: "1"
```

### snap/local/typedown-launch

```sh
#!/bin/sh
plat="$SNAP/gnome-platform/usr/lib/x86_64-linux-gnu"
libdir="$SNAP_USER_COMMON/lib"
mkdir -p "$libdir"
for base in libwebkit2gtk-4.1 libjavascriptcoregtk-4.1 libgdk-3 libgtk-3 libsoup-3.0 libcairo \
            libpango-1.0 libpangocairo-1.0 libgdk_pixbuf-2.0 libatk-1.0 libgio-2.0 libglib-2.0 libgobject-2.0; do
  [ -e "$libdir/$base.so" ] && continue
  target=$(ls "$plat/$base.so".[0-9]* 2>/dev/null | head -n1)
  [ -n "$target" ] && ln -sf "$target" "$libdir/$base.so"
done
export LD_LIBRARY_PATH="$libdir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$SNAP/opt/typedown/Typedown.Uno" "$@"
```

### snap/gui/typedown.desktop

```ini
[Desktop Entry]
Type=Application
Name=Typedown
Comment=Markdown editor
Exec=typedown %f
Icon=${SNAP}/meta/gui/typedown.png
Terminal=false
Categories=Office;TextEditor;
MimeType=text/markdown;text/x-markdown;
StartupWMClass=Typedown.Uno
```

加 256×256 的 `snap/gui/typedown.png`。

## 已知坑

1. WebKit 的嵌套 bubblewrap 沙箱在 strict 里起不来；webkit2gtk-4.1 默认不开，Uno 6.7 也没开，暂时不会撞上。将来若默认开启，用 `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1`。
2. NVIDIA / 虚拟机下 WebKit 2.46+ 的 DMA-BUF 渲染器在 snap 里拿不到宿主 GBM 会直接 abort：`WEBKIT_DISABLE_DMABUF_RENDERER=1`（App.xaml.cs 已设，snap 里保留）。
3. .NET 自包含：`sched_setaffinity` 现已在 snapd 默认模板放行；ICU 用 `libicu74`；`PublishSingleFile=false` 就没有解包目录问题。
4. Wayland 会话：`x11` plug 连 Xwayland，照常运行；DPI 缩放可能要 `UNO_DISPLAY_SCALE_OVERRIDE`。
5. `%f` 传入的文件在 snap 里经 document portal 变成 doc 路径，"在文件夹中显示"、按目录开文件树会退化。
6. desktop 文件缺失或 Icon 重复、包外符号链接、自带 xdg-open，都会把上传推入人工审核；`StartupWMClass` 要和 X11 窗口类一致。
7. 自动更新：snapd 每天检查 4 次，运行中的应用会推迟；先发 edge/beta 自测再 stable。

## 步骤

1. Ubuntu One 账号，登录 snapcraft.io 接受 Developer Terms。
2. `snapcraft register typedown`，人工审核约 2 个工作日（2024-03 起所有新名字都审）。
3. 本机装 snapcraft + LXD：`snapcraft pack --use-lxd`；`snap install --dangerous ./typedown_*.snap` 本地验证 WebView、文件对话框、链接、Wayland 会话。
4. `review-tools` 的 `snap-review` 预跑，消掉所有 warning（任何 warning 都进人工审核）。
5. `snapcraft upload --release=edge typedown_*.snap`；自动审核几分钟。
6. 在 snapcraft.io/typedown/listing 填图标、截图（≤5）、描述、分类。
7. `removable-media` 自动连接要论坛申请，编辑器多半不给，文档里写 `snap connect`。
8. edge → beta → candidate → stable 逐级 `snapcraft release`。

人工审核队列不稳定：metadata 误报的案例等了 24 天到 2 个月。零 warning 时当天可上 edge。

来源：https://ubuntu.com/docs/snapcraft/stable/how-to/publishing/register-a-snap/ ，https://forum.snapcraft.io/t/manual-review-of-all-new-snap-name-registrations/39440 ，https://forum.snapcraft.io/t/manual-review-pending-24-days/50138

## 打包前要改的两处代码

- `Typedown.Uno` 主程序集纳入 `X11Window.InstallLibraryResolver`，去掉对未版本化 SONAME 链接的依赖。
- 最近文件、会话恢复、文件树容忍 `/run/user/*/doc/` 路径。
