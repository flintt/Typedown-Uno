#!/bin/bash
# Builds the Linux distribution formats from a published folder:
#   tar.gz   portable, extract and run
#   .deb     Debian/Ubuntu package (declares the two runtime dependencies)
#   AppImage single executable (needs appimagetool; skipped when it is missing)
#
#   ./packaging/build-linux-packages.sh <published-dir> <version> [output-dir]
set -e

PUBLISH=${1:?usage: build-linux-packages.sh <published-dir> <version> [out]}
VERSION=${2:?version}
OUT=${3:-dist}
NAME=typedown
mkdir -p "$OUT"
PUBLISH=$(readlink -f "$PUBLISH")
OUT=$(readlink -f "$OUT")

echo "==> tar.gz"
tar -czf "$OUT/Typedown-linux-x64-$VERSION.tar.gz" -C "$PUBLISH" .

# ---- .deb -------------------------------------------------------------------------------------------------
echo "==> deb"
ROOT=$(mktemp -d)
install -d "$ROOT/DEBIAN" "$ROOT/opt/$NAME" "$ROOT/usr/bin" "$ROOT/usr/share/applications" "$ROOT/usr/share/icons/hicolor/256x256/apps"
cp -r "$PUBLISH"/. "$ROOT/opt/$NAME/"
chmod +x "$ROOT/opt/$NAME/Typedown.Uno"
[ -f "$PUBLISH/Assets/typedown.png" ] && cp "$PUBLISH/Assets/typedown.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/$NAME.png"

cat > "$ROOT/usr/bin/$NAME" <<'LAUNCH'
#!/bin/sh
# The GTK web view inside Uno needs X11 even in a Wayland session.
export GDK_BACKEND=x11
exec /opt/typedown/Typedown.Uno "$@"
LAUNCH
chmod +x "$ROOT/usr/bin/$NAME"

cat > "$ROOT/usr/share/applications/$NAME.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Typedown
Comment=Markdown editor
Exec=/usr/bin/$NAME %f
Icon=$NAME
Terminal=false
Categories=Office;TextEditor;
MimeType=text/markdown;text/x-markdown;
DESKTOP

cat > "$ROOT/DEBIAN/control" <<CONTROL
Package: $NAME
Version: $VERSION
Section: editors
Priority: optional
Architecture: amd64
Depends: libgtk-3-0 | libgtk-3-0t64, libwebkit2gtk-4.1-0, libx11-6
Maintainer: Typedown Community
Description: Typedown (Uno Platform edition)
 Cross-platform Markdown editor with the Typedown/MarkText editing engine.
 The .NET runtime is bundled; only GTK 3 and WebKitGTK 4.1 come from the system.
CONTROL

fakeroot dpkg-deb --build "$ROOT" "$OUT/typedown_${VERSION}_amd64.deb" >/dev/null
rm -rf "$ROOT"

# ---- AppImage ---------------------------------------------------------------------------------------------
if command -v appimagetool >/dev/null; then
  echo "==> AppImage"
  APPDIR=$(mktemp -d)/Typedown.AppDir
  install -d "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
  cp -r "$PUBLISH"/. "$APPDIR/usr/bin/"
  chmod +x "$APPDIR/usr/bin/Typedown.Uno"
  [ -f "$PUBLISH/Assets/typedown.png" ] && cp "$PUBLISH/Assets/typedown.png" "$APPDIR/$NAME.png" && cp "$PUBLISH/Assets/typedown.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$NAME.png"
  cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
HERE=$(dirname "$(readlink -f "$0")")
export GDK_BACKEND=x11
exec "$HERE/usr/bin/Typedown.Uno" "$@"
APPRUN
  chmod +x "$APPDIR/AppRun"
  cat > "$APPDIR/$NAME.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Typedown
Exec=Typedown.Uno %f
Icon=$NAME
Categories=Office;TextEditor;
Terminal=false
MimeType=text/markdown;text/x-markdown;
DESKTOP
  cp "$APPDIR/$NAME.desktop" "$APPDIR/usr/share/applications/"
  ARCH=x86_64 appimagetool "$APPDIR" "$OUT/Typedown-$VERSION-x86_64.AppImage" >/dev/null
  rm -rf "$(dirname "$APPDIR")"
else
  echo "==> AppImage skipped (appimagetool not installed)"
fi

ls -la "$OUT"
