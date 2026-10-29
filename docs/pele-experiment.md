# UNO Pele renderer experiment

Branch: `experiment/pele-renderer`, based on `feat/typedown-refresh` at `f9c8273`.

This branch lets Uno select Mermaid 11.16.1 or Pele 1.12.0 under Settings > Editor > Mermaid diagram
renderer. Mermaid remains the default. The shared editor bundle still asks for the `mermaid` global;
`Assets/Editor/pele-mermaid-adapter.js` implements the three calls it uses (`initialize`, `render`, `run`)
and lazily loads the selected engine. Changing the setting redraws the open document's diagrams and also
changes the engine used by exports and diagram copy. `Tools/sync-editor.sh` restores the adapter whenever
the Windows editor bundle is synced. Mermaid fenced blocks stay unchanged; documents need no conversion.

The adapter gives each SVG a unique ID prefix, disables diagram link clicks as the previous Mermaid
`securityLevel: strict` did, and puts light or dark colors directly on the SVG. Direct colors are needed
because the editor's XHTML picture export and diagram copy clone the SVG into an image before making a PNG.

The editor stylesheet gives preview SVGs `width: 100%`. Mermaid limits this with an intrinsic `max-width`,
but Pele previously used only `max-width: 100%`, stretching small diagrams to the whole editor width.
The adapter now caps Pele SVGs at their numeric `width` (or `viewBox` width), while allowing them to shrink
with a narrower editor. For `graph TD; A-->B` in an 800 px preview, the displayed size changed from
800×2000 to its natural 64×160; at 40 px preview width it becomes 40×100. This corrects preview scale,
not aspect ratio. PDF export was already close to Mermaid's size because its picture path uses the SVG's
own dimensions. The sizing check also runs in `Tools/rich-copy-check.js` alongside copy and XHTML checks.

## Checks performed

- The 58 Mermaid fences in the supplied `PELE-samples.md` rendered with Pele 1.12.0 (58/58).
- `node Tools/rich-copy-check.js`: ordinary flowchart, Event modeling, and User journey each copied a PNG;
  `RenderXhtml` returned three PNG pictures with no SVG left. Switching to Mermaid redrew the open diagram,
  and switching back redrew it with Pele. The Event modeling copy check failed on the old Mermaid bundle
  because the clipboard got Markdown only.
- Linux WebKitGTK 4.1 in Xvfb: both Event modeling examples and the User journey example produced SVG
  without `<foreignObject>` and converted to PNG (3/3).
- `/root/.dotnet/dotnet publish Typedown.Uno/Typedown.Uno.csproj -f net9.0-desktop -c Release -r linux-x64
  --self-contained -p:PublishSingleFile=false -o publish/Typedown-linux-x64-pele` succeeded with both engines.
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
