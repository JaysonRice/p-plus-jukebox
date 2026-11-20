# P+ Tracklist Editor

Desktop app (WPF, .NET 10) for editing Project+ stage music: tracklists (`.tlst`) and songs (`.brstm`), including making looping BRSTMs from WAV/MP3.
Built for people who aren't modders: no BrawlCrate, no hex.

**Status:** steps 1–2 of 7 done: tracklist read/write and the music library (stages, adding songs, problem checks). No UI yet.

## Build & test
```powershell
dotnet build
dotnet test                                                     # all tests
dotnet test --filter "FullyQualifiedName~EveryRealTracklist"    # one test
dotnet test --logger "console;verbosity=detailed"               # see test output (e.g. how many real files ran)
```
Real-file tests use the music repo and the P+ build on this machine (paths in `tests/Core.Tests/RealData.cs`, overridable with
`PPLUS_MUSIC_ROOT` / `PPLUS_BUILD_SOUND`). They pass with a "skipped" note if the folders don't exist.

## Layout
```
src/Core/         no-UI library: file formats + logic (everything testable)
  Tlst/           TlstFile / TlstEntry: .tlst read/write
  Library/        MusicFolder, MusicLibrary (music over stock build), StageEditSession (edit + save one stage),
                  SongFileNames (safe names), StageNames (pretty names), DiscSongs (Brawl disc song names)
tests/Core.Tests/ xUnit
```

## Format
See `TlstFile.cs` (header doc) and `pplus-wii/MODDING_NOTES.md`. Field names match BrawlLib's `TLSTEntry`.

## Credits
- Disc song names: BrawlManagers `SongIDMap` (MIT, © 2014 libertyernie).
- Planned audio libraries: VGAudio (MIT), NAudio (MIT).
