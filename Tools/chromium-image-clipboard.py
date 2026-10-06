#!/usr/bin/env python3
"""Owns the clipboard the way Chromium's "Copy image" does: the picture as image/png and an <img> as text/html, and no
plain text. Stays until killed (an X clipboard is served by its owner). Used by image-paste-check.sh.

    chromium-image-clipboard.py PICTURE.png
"""
import sys, gi
gi.require_version('Gtk', '3.0')
from gi.repository import Gtk, Gdk

png = open(sys.argv[1], 'rb').read()
html = ('<meta http-equiv="content-type" content="text/html; charset=utf-8"><img src="file://%s">' % sys.argv[1]).encode()
clipboard = Gdk.Atom.intern('CLIPBOARD', False)

owner = Gtk.Invisible()
def serve(widget, selection, info, time):
    selection.set(selection.get_target(), 8, png if info == 1 else html)
owner.connect('selection-get', serve)
owner.connect('selection-clear-event', lambda *a: Gtk.main_quit())
Gtk.selection_add_target(owner, clipboard, Gdk.Atom.intern('image/png', False), 1)
Gtk.selection_add_target(owner, clipboard, Gdk.Atom.intern('text/html', False), 2)
if not Gtk.selection_owner_set(owner, clipboard, Gdk.CURRENT_TIME):
    sys.exit('could not own the clipboard')
print('owning the clipboard', flush=True)
Gtk.main()
