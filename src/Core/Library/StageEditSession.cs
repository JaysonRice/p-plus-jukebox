using PPlusTracklistEditor.Core.Tlst;

namespace PPlusTracklistEditor.Core.Library;

public enum ProblemSeverity { Warning, Error }

/// <summary>Something wrong with a stage, in words the user can act on. Errors block saving; warnings don't.</summary>
public sealed record Problem(ProblemSeverity Severity, string Message, int? EntryIndex = null);

/// <summary>A song file waiting to be copied into the music folder when the stage is saved.</summary>
public sealed record PendingSong(string SourceFile, string SongPath);

/// <summary>
/// Editing one stage's tracklist. Nothing touches disk until <see cref="Save"/>: new song files are queued and
/// copied on save, then the tracklist is written. Cancel = just drop the session.
/// </summary>
public sealed class StageEditSession
{
    public const byte DefaultVolume = 80;
    public const byte DefaultFrequency = 40;

    private readonly MusicLibrary _library;
    private byte[] _savedBytes;
    private readonly List<PendingSong> _pending = [];

    public string StageName { get; }
    public string DisplayName => StageNames.Pretty(StageName);
    public TlstFile Tracklist { get; private set; }
    public IReadOnlyList<PendingSong> PendingSongs => _pending;

    /// <summary>True until the first save of a stage that only existed in the stock build.</summary>
    public bool StartsFromStock { get; private set; }

    internal StageEditSession(MusicLibrary library, string stageName, byte[] bytes, bool startsFromStock)
    {
        _library = library;
        StageName = stageName;
        _savedBytes = bytes;
        Tracklist = TlstFile.Read(bytes);
        StartsFromStock = startsFromStock;
    }

    public bool IsDirty => StartsFromStock || _pending.Count > 0 || !Tracklist.ToBytesOrEmpty().AsSpan().SequenceEqual(_savedBytes);

    public SongLocation Locate(TlstEntry entry) =>
        _pending.Any(p => p.SongPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))
            ? new SongLocation(SongSource.MusicFolder, _pending.First(p => p.SongPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).SourceFile)
            : _library.Locate(entry);

    /// <summary>
    /// Adds a new song from a .brstm file anywhere on the computer. The file is copied into
    /// <c>strm/&lt;series&gt;/&lt;title&gt;.brstm</c> on save, with a safe, unique name.
    /// </summary>
    public TlstEntry AddSong(string brstmFile, string title, string series)
    {
        if (!File.Exists(brstmFile)) throw new MusicLibraryException($"Can't find the file \"{brstmFile}\".");
        if (!IsBrstm(brstmFile))
            throw new MusicLibraryException($"\"{Path.GetFileName(brstmFile)}\" isn't a BRSTM song file. Convert it to BRSTM first.");
        if (string.IsNullOrWhiteSpace(title)) throw new MusicLibraryException("Please give the song a title.");

        string songPath = SongFileNames.UniqueSongPath(_library.Music, series, title, _pending.Select(p => p.SongPath));
        _pending.Add(new PendingSong(Path.GetFullPath(brstmFile), songPath));
        return AddEntry(songPath, title.Trim());
    }

    /// <summary>Adds a song that's already in the music folder or stock build (by its tracklist path).</summary>
    public TlstEntry AddExistingSong(string songPath, string title)
    {
        var probe = new TlstEntry { Path = songPath };
        if (Locate(probe).Source == SongSource.Missing) throw new MusicLibraryException($"There's no song \"{songPath}\".");
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

    /// <summary>Removes a song from this stage. Its file stays (other stages may use it). A queued file is un-queued if nothing else uses it.</summary>
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

    public IReadOnlyList<Problem> Validate()
    {
        var problems = new List<Problem>();
        var entries = Tracklist.Entries;

        if (entries.Count == 0)
            problems.Add(new(ProblemSeverity.Warning, "This stage has no songs, so it will be silent."));
        if (_library.Stock is not null && !_library.Stock.TracklistNames().Contains(StageName, StringComparer.OrdinalIgnoreCase))
            problems.Add(new(ProblemSeverity.Warning,
                $"Project+ doesn't have a stage tracklist called \"{StageName}\", so these songs won't play anywhere. It may be named differently in this version."));
        if (entries.Count > 0 && entries.All(e => e.Frequency == 0))
            problems.Add(new(ProblemSeverity.Warning, "Every song is set to never play (\"how often\" is 0)."));

        for (int i = 0; i < entries.Count; i++)
        {
            TlstEntry e = entries[i];
            string name = $"\"{MusicLibrary.DisplayTitle(e)}\"";
            if (Locate(e).Source == SongSource.Missing)
                problems.Add(new(ProblemSeverity.Error, $"{name}: its song file ({e.Path}.brstm) is missing, so it would play silence. Find the file or remove the song.", i));
            if (e.Path is null && DiscSongs.Find(e.SongId) is null)
                problems.Add(new(ProblemSeverity.Warning, $"{name} is an original Brawl song this app doesn't recognize (ID {e.SongId:X4}).", i));
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
    /// Copies queued song files into the music folder, backs up the old tracklist, and writes the new one.
    /// Refuses if <see cref="Validate"/> finds errors.
    /// </summary>
    public void Save()
    {
        var errors = Validate().Where(p => p.Severity == ProblemSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new MusicLibraryException("Can't save yet:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "• " + e.Message)));

        byte[] bytes = Tracklist.ToBytes();
        MusicFolder music = _library.Music;

        foreach (PendingSong song in _pending)
        {
            string target = music.SongFile(song.SongPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(song.SourceFile, target, overwrite: false); // unique name was chosen when queued; never overwrite
        }
        _pending.Clear();

        string tlstPath = music.TracklistPath(StageName);
        if (File.Exists(tlstPath))
        {
            Directory.CreateDirectory(_library.BackupDir);
            File.Copy(tlstPath, Path.Combine(_library.BackupDir, $"{StageName}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.tlst"));
        }
        Tracklist.Save(tlstPath);
        _savedBytes = bytes;
        StartsFromStock = false;
    }

    internal static bool IsBrstm(string file)
    {
        Span<byte> magic = stackalloc byte[4];
        using var fs = File.OpenRead(file);
        return fs.Read(magic) == 4 && magic.SequenceEqual("RSTM"u8);
    }
}
