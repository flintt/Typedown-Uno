#!/bin/bash
# System-wide install of the Typedown (Uno) Linux build. Run as root:
#   sudo ./install-linux.sh [Typedown-linux-x64.tar.gz]
# Installs to /opt/typedown-uno, adds /usr/local/bin/typedown and a desktop entry, and creates the unversioned
# library symlinks Uno's GTK web view needs (see below).
set -e
mkdir -p /opt/typedown-uno
tar -xzf "${1:-/tmp/Typedown-linux-x64.tar.gz}" -C /opt/typedown-uno
chmod +x /opt/typedown-uno/Typedown.Uno

# Uno's GTK web view P/Invokes unversioned library names (libgdk-3.so, libsoup-3.0.so, libwebkit2gtk-4.1.so…).
# Those symlinks ship only in the -dev packages, so on a normal desktop the native web view silently fails to
# finish initializing (menus work, editor stays blank). Create just the links instead of pulling in the headers.
libdir=$(dirname "$(ldconfig -p | awk '/libgtk-3\.so\.0/ {print $NF; exit}')")
for base in libwebkit2gtk-4.1 libjavascriptcoregtk-4.1 libgdk-3 libgtk-3 libsoup-3.0 libcairo libpango-1.0 libpangocairo-1.0 libgdk_pixbuf-2.0; do
  if [ ! -e "$libdir/$base.so" ]; then
    target=$(ls "$libdir/$base.so."* 2>/dev/null | grep -E "\.so\.[0-9]+$" | head -1)
    [ -n "$target" ] && ln -sf "$(basename "$target")" "$libdir/$base.so" && echo "linked $base.so -> $(basename "$target")"
  fi
done
ldconfig

cat > /usr/local/bin/typedown <<'EOF'
#!/bin/sh
# Typedown (Uno Platform build); the WebKitGTK view inside Uno needs X11 even on Wayland sessions
export GDK_BACKEND=x11
exec /opt/typedown-uno/Typedown.Uno "$@"
EOF
chmod +x /usr/local/bin/typedown

icon=$(ls /opt/typedown-uno/Assets/Icons/*.png 2>/dev/null | sort | tail -1)
cat > /usr/share/applications/typedown-uno.desktop <<EOF
[Desktop Entry]
Type=Application
Name=Typedown
Comment=Markdown editor
Exec=/usr/local/bin/typedown %f
Icon=${icon:-accessories-text-editor}
Terminal=false
Categories=Office;TextEditor;
MimeType=text/markdown;text/x-markdown;
EOF
update-desktop-database /usr/share/applications 2>/dev/null || true

echo "installed: $(du -sh /opt/typedown-uno | cut -f1) in /opt/typedown-uno"
echo "run it with: typedown [file.md]"
