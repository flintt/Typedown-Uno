#!/usr/bin/env python3
"""Pastes the clipboard into a new LibreOffice Writer document, as Ctrl+V would, and prints what arrived as JSON:
the text, whether any of it is bold, and the pictures (with their size). Used by reading-copy-check.sh to see a copy
the way a word processor takes it.

    lo-paste.py PORT        (an soffice listening with --accept="socket,host=localhost,port=PORT;urp;")
"""
import json, sys, time
import uno
from com.sun.star.beans import PropertyValue

port = int(sys.argv[1])
local = uno.getComponentContext()
resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
for attempt in range(60):
    try:
        ctx = resolver.resolve(f"uno:socket,host=localhost,port={port};urp;StarOffice.ComponentContext")
        break
    except Exception:
        time.sleep(1)
else:
    print(json.dumps({"error": "no LibreOffice on that port"}))
    sys.exit(2)
smgr = ctx.ServiceManager
desktop = smgr.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
doc = desktop.loadComponentFromURL("private:factory/swriter", "_blank", 0, ())
dispatcher = smgr.createInstanceWithContext("com.sun.star.frame.DispatchHelper", ctx)
dispatcher.executeDispatch(doc.getCurrentController().getFrame(), ".uno:Paste", "", 0, ())
time.sleep(2)
text = doc.getText().getString()
bold = False
paragraphs = doc.getText().createEnumeration()
while paragraphs.hasMoreElements():
    paragraph = paragraphs.nextElement()
    if not paragraph.supportsService("com.sun.star.text.Paragraph"):
        continue
    portions = paragraph.createEnumeration()
    while portions.hasMoreElements():
        portion = portions.nextElement()
        if portion.getString().strip() and portion.CharWeight >= 150:
            bold = True
graphics = doc.getGraphicObjects()
pictures = []
for i in range(graphics.getCount()):
    g = graphics.getByIndex(i)
    size = g.getSize()
    pictures.append({"width": size.Width, "height": size.Height})
doc.close(True)
print(json.dumps({"text": text, "bold": bold, "pictures": pictures}, ensure_ascii=False))
