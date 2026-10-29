# UNO Pele renderer experiment

Branch: `experiment/pele-renderer`, based on `feat/typedown-refresh` at `f9c8273`.

This branch replaces the bundled Mermaid 11.16.1 renderer with Pele 1.12.0 **only in Uno**. The shared
editor bundle still asks for the `mermaid` global. `Assets/Editor/pele-mermaid-adapter.js` implements the
three calls it uses (`initialize`, `render`, `run`) and loads the local Pele script on the first diagram.
`Tools/sync-editor.sh` restores the adapter whenever the Windows editor bundle is synced. Mermaid fenced
blocks stay unchanged; documents do not need conversion.

The adapter gives each SVG a unique ID prefix, disables diagram link clicks as the previous Mermaid
`securityLevel: strict` did, and puts light or dark colors directly on the SVG. Direct colors are needed
because the editor's XHTML picture export and diagram copy clone the SVG into an image before making a PNG.

## Checks performed

- The 58 Mermaid fences in the supplied `PELE-samples.md` rendered with Pele 1.12.0 (58/58).
- `node Tools/rich-copy-check.js`: ordinary flowchart, Event modeling, and User journey each copied a PNG;
  `RenderXhtml` returned three PNG pictures with no SVG left. The new Event modeling copy check failed on
  the old Mermaid bundle because the clipboard got Markdown only.
- Linux WebKitGTK 4.1 in Xvfb: both Event modeling examples and the User journey example produced SVG
  without `<foreignObject>` and converted to PNG (3/3).
- `/root/.dotnet/dotnet build Typedown.Uno/Typedown.Uno.csproj -c Debug -f net9.0-desktop --no-restore`
  succeeded. The output includes the adapter, Pele script, and license; it does not include Mermaid.
- `bash packaging/check-app-assets.sh` passed.

## Still to assess before replacing Mermaid elsewhere

- Pele keeps Mermaid syntax but changes diagram layout. Compare screenshots for long labels, dense graphs,
  custom colors, and charts before accepting the new appearance.
- Mermaid theme directives and `themeVariables` do not set Pele's palette. The adapter uses an approximate
  light/dark palette, so Uno custom theme colors are not yet mapped.
- Math in diagram labels displays its TeX source until a Pele math resolver is supplied. External Mermaid
  plugins are not supported by Pele.
- The browser test used the current editor bundle and Linux WebKitGTK. Windows and macOS Uno runtime
  behavior has not been tested in this branch.

Pele and the bundled reset icon licenses are in `Assets/Editor/pele.LICENSE.txt`. No other repository or
branch is changed by this experiment.
