# P+ Jukebox

A Windows app for managing the **music and stage lists** of your [Project+](https://projectplusgame.com) setup. No BrawlCrate, no hex editing, no renaming files by hand.

Before this, adding a song meant downloading a BRSTM, naming it exactly right, dropping it in `strm/`, opening the stage's `.tlst` in BrawlCrate and adding the entry. P+ Jukebox does all of that for you.

## What it does

- **Song Library:** keep all your songs in one place, see which builds use them and how much space they take. Add songs from a folder, or from your Project+ folder.
- **Stage Music:** pick a stage (on a picture of the stage select screen) and choose which songs play there, how often, and how loud. Stages with alternate versions (hold R, L or Z) show every version.
- **Stage Lists:** rename the stage list presets, choose starters and counterpicks, and turn stage hazards on or off.
- **Loop Maker:** turn a WAV, MP3, M4A or FLAC file into a looping BRSTM, and set the loop points.
- **Builds:** keep several setups (for example "Casual" and "Tournament"), each with its own songs and stage lists. Every build starts from an untouched copy of Project+.
- **Sync:** one button writes your build to your SD card (or any Project+ folder).

## What it doesn't do

- **No custom characters or skins.** Character and costume files (`.pac`) are not touched.
- No new stages, and no stage select pictures. It edits the music and lists of the stages already in your Project+.
- No choosing which alternate version of a stage loads by default.
- It does not include Project+ or any game files. You need your own copy.
- It only writes music, stage list presets and preset names (see "What Sync writes").

## What you need

- Windows 10 or 11 (64-bit). Nothing else to install.
- Your own Project+ folder (on your SD card, or a copy on your PC).

## Install and first run

1. Download the zip from the [Releases page](../../releases) and **unzip it** to a folder you'll keep, such as `Documents\P+ Jukebox`. Don't run it from inside the zip.
2. Open `PPlusJukebox.exe`. It's the only program in the folder. The first start takes a few seconds longer than usual.
3. Windows may say **"Windows protected your PC"**, because the app isn't signed. Click **More info**, then **Run anyway**.
4. The first time, the app asks for your Project+ folder. It copies your current songs and stage lists into its library, so you start with everything you already have.

## Syncing to your SD card

1. Make your changes in the app, and click **Save** on the stage you edited.
2. Open **Sync**, pick your Project+ folder (the SD card's `Project+` folder, or any copy), and review what it will do.
3. Click Sync. It copies new and changed songs and the stage list presets. Put the card in your Wii as usual.

### What Sync writes

- Songs in `pf\sound`.
- Stage list presets: `pf\stage\switch\SwitchNN.rss`.
- The preset names, only if you renamed one: `pf\menu2\sc_selcharacter2.pac` and `pf\menu2\mu_menumain.pac`.

It never deletes a song from the card that isn't in the app's library.

### Backups

Everything the app replaces is kept in the `Library\backups` folder next to the app. Menu files keep the very first original and the last version before each sync. Stage list presets keep every earlier version, with the date in the file name.

## Updating to a new version

Your library lives in the **`Library` folder next to the app**. To update, unzip the new version and **copy your old `Library` folder into the new app folder** (or unzip the new version over the old one). If you skip this, the new copy starts empty, and your songs are still safe in the old folder.

If you update Project+ itself, use **Update the app from this Project+** in the Sync window. Your songs in your builds are kept.

## Building from source

```powershell
dotnet build
dotnet run --project src/App
dotnet test
powershell -File tools\release\publish.ps1     # builds the release zip in dist\
```

Needs the .NET 10 SDK. Some tests read real Project+ and music folders from the author's PC; they skip themselves when the folders don't exist (override with `PPLUS_MUSIC_ROOT` and `PPLUS_BUILD_SOUND`).

```
src/Core/   no-UI library: file formats and logic (tracklists, stage lists, menu files, audio, library, sync)
src/App/    WPF app (MVVM, CommunityToolkit.Mvvm)
tests/      xUnit tests
tools/      theme generator, release script
```

## Credits

- Disc song names: BrawlManagers `SongIDMap` (MIT, © 2014 libertyernie).
- File formats: thanks to the [BrawlCrate / BrawlLib](https://github.com/soopercool101/BrawlCrate) project and the Brawl and Project+ modding community, whose tools and documentation made these formats understandable.
- UI: CommunityToolkit.Mvvm (MIT, © .NET Foundation).
- Audio: VGAudio (MIT, © Alex Barney) for BRSTM/DSP-ADPCM; NAudio (MIT, © Mark Heath) for decoding, resampling and playback.

Not affiliated with the Project+ team or Nintendo. Licensed under the [MIT License](LICENSE).
