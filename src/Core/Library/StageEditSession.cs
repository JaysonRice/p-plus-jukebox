using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

public enum ProblemSeverity { Warning, Error }

/// <summary>Something wrong with a stage, in words the user can act on. Errors block saving; warnings don't.</summary>
public sealed record Problem(ProblemSeverity Severity, string Message, int? EntryIndex = null);

/// <summary>A song file waiting to be copied into the library when the stage is saved.</summary>
public sealed record PendingSong(string SourceFile, string SongPath);

/// <summary>
/// Editing one stage's tracklist in one build. Nothing touches disk until <see cref="Save"/>: new song files are
/// queued and copied into the shared library on save, then the build's tracklist is written. Cancel = drop the session.
/// </summary>
public sealed class StageEditSession
{
    public const byte DefaultVolume = 80;
    public const byte DefaultFrequency = 40;

    private byte[] _savedBytes;
    private readonly List<PendingSong> _pending = [];

    public Build Build { get; }
    public string StageName { get; }
    public string DisplayName => StageNames.Pretty(StageName);
    public TlstFile Tracklist { get; private set; }
    public IReadOnlyList<PendingSong> PendingSongs => _pending;
    private AppLibrary Library => Build.Library;

    internal StageEditSession(Build build, string stageName, byte[] bytes)
    {
        Build = build;
        StageName = stageName;
        _savedBytes = bytes;
        Tracklist = TlstFile.Read(bytes);
    }

    public bool IsDirty => _pending.Count > 0 || !Tracklist.ToBytesOrEmpty().AsSpan().SequenceEqual(_savedBytes);

    public SongLocation Locate(TlstEntry entry)
    {
        PendingSong? pending = _pending.FirstOrDefault(p => p.SongPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
        return pending is not null ? new SongLocation(SongSource.Library, pending.SourceFile) : Build.Locate(entry);
    }

    /// <summary>
    /// Adds a new song from a .brstm file anywhere on the computer. On save it's copied into the library as
    /// <c>songs\&lt;series&gt;\&lt;title&gt;.brstm</c> with a safe, unique name.
    /// </summary>
    public TlstEntry AddSong(string brstmFile, string title, string series)
    {
        if (!File.Exists(brstmFile)) throw new MusicLibraryException($"Can't find the file \"{brstmFile}\".");
        if (!IsBrstm(brstmFile))
            throw new MusicLibraryException($"\"{Path.GetFileName(brstmFile)}\" isn't a BRSTM song file. Convert it to BRSTM first.");
        if (string.IsNullOrWhiteSpace(title)) throw new MusicLibraryException("Please give the song a title.");

        string songPath = SongFileNames.UniqueSongPath(Library.Songs.Has, series, title, _pending.Select(p => p.SongPath));
        _pending.Add(new PendingSong(Path.GetFullPath(brstmFile), songPath));
        return AddEntry(songPath, title.Trim());
    }

    /// <summary>Adds a song that's already in the library (by its path, "Series/Name").</summary>
    public TlstEntry AddExistingSong(string songPath, string? title = null)
    {
        if (Locate(new TlstEntry { Path = songPath }).Source == SongSource.Missing)
            throw new MusicLibraryException($"The library has no song \"{songPath}\".");
        return AddEntry(songPath, string.IsNullOrWhiteSpace(title) ? songPath.Split('/')[^1] : title.Trim());
    }

    private TlstEntry AddEntry(string songPath, string title)
    {
        var entry = new TlstEntry
        {
            SongId = Tracklist.NextFreeCustomSongId(),
            Path = songPath,
            Title = title,
            Volume = DefaultVolume,
            Frequency = DefaultFrequency,
        };
        Tracklist.Entries.Add(entry);
        return entry;
    }

    /// <summary>Removes a song from this stage. The library keeps the file. A queued file is un-queued if nothing else uses it.</summary>
    public void Remove(TlstEntry entry)
    {
        Tracklist.Entries.Remove(entry);
        if (entry.Path is not null && !Tracklist.Entries.Any(e => entry.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase)))
            _pending.RemoveAll(p => p.SongPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
    }

    public void Move(int from, int to)
    {
        var list = Tracklist.Entries;
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to) return;
        TlstEntry e = list[from];
        list.RemoveAt(from);
        list.Insert(to, e);
    }

    /// <summary>True when the Default build has this stage, so <see cref="ResetToDefault"/> can work.</summary>
    public bool CanResetToDefault => !Build.IsDefault && File.Exists(Library.DefaultBuild.TracklistPath(StageName));

    /// <summary>Replaces this stage's songs with the Default build's (unsaved until <see cref="Save"/>).</summary>
    public void ResetToDefault()
    {
        if (!CanResetToDefault) throw new MusicLibraryException("There's no default song list for this stage.");
        Tracklist = TlstFile.Load(Library.DefaultBuild.TracklistPath(StageName));
        _pending.Clear();
    }

    public IReadOnlyList<Problem> Validate()
    {
        var problems = new List<Problem>();
        var entries = Tracklist.Entries;

        if (entries.Count == 0)
            problems.Add(new(ProblemSeverity.Warning, "This stage has no songs, so it will be silent."));
        if (Library.KnownStages.Count > 0 && !Library.KnownStages.Contains(StageName))
            problems.Add(new(ProblemSeverity.Warning,
                $"Your Project+ doesn't have a stage tracklist called \"{StageName}\", so these songs won't play anywhere. It may be named differently in your version."));
        if (entries.Count > 0 && entries.All(e => e.Frequency == 0))
            problems.Add(new(ProblemSeverity.Warning, "Every song is set to never play (\"how often\" is 0)."));

        for (int i = 0; i < entries.Count; i++)
        {
            TlstEntry e = entries[i];
            string name = $"\"{Build.DisplayTitle(e)}\"";
            if (Locate(e).Source == SongSource.Missing)
                problems.Add(new(ProblemSeverity.Error, $"{name}: its song file ({e.Path}.brstm) is missing, so it would play silence. Find the file or remove the song.", i));
            if (e.Path is null && DiscSongs.Find(e.SongId) is null)
                problems.Add(new(ProblemSeverity.Warning, $"{name} is an original Brawl song this app doesn't recognize (ID {e.SongId:X4}).", i));
            if (e.Path is not null && e.Path.Contains('\\'))
                problems.Add(new(ProblemSeverity.Warning, $"{name}: its file path uses \"\\\" instead of \"/\", which the Wii may not understand. It will be fixed when you save.", i));
            if (e.Volume == 0)
                problems.Add(new(ProblemSeverity.Warning, $"{name} has volume 0, so it will be silent.", i));
            if (entries.Take(i).Any(prev => prev.SongId == e.SongId))
                problems.Add(new(ProblemSeverity.Error, $"{name} has the same song ID as another song on this stage. Remove and re-add it.", i));
        }

        try { Tracklist.ToBytes(); }
        catch (TlstFormatException ex) { problems.Add(new(ProblemSeverity.Error, ex.Message)); }
        return problems;
    }

    /// <summary>
    /// Copies queued song files into the library, backs up the build's old tracklist, and writes the new one.
    /// Refuses if <see cref="Validate"/> finds errors.
    /// </summary>
    public void Save()
    {
        var errors = Validate().Where(p => p.Severity == ProblemSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new MusicLibraryException("Can't save yet:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "• " + e.Message)));

        foreach (TlstEntry e in Tracklist.Entries)
            if (e.Path is not null) e.Path = SongStore.Normalize(e.Path);
        byte[] bytes = Tracklist.ToBytes();
        foreach (PendingSong song in _pending)
            Library.Songs.Add(song.SourceFile, song.SongPath); // unique name was chosen when queued; never overwrites
        _pending.Clear();

        string tlstPath = Build.TracklistPath(StageName);
        if (File.Exists(tlstPath))
        {
            string backups = Path.Combine(Library.BackupDir, Build.Id);
            Directory.CreateDirectory(backups);
            File.Copy(tlstPath, Path.Combine(backups, $"{StageName}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.tlst"));
        }
        Tracklist.Save(tlstPath);
        _savedBytes = bytes;
    }

    /// <summary>A copy of the current songs and queued files, for Undo.</summary>
    public StageSnapshot Snapshot() =>
        new(Tracklist.Entries.Select(e => e.Clone()).ToArray(), _pending.ToArray());

    /// <summary>Puts the songs and queued files back the way a <see cref="Snapshot"/> had them.</summary>
    public void Restore(StageSnapshot snapshot)
    {
        Tracklist.Entries.Clear();
        Tracklist.Entries.AddRange(snapshot.Entries.Select(e => e.Clone()));
        _pending.Clear();
        _pending.AddRange(snapshot.Pending);
    }

    internal static bool IsBrstm(string file)
    {
        Span<byte> magic = stackalloc byte[4];
        using var fs = File.OpenRead(file);
        return fs.Read(magic) == 4 && magic.SequenceEqual("RSTM"u8);
    }
}
