using PPlusTracklistEditor.Core.Stages;

namespace PPlusTracklistEditor.App.Services;

/// <summary>Where the game draws each stage icon on a stage select page, scaled to screen pixels.</summary>
public static class StagePageLayout
{
    public readonly record struct Spot(double Left, double Top, double Width, double Height);

    /// <summary>An area in game units (y up).</summary>
    public readonly record struct Frame(double Left, double Right, double Bottom, double Top);

    /// <summary>
    /// The spots for a page of <paramref name="count"/> stages (none if the game has no layout for that many), inside
    /// <paramref name="frame"/> or, if null, the icons' own bounds plus a small margin. At most
    /// <paramref name="pixelsPerUnit"/>, smaller if needed to fit <paramref name="maxWidth"/>.
    /// </summary>
    public static (double Width, double Height, IReadOnlyList<Spot> Spots) Place(int count, double maxWidth, double pixelsPerUnit, Frame? frame = null)
    {
        var spots = count is > 0 && count <= StageSelectSpots.MaxCount ? StageSelectSpots.For(count) : [];
        if (spots.Count == 0 && frame is null) return (0, 0, []);
        var f = frame ?? new Frame(spots.Min(s => s.X - Half(s).W) - 1, spots.Max(s => s.X + Half(s).W) + 1,
                                   spots.Min(s => s.Y - Half(s).H) - 1, spots.Max(s => s.Y + Half(s).H) + 1);
        double scale = Math.Min(pixelsPerUnit, maxWidth / (f.Right - f.Left));
        var placed = spots.Select(s =>
        {
            var (w, h) = Half(s);
            return new Spot((s.X - w - f.Left) * scale, (f.Top - s.Y - h) * scale, 2 * w * scale, 2 * h * scale);
        }).ToList();
        return ((f.Right - f.Left) * scale, (f.Top - f.Bottom) * scale, placed);
    }

    private static (double W, double H) Half(IconSpot s) =>
        (StageSelectSpots.IconWidth * s.Scale / 2, StageSelectSpots.IconHeight * s.Scale / 2);
}
