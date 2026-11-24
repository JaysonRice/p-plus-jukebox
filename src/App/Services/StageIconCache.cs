using System.Windows.Media;
using System.Windows.Media.Imaging;
using PPlusTracklistEditor.Core.Library;
using PPlusTracklistEditor.Core.Stages;

namespace PPlusTracklistEditor.App.Services;

/// <summary>
/// The stage icons of the library's copy of <c>sc_selmap.pac</c>, as frozen WPF images. Read once per file version and
/// shared by every build's Stage Lists tab.
/// </summary>
public static class StageIconCache
{
    private static string? _key;
    private static StageIcons _icons = StageIcons.Empty;
    private static readonly Dictionary<int, ImageSource?> Images = [];

    /// <summary>A lookup from cosmetic ID to icon (null = no icon) for this library.</summary>
    public static Func<int, ImageSource?> For(AppLibrary library)
    {
        string path = library.StageIconsFile;
        string key = path + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        if (key != _key)
        {
            _key = key;
            _icons = library.LoadStageIcons();
            Images.Clear();
        }
        var icons = _icons;
        return id =>
        {
            if (Images.TryGetValue(id, out var done) && ReferenceEquals(icons, _icons)) return done;
            ImageSource? image = null;
            if (icons.For(id) is { } img)
            {
                var bitmap = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Pixels, img.Width * 4);
                bitmap.Freeze();
                image = bitmap;
            }
            if (ReferenceEquals(icons, _icons)) Images[id] = image;
            return image;
        };
    }
}
