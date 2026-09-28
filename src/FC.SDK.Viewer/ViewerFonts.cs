using DIR.Lib;
using Microsoft.Extensions.Logging;

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
/// One consumer now: <see cref="Fallback"/> goes to <c>PixelWidgetBase.FontFallback</c>, and the
/// declarative painter splits every text leaf into per-font runs through it, measure and paint alike.
/// That retired the viewer's own <c>Fill</c>-leaf glyph path, which existed only because the painter
/// once drew a whole leaf with one font. The resolver is built by ROLE so a codepoint whose Unicode
/// default presentation is emoji (the hourglass, the camera) is taken from the emoji face even where
/// the symbol face also has it.
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

    /// <summary>Face used for ordinary text. Never empty — falls back to the platform default.</summary>
    public string PrimaryPath { get; }

    /// <summary>Face covering BMP symbols the primary lacks, or null if none is installed.</summary>
    public string? SymbolPath { get; }

    /// <summary>Face for supplementary-plane pictographs, or null if none is installed.</summary>
    public string? EmojiPath { get; }

    /// <summary>
    /// The DIR.Lib per-run resolver over the same chain, handed to the widget as its
    /// <c>FontFallback</c> so every text leaf draws each run with a face that covers it.
    /// </summary>
    public FontFallbackResolver Fallback { get; }

    private ViewerFonts(string primary, string? symbol, string? emoji)
    {
        PrimaryPath = primary;
        SymbolPath = symbol;
        EmojiPath = emoji;
        Fallback = FontFallbackResolver.FromRoles(primary, symbol, emoji);
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
    /// True when some available face covers every codepoint in <paramref name="text"/>. Asked of the
    /// same resolver the painter draws with, so "can render" and "will render" cannot disagree.
    /// </summary>
    public bool CanRender(string text) => Fallback.CanRender(text);
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

    // The step arrows, the checkbox marks and the tick are drawn icons now (IconKind.CaretLeft /
    // CaretRight / Check, and Builder.Checkbox), so they need no face at all. What is left here is what
    // the icon family has no member for.
    public string No => Pick("✗", "NO");               // ✗
    public string FocusFar => Pick("⟵", "<<");         // ⟵
    public string FocusNear => Pick("⟶", ">>");        // ⟶
    public string Busy => Pick("⏳", "*");              // ⏳
    public string Camera => Pick("\U0001F4F7", "");         // 📷 (supplementary plane)
}
