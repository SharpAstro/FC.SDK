using DIR.Lib;
using FC.SDK.Canon;
using SdlVulkan.Renderer;
using Vortice.Vulkan;

namespace FC.SDK.Viewer;

/// <summary>
/// The whole UI. Every rect comes from the DIR.Lib layout engine — panels are declared as a
/// <see cref="Layout.Node"/> tree and painted by <c>RenderLayout</c>, which binds each click region
/// to the same arranged rect it drew, so a button can never be clickable somewhere it is not drawn.
/// The only geometry the widget computes itself is virtualization (how many rows fit), and that is
/// delegated to <see cref="ListScrollController"/>.
/// </summary>
/// <remarks>
/// Symbol glyphs (<c>◀ ▶ ☑ ✓</c>) come from a different face than the text: a platform UI font does not
/// cover them, and the declarative painter draws a whole text leaf with one font — per-run fallback in
/// that path is DIR.Lib#29 (https://github.com/SharpAstro/DIR.Lib/issues/29). Until then they go through
/// <see cref="Glyph"/>, which uses the layout engine's <c>Fill</c> escape hatch: the rect still comes
/// from arrange, only the font is chosen by the widget. Supplementary-plane pictographs need none of
/// this — <c>PixelWidgetBase.EmojiFontPath</c> already routes those.
/// </remarks>
public sealed class ViewerWidget : PixelWidgetBase<VulkanContext>
{
    private const float LeftPanelWidth = 232f;
    private const float RightPanelWidth = 400f;
    private const float LogPanelHeight = 190f;
    private const float TopBarHeight = 30f;
    private const float StatusBarHeight = 22f;
    private const float StepButtonWidth = 20f;
    private const float GlyphColumnWidth = 18f;

    private readonly VkRenderer _renderer;
    private readonly ViewerState _state;
    private readonly ViewerActions _actions;
    private readonly ViewerLog _log;
    private readonly ViewerFonts _fonts;
    private readonly ViewerGlyphs _glyphs;

    // Per-frame payloads for the Glyph() Fill leaves: the Fill content carries only a key, so the
    // glyph text and its colour are parked here and looked up when that leaf paints. Rebuilt every
    // frame in Render(), and only ever appended to, so an index stays valid for the frame that made it.
    private readonly List<(string Glyph, RGBAColor32 Color, float FontSize)> _glyphSlots = [];

    private readonly ListScrollController _actionScroll = new();
    private readonly ListScrollController _controlScroll = new();
    private readonly ListScrollController _logScroll = new() { Anchor = ScrollAnchor.Bottom };

    // Deferred GPU uploads, following the renderer's documented pattern: pixels captured on the
    // action thread, texture created and recorded during OnPreRenderPass, previous texture disposed
    // one frame later once its fence has been waited on.
    private readonly DeferredTexture _liveView;
    private readonly DeferredTexture _thumbnail;

    private float _fontSize = ViewerTheme.Metrics.BaseFontSize;

    public ViewerWidget(VkRenderer renderer, ViewerState state, ViewerActions actions, ViewerLog log, ViewerFonts fonts)
        : base(renderer)
    {
        _renderer = renderer;
        _state = state;
        _actions = actions;
        _log = log;
        _fonts = fonts;
        _glyphs = new ViewerGlyphs(fonts);
        FontPath = fonts.PrimaryPath;
        // Handles supplementary-plane pictographs (📷) inside ordinary labels for free.
        EmojiFontPath = fonts.EmojiPath;

        _liveView = new DeferredTexture(renderer);
        _thumbnail = new DeferredTexture(renderer);

        // Chain both uploads onto whatever else wants the pre-render-pass hook.
        var previous = renderer.OnPreRenderPass;
        renderer.OnPreRenderPass = cmd =>
        {
            previous?.Invoke(cmd);
            _liveView.Flush(cmd);
            _thumbnail.Flush(cmd);
        };
    }

    /// <summary>Font size in design units; the layout engine applies DPI scale on top.</summary>
    public float FontSize
    {
        get => _fontSize;
        set => _fontSize = Math.Clamp(value, 8f, 24f);
    }

    private float SmallFontSize => _fontSize - 1f;

    public void Render(RectF32 bounds)
    {
        BeginFrame();
        _glyphSlots.Clear();

        // Hand the newest rasters to the upload queue before anything draws them.
        if (_state.LiveViewFrame is { } frame) _liveView.Submit(frame);
        if (_state.LastThumbnail is { } thumb) _thumbnail.Submit(thumb);

        RenderLayout(Shell(), bounds, drawFill: PaintFill);
    }

    // ---------------------------------------------------------------- shell

    private Layout.Node Shell() =>
        Layout.Builder.Dock(
            // Centre: preview area. Docked strips are pinned around it.
            Layout.Builder.Fill(key: "preview").Stretch().Bg(ViewerTheme.Palette.ContentBg),
            Layout.Builder.Top(TopBar(), TopBarHeight),
            Layout.Builder.Bottom(StatusBar(), StatusBarHeight),
            Layout.Builder.Bottom(Panel("Log", "log"), LogPanelHeight),
            Layout.Builder.Left(Panel("Camera", "actions"), LeftPanelWidth),
            Layout.Builder.Right(Panel("Controls", "controls"), RightPanelWidth));

    /// <summary>A titled panel whose body is an app-drawn, scrollable region routed by <paramref name="key"/>.</summary>
    private Layout.Node Panel(string title, string key) =>
        Layout.Builder.Dock(
                Layout.Builder.Fill(key: key).Stretch(),
                Layout.Builder.Top(
                    Layout.Builder.Text(title, SmallFontSize, ViewerTheme.Palette.HeaderText)
                        .Pad(ViewerTheme.Metrics.Padding)
                        .Bg(ViewerTheme.Palette.HeaderBg),
                    ViewerTheme.Metrics.HeaderHeight))
            .Bg(ViewerTheme.Palette.PanelBg);

    private Layout.Node TopBar()
    {
        var connection = _state.ConnectedTo is { } device ? device.ToString() : "no transport";
        var session = _state.SessionOpen
            ? _state.RemoteMode ? "session + remote mode" : "session (no remote mode)"
            : "no session";

        return Layout.Builder.HStack(
                Layout.Builder.Text("FC.SDK Viewer", _fontSize + 1f, ViewerTheme.Accent).WAuto().HStar(),
                Layout.Builder.Text(_state.Model ?? "—", _fontSize, ViewerTheme.Palette.HeaderText).WStar(1.2f).HStar(),
                Layout.Builder.Text(_state.SerialNumber is { Length: > 0 } sn ? $"S/N {sn}" : "",
                    SmallFontSize, ViewerTheme.Palette.DimText).WStar().HStar(),
                Layout.Builder.Text(_state.BatteryPercent is { } level ? $"battery {level}%" : "",
                    SmallFontSize, BatteryColor(_state.BatteryPercent)).WStar(0.6f).HStar(),
                Layout.Builder.Text($"{connection} · {session}", SmallFontSize,
                    _state.SessionOpen ? ViewerTheme.Ok : ViewerTheme.Palette.DimText).WStar(1.6f).HStar())
            .WithGap(ViewerTheme.Metrics.Padding)
            .Pad(ViewerTheme.Metrics.Padding)
            .Bg(ViewerTheme.Palette.HeaderBg);
    }

    private static RGBAColor32 BatteryColor(byte? level) => level switch
    {
        null => ViewerTheme.Palette.DimText,
        < 20 => ViewerTheme.Error,
        < 50 => ViewerTheme.Warn,
        _ => ViewerTheme.Ok,
    };

    private Layout.Node StatusBar()
    {
        var busyColor = _state.IsBusy ? ViewerTheme.Warn : ViewerTheme.Palette.DimText;
        var logFile = _log.FilePath is { } path ? Path.GetFileName(path) : "(no log file)";

        return Layout.Builder.HStack(
                Glyph(_state.IsBusy ? _glyphs.Busy : " ", busyColor)
                    .W(Layout.Sizing.Fixed(GlyphColumnWidth)).HStar(),
                Layout.Builder.Text(_state.BusyOperation ?? "idle", SmallFontSize, busyColor).WStar(0.5f).HStar(),
                Layout.Builder.Text(_state.StatusMessage, SmallFontSize, ViewerTheme.Palette.BodyText).WStar(2.4f).HStar(),
                Layout.Builder.Text($"log → {logFile}", SmallFontSize, ViewerTheme.Palette.DimText).WStar(0.9f).HStar())
            .WithGap(ViewerTheme.Metrics.Padding)
            .Pad(ViewerTheme.Metrics.Padding)
            .Bg(ViewerTheme.Palette.HeaderBg);
    }

    // ---------------------------------------------------------------- fill routing

    private void PaintFill(Layout.Content.Fill fill, RectF32 rect)
    {
        switch (fill.Key)
        {
            case "actions": PaintScrolledRows(rect, _actionScroll, BuildActionRows(), ViewerTheme.Metrics.ButtonHeight + 2f); break;
            case "controls": PaintScrolledRows(rect, _controlScroll, BuildControlRows(), ViewerTheme.Metrics.ItemHeight + 2f); break;
            case "log": PaintScrolledRows(rect, _logScroll, BuildLogRows(), SmallFontSize + 4f); break;
            case "preview": PaintPreview(rect); break;
            case { } key when key.StartsWith("glyph:", StringComparison.Ordinal): PaintGlyph(key, rect); break;
        }
    }

    /// <summary>
    /// A single glyph, drawn with whichever installed face covers it rather than the tree's primary
    /// font. Sized and positioned by the layout engine like any other leaf — only the font differs.
    /// </summary>
    private Layout.Node Glyph(string glyph, RGBAColor32 color, float? fontSize = null)
    {
        _glyphSlots.Add((glyph, color, fontSize ?? SmallFontSize));
        return Layout.Builder.Fill(key: $"glyph:{_glyphSlots.Count - 1}");
    }

    private void PaintGlyph(string key, RectF32 rect)
    {
        if (!int.TryParse(key.AsSpan("glyph:".Length), out var index) ||
            (uint)index >= (uint)_glyphSlots.Count)
        {
            return;
        }

        var (glyph, color, fontSize) = _glyphSlots[index];
        // FontFor returns null only when nothing covers the codepoint, and ViewerGlyphs already
        // substituted ASCII in that case — so the primary is always a safe last resort.
        var font = _fonts.FontFor(char.ConvertToUtf32(glyph, 0)) ?? FontPath;

        DrawText(glyph, font, rect.X, rect.Y, rect.Width, rect.Height,
            fontSize * DpiScale, color, TextAlign.Center, TextAlign.Center);
    }

    /// <summary>
    /// Paints a virtualized row list. <see cref="ListScrollController"/> owns the "how many rows fit"
    /// arithmetic; the visible slice is then handed back to the layout engine as an ordinary VStack,
    /// so rows keep draw==hit and DPI scaling for free.
    /// </summary>
    private void PaintScrolledRows(RectF32 rect, ListScrollController scroll, List<Layout.Node> rows, float rowHeight)
    {
        scroll.SetExtent(rect, rowHeight * DpiScale, rows.Count, DpiScale);

        var first = Math.Min(scroll.FirstVisibleAtom, Math.Max(0, rows.Count - 1));
        var take = Math.Min(scroll.VisibleAtoms, Math.Max(0, rows.Count - first));

        if (take > 0)
        {
            var slice = Layout.Builder.VStack([.. rows.GetRange(first, take)])
                .WithGap(2f)
                .Pad(ViewerTheme.Metrics.Padding)
                .Stretch();

            // Forward drawFill: rows contain Glyph() Fill leaves, and a nested RenderLayout without
            // the callback drops them silently — the leaf arranges and reserves space, then nothing
            // paints it.
            RenderLayout(slice, scroll.ContentArea, drawFill: PaintFill);
        }

        scroll.DrawScrollBar(FillRect);
    }

    // ---------------------------------------------------------------- left panel

    private List<Layout.Node> BuildActionRows()
    {
        var connected = _state.IsConnected;
        var open = _state.SessionOpen;
        var remote = open && _state.RemoteMode;

        List<Layout.Node> rows =
        [
            SectionHeader("Discovery"),
            Button("Scan for cameras", "scan", _actions.Scan),
        ];

        if (_state.Devices.Count == 0)
        {
            rows.Add(Note("No cameras found yet."));
        }
        else
        {
            for (int i = 0; i < _state.Devices.Count; i++)
            {
                var index = i;
                var device = _state.Devices[i];
                var selected = i == _state.SelectedDeviceIndex;
                rows.Add(Layout.Builder.Text(device.ToString(), SmallFontSize,
                        selected ? ViewerTheme.Palette.HeaderText : ViewerTheme.Palette.DimText)
                    .Pad(4f)
                    .RowH(ViewerTheme.Metrics.ItemHeight)
                    .Bg(selected ? ViewerTheme.Palette.Selection : ViewerTheme.Palette.PanelBg)
                    .Radius(3f)
                    .Clickable(new HitResult.ListItemHit("devices", index), _ =>
                    {
                        _state.SelectedDeviceIndex = index;
                        _state.Invalidate();
                    }));
            }
        }

        rows.Add(Button(connected ? "Reconnect transport" : "Connect transport", "connect", _actions.Connect,
            enabled: _state.SelectedDeviceIndex >= 0));
        rows.Add(Button("Disconnect", "disconnect", _actions.Disconnect, enabled: connected,
            background: ViewerTheme.DangerBg));

        rows.Add(SectionHeader("Session"));
        rows.Add(Button("Open session", "open", () => _actions.OpenSession(remoteMode: true), enabled: connected && !open));
        rows.Add(Button("Open — no remote mode", "open-plain", () => _actions.OpenSession(remoteMode: false), enabled: connected && !open));
        rows.Add(Button("Close session", "close", _actions.CloseSession, enabled: open));
        rows.Add(Button(remote ? "Exit remote mode" : "Enter remote mode", "remote",
            () => _actions.SetRemoteMode(!remote), enabled: open));

        rows.Add(SectionHeader("Capture"));
        rows.Add(Button($"{_glyphs.Camera} Take picture".TrimStart(), "shoot", _actions.TakePicture, enabled: remote,
            background: ViewerTheme.ActiveBg));
        rows.Add(Button("InitiateCapture (std PTP)", "initiate", _actions.InitiateCapture, enabled: open));
        rows.Add(Button("Half-press", "halfpress", () => _actions.HalfPress(true), enabled: remote));
        rows.Add(Button("Release", "release", () => _actions.HalfPress(false), enabled: remote));
        rows.Add(Button("Cancel AF", "afcancel", _actions.CancelAutoFocus, enabled: remote));
        rows.Add(Button("Bulb start", "bulbstart", () => _actions.Bulb(true), enabled: remote));
        rows.Add(Button("Bulb end", "bulbend", () => _actions.Bulb(false), enabled: remote));
        rows.Add(Toggle("Auto-download new images", "autodl", _actions.AutoDownload,
            () => { _actions.AutoDownload = !_actions.AutoDownload; _state.Invalidate(); }));
        rows.Add(Button("Download last image", "download", _actions.DownloadLast,
            enabled: open && _state.LastObjectHandle is not null));

        rows.Add(SectionHeader("Live view"));
        rows.Add(Button(_state.LiveViewActive ? "Stop live view" : "Start live view", "lv",
            () => { if (_state.LiveViewActive) _actions.StopLiveView(); else _actions.StartLiveView(); },
            enabled: remote, background: _state.LiveViewActive ? ViewerTheme.ActiveBg : null));
        rows.Add(Button("Save one frame", "lvsave", _actions.SaveLiveViewFrame, enabled: remote));
        rows.Add(GlyphButton(_glyphs.FocusFar, "Focus far", "lensfar",
            () => _actions.DriveLens(EdsDriveLensStep.FarMedium), enabled: remote));
        rows.Add(GlyphButton(_glyphs.FocusNear, "Focus near", "lensnear",
            () => _actions.DriveLens(EdsDriveLensStep.NearMedium), enabled: remote));

        rows.Add(SectionHeader("Diagnostics"));
        rows.Add(Button("Read all properties", "readall", _actions.ReadAll, enabled: open));
        rows.Add(Button("Drain event queue", "drain", _actions.DrainEvents, enabled: open));
        rows.Add(Button("Dump properties to file", "dump", _actions.DumpProperties, enabled: open));
        rows.Add(Button("Read C.Fn block", "cfn", _actions.ReadCustomFunctions, enabled: open));
        rows.Add(Button("Report host capacity", "capacity", _actions.ReportHostCapacity, enabled: remote));
        rows.Add(Button("Keep device on", "keepalive", _actions.KeepDeviceOn, enabled: remote));
        rows.Add(Button("Lock camera UI", "uilock", () => _actions.SetUILock(true), enabled: remote));
        rows.Add(Button("Unlock camera UI", "uiunlock", () => _actions.SetUILock(false), enabled: remote));
        rows.Add(Button("Reset mirror-lockup state", "mlureset", _actions.ResetMirrorLockup, enabled: remote));

        if (_state.SupportedOperations.Count > 0)
        {
            rows.Add(SectionHeader("Transport support"));
            rows.Add(Note($"{_state.SupportedOperations.Count} PTP operations advertised"));
            rows.Add(OperationNote(0x1015, "GetDevicePropValue"));
            rows.Add(OperationNote(0x9110, "SetDevicePropValueEx"));
            rows.Add(OperationNote(0x9116, "GetEvent"));
            rows.Add(OperationNote(0x9127, "RequestDevicePropValue"));
            rows.Add(OperationNote(0x913D, "SetRequestOLCInfoGroup"));
            rows.Add(OperationNote(0x911A, "PCHDDCapacity"));
        }

        return rows;
    }

    private Layout.Node OperationNote(ushort code, string name)
    {
        var supported = _state.SupportedOperations.Contains(code);
        var color = supported ? ViewerTheme.Ok : ViewerTheme.Warn;

        return Layout.Builder.HStack(
                Glyph(supported ? _glyphs.Yes : _glyphs.No, color, SmallFontSize - 1f)
                    .W(Layout.Sizing.Fixed(GlyphColumnWidth)).HStar(),
                Layout.Builder.Text($"0x{code:X4} {name}", SmallFontSize - 1f, color).WStar().HStar())
            .WithGap(4f)
            .Pad(4f)
            .RowH(ViewerTheme.Metrics.ItemHeight - 4f);
    }

    // ---------------------------------------------------------------- right panel

    private List<Layout.Node> BuildControlRows()
    {
        List<Layout.Node> rows = [];

        foreach (var (group, controls) in CameraControls.Groups)
        {
            rows.Add(SectionHeader(group));
            foreach (var control in controls)
            {
                rows.Add(ControlRow(control));
            }
        }

        rows.Add(SectionHeader($"Event-stream property cache ({_state.RawProperties.Count})"));
        if (_state.RawProperties.Count == 0)
        {
            rows.Add(Note("Empty — open a session and read properties."));
        }
        foreach (var entry in _state.RawProperties)
        {
            var name = entry.PropertyId?.ToString() ?? "—";
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text($"0x{entry.PtpCode:X4}", SmallFontSize - 1f, ViewerTheme.Accent).WStar(0.5f).HStar(),
                    Layout.Builder.Text(name, SmallFontSize - 1f, ViewerTheme.Palette.DimText).WStar(1.3f).HStar(),
                    Layout.Builder.Text($"0x{entry.Value:X}", SmallFontSize - 1f, ViewerTheme.Palette.BodyText).WStar(0.8f).HStar(),
                    Layout.Builder.Text(entry.AllowedValues is { Length: > 0 } a ? $"{a.Length} opts" : "",
                        SmallFontSize - 1f, ViewerTheme.Palette.DimText).WStar(0.5f).HStar())
                .WithGap(4f)
                .Pad(3f)
                .RowH(ViewerTheme.Metrics.ItemHeight - 4f));
        }

        return rows;
    }

    private Layout.Node ControlRow(CameraControl control)
    {
        var reading = _state.Reading(control.PropertyId);
        var interactive = _state.SessionOpen && control.Writable && !_state.IsBusy;

        var (valueText, valueColor) = reading switch
        {
            null => ("—", ViewerTheme.Palette.DimText),
            { Ok: true } r => (control.Format(r.Value), ViewerTheme.Palette.HeaderText),
            var r => (r!.Error.ToString(), r.Error is EdsError.DevicePropNotSupported or EdsError.NotSupported
                ? ViewerTheme.Warn
                : ViewerTheme.Error),
        };

        var allowed = reading?.AllowedValues;
        var current = reading?.Ok is true ? reading.Value : 0u;

        // Read-only properties get spacers of the same width, so every value column still lines up
        // without offering a control the camera would reject.
        var stepBack = control.Writable
            ? StepButton(_glyphs.StepBack, $"{control.PropertyId}-prev", interactive,
                () => _actions.SetControl(control, control.Previous(current, allowed)))
            : StepSpacer();
        var stepForward = control.Writable
            ? StepButton(_glyphs.StepForward, $"{control.PropertyId}-next", interactive,
                () => _actions.SetControl(control, control.Next(current, allowed)))
            : StepSpacer();

        return Layout.Builder.HStack(
                Layout.Builder.Text(control.Label, SmallFontSize, ViewerTheme.Palette.BodyText).WStar(1.15f).HStar(),
                Layout.Builder.Text(valueText, SmallFontSize, valueColor).WStar(1.5f).HStar(),
                stepBack,
                stepForward)
            .WithGap(4f)
            .Pad(3f)
            .RowH(ViewerTheme.Metrics.ItemHeight);
    }

    // ---------------------------------------------------------------- log panel

    private List<Layout.Node> BuildLogRows()
    {
        var lines = _log.Snapshot();
        var rows = new List<Layout.Node>(lines.Length);

        foreach (var line in lines)
        {
            rows.Add(Layout.Builder.Text(line.Format(), SmallFontSize - 1f, ViewerTheme.LogColor(line.Level))
                .RowH(SmallFontSize + 2f));
        }

        return rows;
    }

    // ---------------------------------------------------------------- preview

    private void PaintPreview(RectF32 rect)
    {
        var liveLabel = _state.LiveViewActive
            ? $"Live view — frame {_state.LiveViewFrameCount}"
            : "Live view (stopped)";

        var captureLabel = _state.LastSavedPath is { } path
            ? $"{Path.GetFileName(path)} — {_state.LastSavedBytes:N0} bytes"
            : _state.LastFileName is { } name ? $"{name} — not downloaded" : "No capture yet";

        var tree = Layout.Builder.VStack(
                Layout.Builder.Text(liveLabel, SmallFontSize,
                    _state.LiveViewActive ? ViewerTheme.Ok : ViewerTheme.Palette.DimText)
                    .RowH(ViewerTheme.Metrics.ItemHeight),
                Layout.Builder.Fill(key: "liveimage").Stretch().Bg(ViewerTheme.Palette.PanelBg),
                Layout.Builder.Text(captureLabel, SmallFontSize, ViewerTheme.Palette.BodyText)
                    .RowH(ViewerTheme.Metrics.ItemHeight),
                Layout.Builder.Fill(key: "thumbimage").HStar(0.55f).WStar().Bg(ViewerTheme.Palette.PanelBg))
            .WithGap(ViewerTheme.Metrics.Padding)
            .Pad(ViewerTheme.Metrics.Padding)
            .Stretch();

        RenderLayout(tree, rect, drawFill: (fill, imageRect) =>
        {
            switch (fill.Key)
            {
                case "liveimage":
                    DrawRasterOrHint(_liveView, imageRect, _state.LiveViewActive
                        ? "waiting for the first frame…"
                        : "start live view to see the sensor feed");
                    break;
                case "thumbimage":
                    DrawRasterOrHint(_thumbnail, imageRect, "the embedded JPEG preview appears here after a capture");
                    break;
            }
        });
    }

    /// <summary>
    /// Draws a texture letterboxed into <paramref name="rect"/>, or a hint when there is nothing yet.
    /// The aspect fit is the one piece of arithmetic the layout engine cannot do for us — it depends
    /// on the image's own dimensions, which are data, not layout.
    /// </summary>
    private void DrawRasterOrHint(DeferredTexture texture, RectF32 rect, string hint)
    {
        if (texture.Texture is not { } tex || tex.Width <= 0 || tex.Height <= 0)
        {
            DrawText(hint, FontPath, rect.X, rect.Y, rect.Width, rect.Height,
                SmallFontSize * DpiScale, ViewerTheme.Palette.DimText, TextAlign.Center, TextAlign.Center);
            return;
        }

        var scale = MathF.Min(rect.Width / tex.Width, rect.Height / tex.Height);
        var w = tex.Width * scale;
        var h = tex.Height * scale;
        _renderer.DrawTexture(tex.DescriptorSet, rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f, w, h);
    }

    // ---------------------------------------------------------------- row primitives

    private Layout.Node SectionHeader(string title) =>
        Layout.Builder.Text(title.ToUpperInvariant(), SmallFontSize - 1f, ViewerTheme.Accent)
            .Pad(3f)
            .RowH(ViewerTheme.Metrics.ItemHeight)
            .Bg(ViewerTheme.Palette.HeaderBg)
            .Radius(3f);

    private Layout.Node Note(string text) =>
        Layout.Builder.Text(text, SmallFontSize - 1f, ViewerTheme.Palette.DimText)
            .Pad(3f)
            .RowH(ViewerTheme.Metrics.ItemHeight - 4f);

    private Layout.Node Button(string label, string action, Action onClick, bool enabled = true,
        RGBAColor32? background = null)
    {
        var bg = enabled ? background ?? ViewerTheme.ButtonBg : ViewerTheme.ButtonDisabledBg;
        var fg = enabled ? ViewerTheme.Palette.BodyText : ViewerTheme.Palette.DimText;

        return Layout.Builder.Text(label, SmallFontSize, fg, TextAlign.Center)
            .RowH(ViewerTheme.Metrics.ButtonHeight)
            .Bg(bg)
            .Radius(4f)
            .Clickable(enabled ? new HitResult.ButtonHit(action) : null, enabled ? _ => onClick() : null);
    }

    private Layout.Node Toggle(string label, string action, bool on, Action onClick) =>
        GlyphButton(on ? _glyphs.Checked : _glyphs.Unchecked, label, action, onClick,
            background: on ? ViewerTheme.ActiveBg : null);

    private static Layout.Node StepSpacer() =>
        Layout.Builder.Spacer().W(Layout.Sizing.Fixed(StepButtonWidth)).HStar();

    private Layout.Node StepButton(string glyph, string action, bool enabled, Action onClick) =>
        Glyph(glyph, enabled ? ViewerTheme.Palette.HeaderText : ViewerTheme.Palette.DimText, SmallFontSize - 1f)
            .W(Layout.Sizing.Fixed(StepButtonWidth)).HStar()
            .Bg(enabled ? ViewerTheme.ButtonBg : ViewerTheme.ButtonDisabledBg)
            .Radius(3f)
            .Clickable(enabled ? new HitResult.ButtonHit(action) : null, enabled ? _ => onClick() : null);

    /// <summary>
    /// A button whose label is preceded by a symbol glyph. Two leaves rather than one string, because
    /// the glyph and the text need different fonts (see the remarks on this type).
    /// </summary>
    private Layout.Node GlyphButton(string glyph, string label, string action, Action onClick,
        bool enabled = true, RGBAColor32? background = null, RGBAColor32? glyphColor = null)
    {
        var bg = enabled ? background ?? ViewerTheme.ButtonBg : ViewerTheme.ButtonDisabledBg;
        var fg = enabled ? ViewerTheme.Palette.BodyText : ViewerTheme.Palette.DimText;

        return Layout.Builder.HStack(
                Glyph(glyph, glyphColor ?? fg).W(Layout.Sizing.Fixed(GlyphColumnWidth)).HStar(),
                Layout.Builder.Text(label, SmallFontSize, fg).WStar().HStar())
            .WithGap(4f)
            .RowH(ViewerTheme.Metrics.ButtonHeight)
            .Bg(bg)
            .Radius(4f)
            .Clickable(enabled ? new HitResult.ButtonHit(action) : null, enabled ? _ => onClick() : null);
    }

    // ---------------------------------------------------------------- input

    public override bool HandleInput(InputEvent evt)
    {
        // Scroll controllers claim wheel/drag inside their own viewports; whichever owns the pointer
        // consumes the event, so no coordinate testing happens here.
        if (_actionScroll.HandleInput(evt) || _controlScroll.HandleInput(evt) || _logScroll.HandleInput(evt))
        {
            _state.Invalidate();
            return true;
        }

        switch (evt)
        {
            case InputEvent.MouseDown { Button: MouseButton.Left } down:
                if (HitTestAndDispatch(down.X, down.Y, down.Modifiers) is not null)
                {
                    _state.Invalidate();
                    return true;
                }
                return false;

            case InputEvent.KeyDown key:
                return HandleKey(key);
        }

        return false;
    }

    private bool HandleKey(InputEvent.KeyDown key)
    {
        switch (key.Key)
        {
            case InputKey.F5:
                _actions.ReadAll();
                return true;

            case InputKey.Space when _state.SessionOpen:
                _actions.TakePicture();
                return true;

            case InputKey.L when (key.Modifiers & InputModifier.Ctrl) != 0:
                if (_state.LiveViewActive) _actions.StopLiveView(); else _actions.StartLiveView();
                return true;

            case InputKey.D when (key.Modifiers & InputModifier.Ctrl) != 0:
                _actions.DumpProperties();
                return true;

            // Ctrl +/- resizes every label at once; the layout engine reflows around it.
            case InputKey.Plus when (key.Modifiers & InputModifier.Ctrl) != 0:
                FontSize += 1f;
                _state.Invalidate();
                return true;

            case InputKey.Minus when (key.Modifiers & InputModifier.Ctrl) != 0:
                FontSize -= 1f;
                _state.Invalidate();
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        _liveView.Dispose();
        _thumbnail.Dispose();
    }

    /// <summary>
    /// A single-slot GPU texture fed from CPU rasters. Uploads are recorded during the renderer's
    /// pre-render-pass hook and the outgoing texture is only released a frame later, once BeginFrame
    /// has waited on the fence that could still have been sampling it.
    /// </summary>
    private sealed class DeferredTexture(VkRenderer renderer) : IDisposable
    {
        private Raster? _pending;
        private Raster? _uploaded;
        private VkTexture? _texture;
        private VkTexture? _retired;

        public VkTexture? Texture => _texture;

        public void Submit(Raster raster)
        {
            // Same instance as last frame means nothing changed — skip the upload entirely.
            if (ReferenceEquals(raster, _uploaded) || ReferenceEquals(raster, _pending)) return;
            _pending = raster;
        }

        public void Flush(VkCommandBuffer cmd)
        {
            _retired?.Dispose();
            _retired = null;

            if (_pending is not { } raster) return;
            _pending = null;

            var texture = VkTexture.CreateDeferred(renderer.Context, raster.Rgba, raster.Width, raster.Height,
                VkFormat.R8G8B8A8Unorm);
            texture.RecordUpload(cmd);

            _retired = _texture;
            _texture = texture;
            _uploaded = raster;
        }

        public void Dispose()
        {
            _retired?.Dispose();
            _texture?.Dispose();
        }
    }
}
