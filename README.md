# P+ Tracklist Editor

Desktop app (WPF, .NET 10) for editing Project+ stage music: tracklists (`.tlst`) and songs (`.brstm`), including making looping BRSTMs from WAV/MP3.
Built for people who aren't modders: no BrawlCrate, no hex.

**Status:** steps 1–4 done: tracklist read/write, song library + builds (first-run import from your Project+ folder), Sync, and the audio core (WAV/MP3/BRSTM import, looping BRSTM encode, loop-aware preview stream). Step 5 (first usable UI) in progress: builds, stages, song editing with Undo, preview playback, song library, Sync.

## Build & test
```powershell
dotnet build
dotnet run --project src/App                                    # run the app (first run asks for your Project+ folder)
dotnet test                                                     # all tests
dotnet test --filter "FullyQualifiedName~EveryRealTracklist"    # one test
dotnet test --logger "console;verbosity=detailed"               # see test output (e.g. how many real files ran)
```
Real-file tests use the music repo and the P+ build on this machine (paths in `tests/Core.Tests/RealData.cs`, overridable with
`PPLUS_MUSIC_ROOT` / `PPLUS_BUILD_SOUND`; the BrawlCrate cross-check uses `PPLUS_BRAWLLIB`). They pass with a "skipped" note if the folders don't exist.

## Layout
```
src/Core/         no-UI library: file formats + logic (everything testable)
  Tlst/           TlstFile / TlstEntry: .tlst read/write
  Library/        AppLibrary (songs + builds, first-run import), Build, SongStore, ProjectPlusFolder, MusicFolder (import source),
                  StageEditSession (edit + save one stage),
                  SongFileNames (safe names), StageNames (pretty names), DiscSongs (Brawl disc song names)
  Sync/           BuildSync: plan + execute a sync to a Project+ folder (pf\sound only)
  Audio/          AudioImport (WAV direct, MP3/M4A/FLAC via Windows Media Foundation, BRSTM), BrstmFile (info, decode,
                  encode with loop, block-aligned like stock songs), AudioOps (resample, normalize, looping preview stream)
src/App/          WPF app (MVVM with CommunityToolkit.Mvvm). ViewModels/ (Main, StageEditor, SongRow, Library, Sync),
                  Views/ (windows), Services/ (Dialogs, PreviewPlayer via NAudio). Library lives in bin\...\Library when run from source.
tests/Core.Tests/ xUnit
```

## Format
See `TlstFile.cs` (header doc) and `pplus-wii/MODDING_NOTES.md`. Field names match BrawlLib's `TLSTEntry`.

## Credits
- Disc song names: BrawlManagers `SongIDMap` (MIT, © 2014 libertyernie).
- UI: CommunityToolkit.Mvvm (MIT, © .NET Foundation).
- Audio: VGAudio (MIT, © Alex Barney) for BRSTM/DSP-ADPCM, NAudio (MIT, © Mark Heath) for decoding, resampling and playback.
