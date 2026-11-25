namespace PPlusTracklistEditor.Core.Library;

/// <summary>
/// The songs a build uses that don't come with Project+ (each with its "_b" hurry-up file when there is one), and how
/// much space their files take: what the user added, so they can keep an eye on how big the build gets.
/// </summary>
public sealed record SongSpace(IReadOnlyList<string> Files, long Bytes)
{
    /// <summary>A song and its hurry-up version count as one song.</summary>
    public int SongCount => Files.Select(p => SongStore.HurryUpBase(p) ?? p).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public string Text => $"{(SongCount == 1 ? "1 song" : $"{SongCount} songs")}, {SizeText.Of(Bytes)}";

    /// <summary>The songs that come with Project+ (what the hidden Project+ original uses).</summary>
    public static HashSet<string> ProjectPlusSongs(AppLibrary lib) => lib.DefaultBuild.SongPathsUsed();

    /// <summary>The added songs among <paramref name="songsUsed"/> (song paths as in a tracklist) that the library has.</summary>
    public static SongSpace Of(AppLibrary lib, IEnumerable<string> songsUsed, ISet<string> projectPlusSongs)
    {
        var files = new List<string>();
        long bytes = 0;
        foreach (string song in songsUsed.Where(s => !projectPlusSongs.Contains(s)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            foreach (string p in new[] { song, song + SongStore.HurryUpSuffix })
                if (lib.Songs.Has(p)) { files.Add(p); bytes += new FileInfo(lib.Songs.FileFor(p)).Length; }
        return new SongSpace(files, bytes);
    }

    /// <summary>For a whole build as saved.</summary>
    public static SongSpace Of(Build build) =>
        build.IsDefault ? new SongSpace([], 0) : Of(build.Library, build.SongPathsUsed(), ProjectPlusSongs(build.Library));
}
