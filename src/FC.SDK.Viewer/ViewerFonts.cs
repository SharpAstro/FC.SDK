using DIR.Lib;
using Microsoft.Extensions.Logging;
using SharpAstro.Fonts;

namespace FC.SDK.Viewer;

/// <summary>
/// The viewer's font set: a primary face for text, plus the symbol and emoji faces that cover the
/// glyphs the primary lacks.
/// </summary>
/// <remarks>
/// A platform UI face is narrower than it looks — Segoe UI carries <c>→ — ·</c> but none of
/// <c>◀ ▶ ☑ ☐ ✓ ✗ ⟳ ⏳</c>, all of which live in Segoe UI Symbol. Rather than assume, coverage is read
/// from each candidate's cmap, so a glyph is only used when some available face actually has it and
/// otherwise degrades to an ASCII stand-in instead of painting a blank box.
/// <para>
/// Two consumers, because the widget framework splits the job: <see cref="EmojiPath"/> goes to
/// <c>PixelWidgetBase.EmojiFontPath</c>, which <c>DrawText</c> uses automatically but only for
/// supplementary-plane codepoints (U+1F000+). BMP symbols need the font chosen per run, which the
/// declarative painter cannot do yet (DIR.Lib#29), so the widget draws those through the layout
/// engine's <c>Fill</c> escape hatch using <see cref="FontFor"/>.
/// </para>
/// </remarks>
public sealed class ViewerFonts
{
    /// <summary>
    /// A face to look for: by family name first, then by file name.
    /// </summary>
    /// <remarks>
    /// Both, because <c>FontResolver.ResolveInstalledFont</c> resolves a family through a hard-coded
    /// standard-family table and otherwise probes only <c>&lt;family&gt;.ttf</c> — which finds
    /// <c>Segoe UI</c> but never <c>Segoe UI Symbol</c>, whose file is <c>seguisym.ttf</c>. The
    /// file-name pass scans the installed-font index, so no absolute paths are hard-coded here.
    /// </remarks>
    private sealed record FaceCandidate(string Family, params string[] FileNames);

    // Ordered by preference. Same shape as drawboard/pdf-viewer's chain: the platform's own UI face
    // first, since a symbol lifted from a different family looks wrong beside the text next to it.
    private static readonly FaceCandidate[] PrimaryCandidates = OperatingSystem.IsWindows()
        ? [new("Segoe UI", "segoeui.ttf"), new("Tahoma", "tahoma.ttf"), new("Arial", "arial.ttf")]
        : OperatingSystem.IsMacOS()
            ? [new("Helvetica Neue", "HelveticaNeue.ttc"), new("Helvetica", "Helvetica.ttc"), new("Arial", "Arial.ttf")]
            : [new("DejaVu Sans", "DejaVuSans.ttf"), new("Liberation Sans", "LiberationSans-Regular.ttf"),
               new("Noto Sans", "NotoSans-Regular.ttf")];

    private static readonly FaceCandidate[] SymbolCandidates = OperatingSystem.IsWindows()
        ? [new("Segoe UI Symbol", "seguisym.ttf"), new("Segoe UI Emoji", "seguiemj.ttf")]
        : OperatingSystem.IsMacOS()
            ? [new("Apple Symbols", "Apple Symbols.ttf"), new("Arial Unicode MS", "Arial Unicode.ttf")]
            // DejaVu Sans has broad BMP symbol coverage, so on Linux it is often both primary and symbol.
            : [new("Noto Sans Symbols 2", "NotoSansSymbols2-Regular.ttf"), new("DejaVu Sans", "DejaVuSans.ttf"),
               new("Symbola", "Symbola.ttf")];

    private static readonly FaceCandidate[] EmojiCandidates = OperatingSystem.IsWindows()
        ? [new("Segoe UI Emoji", "seguiemj.ttf")]
        : OperatingSystem.IsMacOS()
            ? [new("Apple Color Emoji", "Apple Color Emoji.ttc", "AppleColorEmoji.ttf")]
            : [new("Noto Color Emoji", "NotoColorEmoji.ttf"), new("Noto Emoji", "NotoEmoji-Regular.ttf")];

    private readonly Dictionary<string, OpenTypeFont?> _faces = [];
    private readonly Dictionary<int, string?> _fontByCodepoint = [];

    /// <summary>Face used for ordinary text. Never empty — falls back to the platform default.</summary>
    public string PrimaryPath { get; }

    /// <summary>Face covering BMP symbols the primary lacks, or null if none is installed.</summary>
    public string? SymbolPath { get; }

    /// <summary>Face for supplementary-plane pictographs, or null if none is installed.</summary>
    public string? EmojiPath { get; }

    /// <summary>
    /// The DIR.Lib per-run resolver over the same chain. Not consumed by the declarative painter
    /// (see the remarks on this type) but the right tool for any text drawn directly.
    /// </summary>
    public FontFallbackResolver Fallback { get; }

    private ViewerFonts(string primary, string? symbol, string? emoji)
    {
        PrimaryPath = primary;
        SymbolPath = symbol;
        EmojiPath = emoji;
        Fallback = new FontFallbackResolver(primary, new[] { symbol, emoji }.OfType<string>());
    }

    public static ViewerFonts Resolve(ILogger logger)
    {
        var primary = FirstInstalled(PrimaryCandidates) ?? FontResolver.ResolveSystemFont();
        var symbol = FirstInstalled(SymbolCandidates);
        var emoji = FirstInstalled(EmojiCandidates);

        logger.LogInformation("Fonts — primary: {Primary}", primary);
        logger.LogInformation("Fonts — symbol:  {Symbol}", symbol ?? "(none installed; symbols degrade to ASCII)");
        logger.LogInformation("Fonts — emoji:   {Emoji}", emoji ?? "(none installed)");

        if (primary.Length == 0)
        {
            // DrawText silently no-ops on an empty font path, so without this the window would come
            // up completely blank with nothing in the log to explain it — the worst possible failure
            // for a tool whose output IS the log. Likely on a minimal container with no fonts package.
            logger.LogError(
                "No usable UI font found. Searched families [{Families}] across [{Directories}]. " +
                "The window will render without any text — install a font package " +
                "(Debian/Ubuntu: fonts-dejavu-core, Alpine: font-dejavu) and re-run.",
                string.Join(", ", PrimaryCandidates.Select(c => c.Family)),
                string.Join(", ", FontResolver.FontDirectories));
        }

        return new ViewerFonts(primary, symbol, emoji);
    }

    private static string? FirstInstalled(FaceCandidate[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (FontResolver.ResolveInstalledFont(candidate.Family) is { Length: > 0 } byFamily
                && File.Exists(byFamily))
            {
                return byFamily;
            }
        }

        // Family lookup missed every candidate; fall back to matching file names against the
        // installed-font index. Enumerated once and reused, since it walks every font directory.
        var installed = InstalledByFileName.Value;
        foreach (var candidate in candidates)
        {
            foreach (var fileName in candidate.FileNames)
            {
                if (installed.TryGetValue(fileName, out var path)) return path;
            }
        }

        return null;
    }

    private static readonly Lazy<Dictionary<string, string>> InstalledByFileName = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in FontResolver.EnumerateInstalledFonts())
        {
            // First match wins: FontDirectories yields system roots before per-user ones.
            map.TryAdd(Path.GetFileName(path), path);
        }
        return map;
    });

    /// <summary>
    /// The face that should draw <paramref name="codepoint"/>, or null when no available face covers
    /// it — in which case the caller must substitute something ASCII rather than draw a blank.
    /// </summary>
    public string? FontFor(int codepoint)
    {
        if (_fontByCodepoint.TryGetValue(codepoint, out var cached)) return cached;

        string? chosen = null;
        foreach (var candidate in new[] { PrimaryPath, SymbolPath, EmojiPath })
        {
            if (candidate is { Length: > 0 } && Covers(candidate, codepoint)) { chosen = candidate; break; }
        }

        _fontByCodepoint[codepoint] = chosen;
        return chosen;
    }

    /// <summary>True when some available face covers every codepoint in <paramref name="text"/>.</summary>
    public bool CanRender(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (FontFor(rune.Value) is null) return false;
        }
        return true;
    }

    /// <summary>True when the primary face itself covers the whole string — no fallback needed.</summary>
    public bool PrimaryCovers(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Covers(PrimaryPath, rune.Value)) return false;
        }
        return true;
    }

    private bool Covers(string fontPath, int codepoint)
    {
        if (!_faces.TryGetValue(fontPath, out var face))
        {
            try { face = OpenTypeFont.LoadFromFile(fontPath); }
            catch { face = null; }   // unreadable / unsupported container — treat as no coverage
            _faces[fontPath] = face;
        }

        return face is not null && face.GetGlyphId((uint)codepoint) != 0;
    }
}

/// <summary>
/// The glyphs the UI wants, each resolved once against the installed faces. A property returns the
/// real glyph when something can draw it and an ASCII stand-in when nothing can, so the same UI code
/// works on a machine with Segoe UI Symbol and on a bare container image.
/// </summary>
public sealed class ViewerGlyphs(ViewerFonts fonts)
{
    /// <param name="preferred">The glyph we would like to draw.</param>
    /// <param name="asciiFallback">What to draw when no installed face covers it.</param>
    private string Pick(string preferred, string asciiFallback) =>
        fonts.CanRender(preferred) ? preferred : asciiFallback;

    public string StepBack => Pick("◀", "<");          // ◀
    public string StepForward => Pick("▶", ">");       // ▶
    public string Checked => Pick("☑", "[x]");         // ☑
    public string Unchecked => Pick("☐", "[ ]");       // ☐
    public string Yes => Pick("✓", "yes");             // ✓
    public string No => Pick("✗", "NO");               // ✗
    public string FocusFar => Pick("⟵", "<<");         // ⟵
    public string FocusNear => Pick("⟶", ">>");        // ⟶
    public string Busy => Pick("⏳", "*");              // ⏳
    public string Camera => Pick("\U0001F4F7", "");         // 📷 (supplementary plane)
}
