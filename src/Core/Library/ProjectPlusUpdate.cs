using PPlusTracklistEditor.Core.Stages;
using PPlusTracklistEditor.Core.Sync;
using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

/// <summary>What a Project+ folder has that differs from the app's Project+ original (e.g. after a Project+ update).</summary>
public sealed class ProjectPlusChanges
{
    public required ProjectPlusFolder Folder { get; init; }
    public string? OldVersion { get; init; }
    public string? NewVersion { get; init; }
    /// <summary>Song lists the original doesn't have yet. They're added to every build too.</summary>
    public required IReadOnlyList<string> NewStages { get; init; }
    /// <summary>Song lists Project+ changed (not ones a sync from this app put there).</summary>
    public required IReadOnlyList<string> ChangedStages { get; init; }
    /// <summary>Stage list presets Project+ changed (0 = Switch00).</summary>
    public required IReadOnlyList<int> ChangedPresets { get; init; }
    /// <summary>New preset names, if Project+ changed them.</summary>
    public IReadOnlyList<string>? NewPresetNames { get; init; }
    /// <summary>Songs the new and changed song lists use that the library doesn't have yet.</summary>
    public required IReadOnlyList<string> NewSongs { get; init; }
    public bool IconsChanged { get; init; }
    public bool StageNamesChanged { get; init; }
    public bool StageVersionsChanged { get; init; }

    public bool HasChanges => NewStages.Count + ChangedStages.Count + ChangedPresets.Count + NewSongs.Count > 0
                              || NewPresetNames is not null || IconsChanged || StageNamesChanged || StageVersionsChanged;

    /// <summary>Plain words for the "this Project+ looks updated" message.</summary>
    public string Summary()
    {
        var lines = new List<string>();
        lines.Add(OldVersion is not null && NewVersion is not null && !SameVersion(OldVersion, NewVersion)
            ? $"This Project+ is version {NewVersion}; the app was set up from {OldVersion}."
            : "This Project+ has changes the app doesn't know about yet (for example from a Project+ update).");
        if (ChangedStages.Count > 0) lines.Add($"• {Plural(ChangedStages.Count, "stage has", "stages have")} different Project+ songs");
        if (NewStages.Count > 0) lines.Add($"• {Plural(NewStages.Count, "new stage", "new stages")}: {string.Join(", ", NewStages.Select(StageNames.Pretty))}");
        if (NewSongs.Count > 0) lines.Add($"• {Plural(NewSongs.Count, "new song", "new songs")}");
        if (ChangedPresets.Count > 0 || NewPresetNames is not null) lines.Add("• Different stage list presets");
        if (IconsChanged || StageNamesChanged || StageVersionsChanged) lines.Add("• Different stage select pictures, stage names or stage versions");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>"3.2" and "3.2.1" agree: the launcher's info.xml only gives the first two numbers.</summary>
    public static bool SameVersion(string a, string b) =>
        a == b || a.StartsWith(b + ".", StringComparison.Ordinal) || b.StartsWith(a + ".", StringComparison.Ordinal);

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

/// <summary>
/// "Update the app from this Project+": brings the app's Project+ original (the hidden build new builds start from,
/// and what "Reset to the Project+ songs" uses) and its own copies of the game's menu and stage files up to date with
/// a Project+ folder. The user's builds keep all their songs; stages new to the app are added to them with Project+'s
/// songs. Everything replaced is backed up first.
/// </summary>
/// <remarks>
/// A song list or preset in the folder only counts as changed by Project+ if it differs from the original AND isn't
/// what a sync from this app put there (the folder's <see cref="SyncRecord"/>, or equal to a build's own copy), so the
/// user's music never becomes the "original".
/// </remarks>
public static class ProjectPlusUpdate
{
    public static ProjectPlusChanges Check(AppLibrary lib, ProjectPlusFolder folder)
    {
        Build original = lib.DefaultBuild;
        var builds = lib.UserBuilds();
        var record = SyncRecord.Load(lib, folder.Root);

        var originalLists = original.TracklistNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newStages = new List<string>();
        var changedStages = new List<string>();
        foreach (string name in Directory.Exists(folder.TracklistDir) ? folder.TracklistNames().Order(StringComparer.OrdinalIgnoreCase) : Enumerable.Empty<string>())
        {
            byte[] there;
            try { there = NormalizedTracklist(Path.Combine(folder.TracklistDir, name + ".tlst")); }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentOutOfRangeException) { continue; }  // not a song list we can read
            string print = SyncRecord.Fingerprint(there);
            bool inOriginal = originalLists.Contains(name);
            if (inOriginal && SyncRecord.FingerprintOf(original.TracklistPath(name)) == print) continue;
            if (IsOurs(print, record.Tracklists.GetValueOrDefault(name), builds.Select(b => b.TracklistPath(name)))) continue;
            (inOriginal ? changedStages : newStages).Add(name);
        }

        var changedPresets = new List<int>();
        for (int i = 0; i < Build.PresetCount; i++)
        {
            string there = folder.StageListPath(i);
            if (!File.Exists(there)) continue;
            string print = SyncRecord.FingerprintOf(there);
            if (File.Exists(original.StageListPath(i)) && SyncRecord.FingerprintOf(original.StageListPath(i)) == print) continue;
            if (IsOurs(print, record.Presets.GetValueOrDefault(i), builds.Select(b => b.StageListPath(i)))) continue;
            changedPresets.Add(i);
        }

        IReadOnlyList<string>? newNames = null;
        if (folder.ReadPresetNames() is { } names && original.Info.PresetNames is { } had && !names.SequenceEqual(had)
            && !(record.PresetNames?.SequenceEqual(names) ?? false)
            && !builds.Any(b => b.Info.PresetNames?.SequenceEqual(names) ?? false))
            newNames = names;

        // Songs the incoming song lists use that the library lacks (and that the folder has).
        var newSongs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in changedStages.Concat(newStages))
            foreach (TlstEntry e in TlstFile.Load(Path.Combine(folder.TracklistDir, name + ".tlst")).Entries)
            {
                if (e.Path is null) continue;
                string song = SongStore.Normalize(e.Path);
                foreach (string p in new[] { song, song + SongStore.HurryUpSuffix })
                    if (!lib.Songs.Has(p) && folder.Songs.Has(p)) newSongs.Add(p);
            }

        return new ProjectPlusChanges
        {
            Folder = folder,
            OldVersion = lib.Info.ProjectPlusVersion,
            NewVersion = folder.ReadVersion(),
            NewStages = newStages,
            ChangedStages = changedStages,
            ChangedPresets = changedPresets,
            NewPresetNames = newNames,
            NewSongs = [.. newSongs],
            IconsChanged = Differs(folder.StageSelectMenuFile, lib.StageIconsFile),
            StageNamesChanged = Differs(folder.StageSlotNamesFile, lib.StageNamesFile),
            StageVersionsChanged = Directory.Exists(Path.Combine(folder.StageDir, "stageslot"))
                                   && StageAltTable.FromStageFolder(folder.StageDir).ToText() != lib.LoadStageAlts().ToText(),
        };
    }

    /// <summary>
    /// Applies <paramref name="changes"/> (from <see cref="Check"/>). Returns the folder holding the backups of
    /// everything it replaced.
    /// </summary>
    public static string Apply(AppLibrary lib, ProjectPlusChanges changes)
    {
        ProjectPlusFolder folder = changes.Folder;
        Build original = lib.DefaultBuild;
        string backup = Path.Combine(lib.BackupDir, $"update-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(backup);
        void Keep(string file, string asName)
        {
            if (!File.Exists(file)) return;
            string dest = Path.Combine(backup, asName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        // 1. Songs first, so no song list ever points at a song the library doesn't have.
        foreach (string song in changes.NewSongs)
            if (!lib.Songs.Has(song)) lib.Songs.Add(folder.Songs.FileFor(song), song);

        // 2. The original's song lists, presets and preset names.
        foreach (string name in changes.ChangedStages.Concat(changes.NewStages))
        {
            Keep(original.TracklistPath(name), Path.Combine("original", "tracklist", name + ".tlst"));
            SongStore.CopyTracklistNormalized(Path.Combine(folder.TracklistDir, name + ".tlst"), original.TracklistPath(name));
        }
        foreach (int i in changes.ChangedPresets)
        {
            Keep(original.StageListPath(i), Path.Combine("original", "stagelists", $"Switch{i:00}.rss"));
            Directory.CreateDirectory(original.StageListDir);
            File.Copy(folder.StageListPath(i), original.StageListPath(i), overwrite: true);
        }
        Keep(Path.Combine(original.Dir, "build.json"), Path.Combine("original", "build.json"));
        if (changes.NewPresetNames is { } names) original.SetPresetNames(names);

        // 3. New stages go into every build too (with Project+'s songs); nothing a build already has is changed.
        foreach (Build build in lib.UserBuilds())
            foreach (string name in changes.NewStages)
                if (!File.Exists(build.TracklistPath(name))) File.Copy(original.TracklistPath(name), build.TracklistPath(name));
        lib.AddKnownStages(changes.NewStages);

        // 4. The app's own copies of the game's menu and stage files.
        if (changes.IconsChanged) { Keep(lib.StageIconsFile, "sc_selmap.pac"); File.Copy(folder.StageSelectMenuFile, lib.StageIconsFile, overwrite: true); }
        if (changes.StageNamesChanged) { Keep(lib.StageNamesFile, "stagenames.txt"); File.Copy(folder.StageSlotNamesFile, lib.StageNamesFile, overwrite: true); }
        if (changes.StageVersionsChanged) { Keep(lib.StageAltsFile, "stagealts.txt"); lib.SaveStageAlts(folder); }

        // Keep the exact version if the folder only gives a vaguer one ("3.2" for "3.2.1").
        if (changes.NewVersion is { } version && !(lib.Info.ProjectPlusVersion is { } had && had.StartsWith(version + ".", StringComparison.Ordinal)))
            lib.Info.ProjectPlusVersion = version;
        lib.SaveInfo();
        return backup;
    }

    private static bool IsOurs(string print, string? recorded, IEnumerable<string> buildCopies) =>
        print == recorded || buildCopies.Any(f => File.Exists(f) && SyncRecord.FingerprintOf(f) == print);

    private static bool Differs(string folderFile, string libraryCopy) =>
        File.Exists(folderFile) && (!File.Exists(libraryCopy) || !File.ReadAllBytes(folderFile).AsSpan().SequenceEqual(File.ReadAllBytes(libraryCopy)));

    /// <summary>A song list as the library would store it ('\' song paths turned into '/').</summary>
    private static byte[] NormalizedTracklist(string file)
    {
        byte[] raw = File.ReadAllBytes(file);
        var tlst = TlstFile.Read(raw);
        if (!tlst.Entries.Any(e => e.Path?.Contains('\\') == true)) return raw;
        foreach (var e in tlst.Entries) if (e.Path is not null) e.Path = SongStore.Normalize(e.Path);
        return tlst.ToBytes();
    }
}
