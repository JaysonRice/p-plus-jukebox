# Theme tools

The app's colors ("Lagoon") are generated, not hand-edited.

- `gen.js` derives each palette (light and dark) from three hues and pushes lightness until the contrast targets hold
  (4.5:1 for text, 3:1 for interface shapes). `node gen.js` prints the colors and their contrast checks.
- `emit.js` writes `src/App/Themes/Lagoon.Light.xaml` and `Lagoon.Dark.xaml` from the Lagoon palette: `node emit.js`.
- `AccentBrushTable.cs.script` is a .NET file-based app (`dotnet run AccentBrushTable.cs.script -- table.txt` after renaming it
  to `.cs`) that lists the Fluent theme brushes built from the Windows accent color; paste its output into
  `src/App/Services/AccentBrushTable.cs`. Needed again only if the .NET version changes.

The app uses two colors: Lagoon green (choosing and confirming) and cobalt (listening and noticing: Play buttons, the timeline, unsaved changes), set as `SECOND` in `emit.js`. The loop end flag keeps a warm red. To try another palette, change which one `emit.js` picks (`p.id === 'lagoon'`) or `SECOND`, then run it and rebuild.
