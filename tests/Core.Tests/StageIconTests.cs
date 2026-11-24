using System.Diagnostics;
using System.Globalization;
using PPlusTracklistEditor.Core.Menu;
using PPlusTracklistEditor.Core.Stages;
using Xunit.Abstractions;

namespace PPlusTracklistEditor.Core.Tests;

public class StageIconTests(ITestOutputHelper output)
{
    private static string SelMap => Path.GetFullPath(Path.Combine(RealData.BuildSound, "..", "menu2", "sc_selmap.pac"));

    private static uint Pixel(WiiImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return (uint)(img.Pixels[i + 3] << 24 | img.Pixels[i + 2] << 16 | img.Pixels[i + 1] << 8 | img.Pixels[i]);
    }

    [Fact]
    public void Rgb5A3_DecodesBothKinds()
    {
        byte[] block = new byte[32];
        block[0] = 0xFC; block[1] = 0x00;                       // opaque: 1 11111 00000 00000 = pure red
        block[2] = 0x3F; block[3] = 0x00;                       // 0 011 1111 0000 0000: alpha 3/7, red
        var img = WiiTexture.Decode(WiiTextureFormat.RGB5A3, 4, 4, block);
        Assert.Equal(0xFFFF0000u, Pixel(img, 0, 0));
        Assert.Equal(0x6DFF0000u, Pixel(img, 1, 0));            // 3 → 0x6D (3 bits spread over 8)
        Assert.Equal(0u, Pixel(img, 3, 3));                     // all-zero = fully clear
    }

    [Fact]
    public void Ci8_UsesBlocksOf8By4()
    {
        byte[] data = new byte[64];                             // a 16x4 image = two 8x4 blocks side by side
        data[0] = 1;                                            // block 0, pixel (0,0)
        data[32 + 7 + 3 * 8] = 2;                               // block 1, its pixel (7,3) = image (15,3)
        ushort[] palette = [0x0000, 0xFFFF, 0x83E0];            // clear, white, green
        var img = WiiTexture.Decode(WiiTextureFormat.CI8, 16, 4, data, palette);
        Assert.Equal(0xFFFFFFFFu, Pixel(img, 0, 0));
        Assert.Equal(0xFF00FF00u, Pixel(img, 15, 3));
        Assert.Equal(0u, Pixel(img, 8, 0));
    }

    [Fact]
    public void Cmpr_DecodesFourSubBlocks()
    {
        byte[] data = new byte[32];
        data[0] = 0xF8; data[1] = 0x00; data[2] = 0x00; data[3] = 0x1F;      // c0 red > c1 blue: four-color mode
        data[24] = 0x00; data[25] = 0x00; data[26] = 0x00; data[27] = 0x00;  // 4th sub-block (bottom right): c0 = c1 = black
        data[28] = 0xFF;                                                      // its first row uses index 3 = clear
        var img = WiiTexture.Decode(WiiTextureFormat.CMPR, 8, 8, data);
        Assert.Equal(0xFFFF0000u, Pixel(img, 0, 0));            // index 0 = c0
        Assert.Equal(0u, Pixel(img, 4, 4));                     // c0 <= c1: index 3 is transparent
        Assert.Equal(0xFF000000u, Pixel(img, 4, 5));
    }

    [Fact]
    public void Spots_MatchTheRowTable()
    {
        for (int n = 0; n <= 33; n++)
        {
            var spots = StageSelectSpots.For(n);
            Assert.Equal(n, spots.Count);
            var rows = new List<int>();
            float? y = null;
            foreach (var s in spots)
            {
                if (y is { } prev && Math.Abs(prev - s.Y) < 0.01f) rows[^1]++;
                else { Assert.True(y is null || s.Y < y, "rows go top to bottom"); rows.Add(1); y = s.Y; }
            }
            Assert.Equal(StageSelectLayout.Rows(n), rows);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => StageSelectSpots.For(34));
    }

    [Fact]
    public void BrokenFiles_GiveNoIcons()
    {
        using var t = new TempSetup();
        string path = Path.Combine(t.Root, "sc_selmap.pac");
        Assert.Equal(0, StageIcons.Load(path).Count);           // missing
        File.WriteAllBytes(path, Lz11.Compress("not an archive at all"u8.ToArray()));
        Assert.Equal(0, StageIcons.Load(path).Count);
        Assert.Null(StageIcons.Empty.For(1));
    }

    [Fact]
    public void RealFile_IconsMatchBrawlCrate()
    {
        if (!File.Exists(SelMap)) { output.WriteLine("Skipped: no sc_selmap.pac"); return; }
        var watch = Stopwatch.StartNew();
        var icons = StageIcons.Load(SelMap);
        output.WriteLine($"Loaded {icons.Count} icons in {watch.ElapsedMilliseconds} ms");
        Assert.True(icons.Count >= 60);
        var bf = icons.For(1)!;
        Assert.Equal((128, 112), (bf.Width, bf.Height));
        Assert.Null(icons.For(999));

        if (!File.Exists(RealData.BrawlLib) || !File.Exists(RealData.PowerShell32)) { output.WriteLine("Skipped BrawlCrate check"); return; }
        int[] ids = [1, 25, 54];                                // Battlefield, Fountain of Dreams, Dream Land
        string script = $"$a=[Reflection.Assembly]::LoadFrom('{RealData.BrawlLib}'); " +
                        $"$r=[BrawlLib.SSBB.ResourceNodes.NodeFactory]::FromFile($null,'{SelMap}'); " +
                        "function W($n){ $n; foreach($c in $n.Children){ W $c } }; $all = W $r; " +
                        $"foreach($id in @({string.Join(",", ids.Select(i => $"'{i:00}'"))})) {{ " +
                        "$t = $all | ? { $_.GetType().Name -eq 'TEX0Node' -and $_.Name -eq \"MenSelmapIcon.$id\" } | select -First 1; " +
                        "$b = $t.GetImage(0); $sb = New-Object Text.StringBuilder; " +
                        "for($y=0;$y -lt $b.Height;$y++){ for($x=0;$x -lt $b.Width;$x++){ [void]$sb.Append($b.GetPixel($x,$y).ToArgb().ToString('X8')) } }; " +
                        "$sb.ToString(); $b.Dispose() }; $r.Dispose()";
        var psi = new ProcessStartInfo(RealData.PowerShell32, ["-NoProfile", "-Command", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string[] lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        output.WriteLine(p.StandardError.ReadToEnd());
        p.WaitForExit();
        Assert.Equal(ids.Length, lines.Length);

        for (int k = 0; k < ids.Length; k++)
        {
            var img = icons.For(ids[k])!;
            Assert.Equal(img.Width * img.Height * 8, lines[k].Length);
            int worst = 0;
            for (int i = 0; i < img.Width * img.Height; i++)
            {
                uint theirs = uint.Parse(lines[k].AsSpan(i * 8, 8), NumberStyles.HexNumber), ours = Pixel(img, i % img.Width, i / img.Width);
                if (theirs >> 24 == 0 && ours >> 24 == 0) continue;                 // fully clear: color doesn't matter
                for (int shift = 0; shift < 32; shift += 8)
                    worst = Math.Max(worst, Math.Abs((int)(theirs >> shift & 0xFF) - (int)(ours >> shift & 0xFF)));
            }
            Assert.True(worst <= 1, $"icon {ids[k]} differs by {worst}");         // BrawlLib rounds 5-bit colors, we shift: off by 1 at most
        }
    }

    [Fact]
    public void RealFile_AddedStagesUseTheGamesLookup()
    {
        if (!File.Exists(SelMap)) { output.WriteLine("Skipped: no sc_selmap.pac"); return; }
        var icons = StageIcons.Load(SelMap);
        var arc = ArcArchive.Parse(Lz11.Decompress(File.ReadAllBytes(SelMap)));
        var brres = Enumerable.Range(0, arc.Count).Select(arc.Data).Where(d => Brres.LooksLikeBrres(d)).Select(Brres.Parse)
            .First(b => b.TexturePattern("MenSelmapIcon").Count > 0);
        // Keyframes as BrawlCrate shows them: stock stages keep their number, P+'s added ones point at 80-98.
        var keys = brres.TexturePattern("MenSelmapIcon").ToDictionary(k => (int)k.Frame, k => k.Texture);
        Assert.Equal("MenSelmapIcon.01", keys[1]);
        Assert.Equal("MenSelmapIcon.80", keys[35]);
        Assert.Equal("MenSelmapIcon.95", keys[61]);
        foreach (var (id, texture) in new[] { (1, "01"), (35, "80"), (49, "94"), (61, "95"), (64, "98") })
            Assert.Equal(brres.DecodeTexture("MenSelmapIcon." + texture)!.Pixels, icons.For(id)!.Pixels);

        string switchDir = Path.GetFullPath(Path.Combine(RealData.BuildSound, "..", "stage", "switch"));
        if (!Directory.Exists(switchDir)) return;
        var missing = Directory.GetFiles(switchDir, "Switch*.rss").SelectMany(f => StageListFile.Load(f).Entries)
            .Select(e => e.CosmeticId).Distinct().Where(id => icons.For(id) is null).ToList();
        Assert.Empty(missing);                                                   // every stage in the build's lists
    }

    [Fact]
    public void Library_KeepsACopyOfTheIcons()
    {
        if (!File.Exists(SelMap)) { output.WriteLine("Skipped: no sc_selmap.pac"); return; }
        using var t = new TempSetup();
        t.StockProjectPlus();
        Directory.CreateDirectory(Path.GetDirectoryName(t.ProjectPlus.StageSelectMenuFile)!);
        File.Copy(SelMap, t.ProjectPlus.StageSelectMenuFile);
        var lib = t.CreateLibrary();
        Assert.True(lib.LoadStageIcons().Count >= 60);

        File.Delete(lib.StageIconsFile);                                         // older libraries get it on open
        Assert.True(Library.AppLibrary.Open(lib.Root).LoadStageIcons().Count >= 60);
    }
}
