using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Sync;
using PPlusTracklistEditor.Core.Tlst;
using Xunit.Abstractions;
using static PPlusTracklistEditor.Core.Tests.TempSetup;

namespace PPlusTracklistEditor.Core.Tests;

public class SyncTests(ITestOutputHelper output)
{
    [Fact]
    public void FreshImport_SyncsBackWithNothingToDo()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();

        SyncPlan plan = BuildSync.Plan(lib.DefaultBuild, t.ProjectPlus);

        Assert.False(plan.HasChanges);
        Assert.Equal(2, plan.TracklistsUnchanged);
        Assert.Contains("Nothing to do", plan.Summary());
    }

    [Fact]
    public void TempleOfTimeExample_RemovedSongsLeaveTheCard_ButStayInTheLibrary()
    {
        using var t = new TempSetup();
        for (int i = 1; i <= 5; i++) t.SdSong($"Zelda/ToT {i}");
        t.SdTracklist("Temple_of_Time", Enumerable.Range(1, 5).Select(i => Song((uint)(0xF000 + i), $"Zelda/ToT {i}", $"ToT {i}")).ToArray());
        var lib = t.CreateLibrary();
        Build tn = lib.NewBuild("Tennessee");

        // User removes 3 songs and adds 3 new ones.
        var session = tn.OpenStage("Temple_of_Time");
        foreach (TlstEntry e in session.Tracklist.Entries.Where(e => e.Title is "ToT 2" or "ToT 3" or "ToT 4").ToList()) session.Remove(e);
        for (int i = 1; i <= 3; i++) session.AddSong(t.DownloadedBrstm($"new{i}.brstm", (byte)(20 + i)), $"New {i}", "Zelda");
        session.Save();

        SyncPlan plan = BuildSync.Plan(tn, t.ProjectPlus);
        output.WriteLine(plan.Summary());

        Assert.Equal(["Temple_of_Time"], plan.TracklistsToWrite);
        Assert.Equal(["Zelda/New 1", "Zelda/New 2", "Zelda/New 3"], plan.SongsToCopy);
        Assert.Equal(["Zelda/ToT 2", "Zelda/ToT 3", "Zelda/ToT 4"], plan.SongsToDelete);
        // Space used by the songs the user added (Project+'s ToT 1 and 5 aren't counted).
        Assert.Equal(["Zelda/New 1", "Zelda/New 2", "Zelda/New 3"], plan.CustomSongs);
        Assert.Equal(plan.CustomSongs.Sum(s => new FileInfo(lib.Songs.FileFor(s)).Length), plan.CustomBytes);
        Assert.Contains("Songs you added to \"Tennessee\": 3 songs", plan.Summary());

        BuildSync.Execute(plan);
        Assert.Contains("Songs you added", BuildSync.Plan(tn, t.ProjectPlus).Summary());   // shown when up to date too

        foreach (string kept in new[] { "Zelda/ToT 1", "Zelda/ToT 5", "Zelda/New 1", "Zelda/New 2", "Zelda/New 3" }) Assert.True(t.SdHasSong(kept), kept);
        foreach (string gone in new[] { "Zelda/ToT 2", "Zelda/ToT 3", "Zelda/ToT 4" })
        {
            Assert.False(t.SdHasSong(gone), gone);
            Assert.True(lib.Songs.Has(gone), $"{gone} should still be in the library");
        }
        Assert.Equal(File.ReadAllBytes(tn.TracklistPath("Temple_of_Time")), File.ReadAllBytes(Path.Combine(t.Sound, "tracklist", "Temple_of_Time.tlst")));
        Assert.False(BuildSync.Plan(tn, t.ProjectPlus).HasChanges); // running Sync again does nothing
        Assert.Equal(t.ProjectPlusRoot, lib.GetBuild(tn.Id).Info.LastSyncFolder);
    }

    [Fact]
    public void Sync_KeepsHurryUpFiles_DiscReferences_AndTracklistsTheBuildDoesntHave()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        // The card has a stage the app's build doesn't know (e.g. from a newer P+), with its own song.
        t.SdSong("Pokemon/Radiant Stadium");
        t.SdTracklist("Radiant_Stadium", Song(0xF000, "Pokemon/Radiant Stadium", "Radiant"));
        t.SdSong("Strays/Nobody Uses Me");
        File.WriteAllText(Path.Combine(t.Sound, "strm", "readme.txt"), "not a song");

        SyncPlan plan = BuildSync.Plan(lib.DefaultBuild, t.ProjectPlus);
        BuildSync.Execute(plan);

        Assert.Equal(["Radiant_Stadium"], plan.TracklistsLeftAlone);
        Assert.Empty(plan.SongsToDelete);                          // not in the library (e.g. added by a P+ update): never deleted
        Assert.True(t.SdHasSong("Super Mario/Overworld_b"));      // hurry-up version of a used song survives
        Assert.True(t.SdHasSong("Pokemon/Radiant Stadium"));      // used by a tracklist the build doesn't have
        Assert.True(File.Exists(Path.Combine(t.Sound, "tracklist", "Radiant_Stadium.tlst")));
        Assert.True(File.Exists(Path.Combine(t.Sound, "strm", "readme.txt")));  // only .brstm files are ever deleted
        Assert.True(t.SdHasSong("Strays/Nobody Uses Me"));
        Assert.Empty(plan.MissingSongs);                           // "Z01" is a disc song
    }

    [Fact]
    public void Sync_OnlyEverTouchesPfSound()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        Build tn = lib.NewBuild("Tennessee");
        var s = tn.OpenStage("Mushroom_Kingdom");
        s.Tracklist.Entries.Clear();
        s.Save();
        var before = SnapshotOutsideSound(t);

        SyncPlan plan = BuildSync.Plan(tn, t.ProjectPlus);
        Assert.Contains("Remove 1 song ", plan.Summary());           // Overworld + its "_b" = 1 song
        BuildSync.Execute(plan);

        Assert.Equal(before, SnapshotOutsideSound(t));
        Assert.False(t.SdHasSong("Super Mario/Overworld"));
        Assert.False(t.SdHasSong("Super Mario/Overworld_b"));      // its main song is unused now, so it goes too
    }

    [Fact]
    public void Sync_CopiesChangedSongsAndFillsInAFreshCard()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        // A different card: same Project+ layout but empty music folder.
        string other = Path.Combine(t.Root, "OtherCard", "Project+");
        Directory.CreateDirectory(Path.Combine(other, "pf", "sound", "tracklist"));

        SyncPlan plan = BuildSync.Plan(lib.DefaultBuild, ProjectPlusFolder.Resolve(other));
        BuildSync.Execute(plan);

        Assert.Equal(2, plan.TracklistsToWrite.Count);
        Assert.Equal(4, plan.SongsToCopy.Count);                    // 3 songs + the "_b"
        Assert.Contains("Copy 3 songs", plan.Summary());             // ...which the summary counts as 3 songs
        Assert.True(File.Exists(Path.Combine(other, "pf", "sound", "strm", "Super Mario", "Overworld_b.brstm")));
        Assert.False(BuildSync.Plan(lib.DefaultBuild, ProjectPlusFolder.Resolve(other)).HasChanges);
    }

    [Fact]
    public void Sync_WarnsAboutSongsThatCantBeFound()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        Tracklist(lib.DefaultBuild.TracklistDir, "Temple_of_Time", Song(0xF000, "Gone/Nowhere", "Lost"));

        SyncPlan plan = BuildSync.Plan(lib.DefaultBuild, t.ProjectPlus);

        Assert.Equal(["Gone/Nowhere"], plan.MissingSongs);
        Assert.Contains("can't be found", plan.Summary());
    }

    [Fact]
    public void Sync_RefusesTheLibraryFolderAsTarget()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        string fake = Path.Combine(lib.Root, "Project+");
        Directory.CreateDirectory(Path.Combine(fake, "pf", "sound", "tracklist"));

        Assert.Throws<MusicLibraryException>(() => BuildSync.Plan(lib.DefaultBuild, ProjectPlusFolder.Resolve(fake)));
    }

    [Fact]
    public void BackslashPaths_AreFixedOnImport_AndSyncTreatsThemAsTheSameSong()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        t.SdSong("Kirby/Royal Road");
        // Hand-made tracklist with a '\' path (as some BrawlCrate edits produce).
        t.SdTracklist("Fountain_Of_Dreams", Song(0xF000, @"Kirby\Royal Road", "Royal Road"));

        var lib = t.CreateLibrary();

        Assert.Equal(1, lib.Info.FixedPathsOnImport);
        Assert.Equal("Kirby/Royal Road", lib.DefaultBuild.OpenStage("Fountain_Of_Dreams").Tracklist.Entries[0].Path);
        SyncPlan first = BuildSync.Plan(lib.DefaultBuild, t.ProjectPlus);
        Assert.Equal(["Fountain_Of_Dreams"], first.TracklistsToWrite);   // the card gets the fixed tracklist
        Assert.Empty(first.SongsToDelete);                               // and keeps the song
        BuildSync.Execute(first);
        Assert.True(t.SdHasSong("Kirby/Royal Road"));
        Assert.False(BuildSync.Plan(lib.DefaultBuild, t.ProjectPlus).HasChanges);
    }

    [Fact]
    public void BackslashPathsInABuild_WarnAndAreFixedOnSave()
    {
        using var t = new TempSetup();
        t.StockProjectPlus();
        var lib = t.CreateLibrary();
        Tracklist(lib.DefaultBuild.TracklistDir, "Temple_of_Time", Song(0xF000, @"Zelda\Temple of Time 1", "ToT 1"));
        var session = lib.DefaultBuild.OpenStage("Temple_of_Time");

        Assert.Contains(session.Validate(), p => p.Severity == ProblemSeverity.Warning && p.Message.Contains("fixed when you save"));
        Assert.Equal(SongSource.Library, session.Locate(session.Tracklist.Entries[0]).Source);
        Assert.Contains("Zelda/Temple of Time 1", lib.DefaultBuild.SongPathsUsed());
        session.Save();

        Assert.Equal("Zelda/Temple of Time 1", TlstFile.Load(lib.DefaultBuild.TracklistPath("Temple_of_Time")).Entries[0].Path);
    }

    private static string SnapshotOutsideSound(TempSetup t) =>
        string.Join("\n", Directory.EnumerateFiles(t.SdCard, "*", SearchOption.AllDirectories)
            .Where(f => !AppLibraryTestAccess.IsInside(f, t.Sound))
            .Order()
            .Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}"));

    // ---------- Real files: copy the real P+ music into temp, import, sync to another temp card ----------

    [Fact]
    public void RealProjectPlus_ImportsAndSyncsToAFreshCard()
    {
        if (!Directory.Exists(RealData.BuildSound)) { output.WriteLine("Real P+ build not present; skipped."); return; }
        using var t = new TempSetup();
        string realPplus = Path.GetFullPath(Path.Combine(RealData.BuildSound, "..", ".."));

        var lib = AppLibrary.CreateFromProjectPlus(t.LibraryRoot, ProjectPlusFolder.Resolve(realPplus));
        Build def = lib.DefaultBuild;
        string card = Path.Combine(t.Root, "Card", "Project+");
        Directory.CreateDirectory(Path.Combine(card, "pf", "sound", "tracklist"));
        SyncPlan plan = BuildSync.Plan(def, ProjectPlusFolder.Resolve(card));
        BuildSync.Execute(plan);

        output.WriteLine($"Library: {lib.Songs.All().Count()} songs, {def.TracklistNames().Count()} tracklists. " +
                         $"Synced {plan.SongsToCopy.Count} songs + {plan.TracklistsToWrite.Count} tracklists; missing {plan.MissingSongs.Count}.");
        Assert.Equal(304, lib.Songs.All().Count());                      // every stock song, incl. the 6 "_b" files
        Assert.Equal(153, def.TracklistNames().Count());
        Assert.Equal(304, plan.SongsToCopy.Count);                       // nothing lost on the way to a card
        Assert.Empty(plan.MissingSongs);
        Assert.Empty(plan.SongsToDelete);
        Assert.False(BuildSync.Plan(def, ProjectPlusFolder.Resolve(card)).HasChanges);
        // The real build was only read.
        Assert.False(File.Exists(Path.Combine(realPplus, "pf", "sound", "tracklist", "Battlefield.tlst.tmp")));

        // Then the owner's music repo as a second build, synced over the same card.
        if (!Directory.Exists(RealData.MusicRoot)) { output.WriteLine("Music repo not present; second half skipped."); return; }
        ImportResult imported = lib.ImportMusicFolder(MusicFolder.Open(RealData.MusicRoot), "Moist");
        Build moist = imported.Build;
        // 4 entries were written with "\" (2025-10 commits); fixed in the repo 2026-11-20. Either way, none survive the import.
        Assert.Contains(imported.FixedPaths, new[] { 0, 4 });
        Assert.DoesNotContain(moist.SongPathsUsed(), p => p.Contains('\\'));
        SyncPlan moistPlan = BuildSync.Plan(moist, ProjectPlusFolder.Resolve(card));
        BuildSync.Execute(moistPlan);

        int renamed = lib.Songs.All().Count(p => p.EndsWith(" (2)"));
        output.WriteLine($"Moist: library now {lib.Songs.All().Count()} songs ({renamed} renamed), {moist.TracklistNames().Count()} tracklists. " +
                         $"Sync copied {moistPlan.SongsToCopy.Count}, wrote {moistPlan.TracklistsToWrite.Count} tracklists, removed {moistPlan.SongsToDelete.Count}, missing {moistPlan.MissingSongs.Count}.");
        Assert.Equal(0, renamed);                                        // its stock songs are byte-identical to 3.2.1's
        Assert.Empty(moistPlan.MissingSongs);
        Assert.Equal(304 + 96, lib.Songs.All().Count());                 // 397 repo songs - 301 shared = 96 new
        SyncPlan again = BuildSync.Plan(moist, ProjectPlusFolder.Resolve(card));
        output.WriteLine("Second sync: " + string.Join(" | ", again.TracklistsToWrite.Take(5).Concat(again.SongsToCopy.Take(5)).Concat(again.SongsToDelete.Take(5).Select(d => "DEL " + d))));
        Assert.False(again.HasChanges);
    }
}

/// <summary>Test access to the library's folder check.</summary>
internal static class AppLibraryTestAccess
{
    public static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
