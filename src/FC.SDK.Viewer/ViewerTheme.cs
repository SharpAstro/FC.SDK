using DIR.Lib;

namespace FC.SDK.Viewer;

/// <summary>
/// Colours and base metrics for the viewer chrome. Every size is a *design unit* — the layout engine
/// multiplies by DPI scale — so nothing here is a device pixel.
/// </summary>
public static class ViewerTheme
{
    public static readonly UiPalette Palette = new(
        ContentBg: new(0x14, 0x16, 0x1c, 0xff),
        PanelBg: new(0x1c, 0x20, 0x28, 0xff),
        HeaderBg: new(0x25, 0x2b, 0x36, 0xff),
        HeaderText: new(0xe8, 0xec, 0xf4, 0xff),
        BodyText: new(0xd2, 0xd8, 0xe2, 0xff),
        DimText: new(0x77, 0x80, 0x90, 0xff),
        Separator: new(0x2e, 0x35, 0x42, 0xff),
        Selection: new(0x2c, 0x4a, 0x78, 0xff));

    public static readonly UiMetrics Metrics = new(
        BaseFontSize: 13f,
        Padding: 6f,
        HeaderHeight: 26f,
        ItemHeight: 22f,
        ButtonHeight: 24f);

    public static readonly RGBAColor32 Accent = new(0x4f, 0x9d, 0xe8, 0xff);
    public static readonly RGBAColor32 ButtonBg = new(0x2b, 0x32, 0x3e, 0xff);
    public static readonly RGBAColor32 ButtonDisabledBg = new(0x21, 0x25, 0x2c, 0xff);
    public static readonly RGBAColor32 DangerBg = new(0x5a, 0x2b, 0x2b, 0xff);
    public static readonly RGBAColor32 ActiveBg = new(0x27, 0x4b, 0x33, 0xff);

    public static readonly RGBAColor32 Ok = new(0x6c, 0xc6, 0x77, 0xff);
    public static readonly RGBAColor32 Warn = new(0xd8, 0xa8, 0x3f, 0xff);
    public static readonly RGBAColor32 Error = new(0xe0, 0x6c, 0x6c, 0xff);
    public static readonly RGBAColor32 Trace = new(0x5f, 0x68, 0x78, 0xff);

    /// <summary>Colour for a log line by severity.</summary>
    public static RGBAColor32 LogColor(ViewerLogLevel level) => level switch
    {
        ViewerLogLevel.Trace => Trace,
        ViewerLogLevel.Debug => Palette.DimText,
        ViewerLogLevel.Info => Palette.BodyText,
        ViewerLogLevel.Warning => Warn,
        _ => Error,
    };
}
