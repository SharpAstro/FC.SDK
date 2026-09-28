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
/// <para>
/// <b>The widget declares; it does not dispatch.</b> Presses, the wheel, hover and tooltips are all
/// answered by the host's <see cref="InputRouter"/> from what the last paint registered: a button is
/// <c>.Clickable</c>, a control that does not apply is <c>.Disabled(reason)</c> (dimmed, press
/// swallowed, the reason shown as its tooltip), a panel body is <c>.WithScroll</c> so the wheel finds
/// it, and its empty space is <c>.Pressable</c> so a drag or the scrollbar thumb reaches the list.
/// <see cref="HandleInput"/> is left with the keys only, as the router's <c>Unhandled</c>.
/// </para>
/// <para>
/// Symbol glyphs (<c>⟵ ⟶ ✗ ⏳</c>) come from a different face than the text, and the painter now splits
/// every text leaf into per-font runs through <see cref="PixelWidgetBase{TSurface}.FontFallback"/>, so
/// they are ordinary <c>Text</c> leaves. The marks the icon family covers (step arrows, the tick, the
/// checkbox) are drawn icons instead and need no face at all.
/// </para>
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
    private readonly ViewerGlyphs _glyphs;

    // SnapToAtom everywhere: rows are painted individually into the controller's atom rects, and
    // without clipping in the painter a sub-atom scroll shift would draw partial rows outside the
    // panel. Snapped mode yields only fully-visible atoms, so nothing can escape the viewport.
    private readonly ListScrollController _actionScroll = new() { SnapToAtom = true };
    private readonly ListScrollController _controlScroll = new() { SnapToAtom = true };
    private readonly ListScrollController _logScroll = new() { Anchor = ScrollAnchor.Bottom, SnapToAtom = true };

    // Pixels captured on the action thread, uploaded through the renderer's own queue: a texture is
    // created on the render thread and its upload recorded at the start of the next frame, before any
    // render pass, and the outgoing texture's Dispose is deferred by the renderer until every frame
    // that drew it has retired.
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
        _glyphs = new ViewerGlyphs(fonts);
        FontPath = fonts.PrimaryPath;
        // The per-run chain every text leaf is measured and painted through: symbols from the symbol
        // face, emoji-presentation codepoints (📷, ⏳) from the emoji face.
        FontFallback = fonts.Fallback;
        EmojiFontPath = fonts.EmojiPath;

        _liveView = new DeferredTexture(renderer.Context);
        _thumbnail = new DeferredTexture(renderer.Context);
    }

    /// <summary>Font size in design units; the layout engine applies DPI scale on top.</summary>
    public float FontSize
    {
        get => _fontSize;
        set => _fontSize = Math.Clamp(value, 8f, 24f);
    }

    /// <summary>
    /// True while a preview upload is queued and not yet drawable. The host asks for a frame on it,
    /// since the upload lands at the start of a frame and nothing else would ask for the one that
    /// finally shows it.
    /// </summary>
    public bool IsUploading => _liveView.IsUploading || _thumbnail.IsUploading;

    private float SmallFontSize => _fontSize - 1f;


    /// <param name="bounds">The window, in pixels.</param>
    /// <param name="tooltip">The router's due tooltip, painted over everything else, or null.</param>
    public void Render(RectF32 bounds, TooltipRequest? tooltip)
    {
        BeginFrame();

        // Hand the newest rasters to the upload queue before anything draws them.
        if (_state.LiveViewFrame is { } frame) _liveView.Submit(frame);
        if (_state.CapturePreview is { } capture) _thumbnail.Submit(capture);

        // The tooltip is the overlay's top layer, so it paints after every panel, the ones drawn
        // through drawFill included, and is clamped on screen by the anchored placement.
        var root = tooltip is { } due ? Layout.Builder.Overlay(Shell(), TooltipCard(due)) : Shell();
        RenderLayout(root, bounds, drawFill: PaintFill);
    }

    // ---------------------------------------------------------------- shell

    private Layout.Node Shell() =>
        Layout.Builder.Dock(
            // Centre: preview area. Docked strips are pinned around it.
            Layout.Builder.Fill(key: "preview").Stretch().Bg(ViewerTheme.Palette.ContentBg),
            Layout.Builder.Top(TopBar(), TopBarHeight),
            Layout.Builder.Bottom(StatusBar(), StatusBarHeight),
            Layout.Builder.Bottom(Panel("Log", "log", _logScroll), LogPanelHeight),
            Layout.Builder.Left(Panel("Camera", "actions", _actionScroll), LeftPanelWidth),
            Layout.Builder.Right(Panel("Controls", "controls", _controlScroll), RightPanelWidth));

    /// <summary>
    /// A titled panel whose body is an app-drawn, scrollable region routed by <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// The body declares its list: <c>.WithScroll</c> makes the router hand it the wheel, and
    /// <c>.Pressable</c> hands it a press that no row claimed, which is what arms the drag and the
    /// scrollbar thumb. Rows register after the body (they paint inside its drawFill), so a press on a
    /// button still reaches the button; only the space between and beside rows reaches the list.
    /// <para>
    /// The padding sits OUTSIDE the Fill, so the Fill's arranged rect is exactly the list's viewport.
    /// It has to be: the painter binds that rect as the viewport and re-clamps the offset against it
    /// before drawFill runs, and when it was the unpadded body the taller rect fitted one row more,
    /// clamped the maximum to 0, and snapped every wheel and thumb drag straight back to the top.
    /// </para>
    /// </remarks>
    private Layout.Node Panel(string title, string key, ListScrollController scroll) =>
        Layout.Builder.Dock(
                Layout.Builder.HStack(
                        Layout.Builder.Fill(key: key).Stretch()
                            .WithScroll(scroll)
                            .Pressable(new HitResult.ChromeHit(), press => ScrollPress(scroll, press)))
                    .Pad(ViewerTheme.Metrics.Padding),
                Layout.Builder.Top(
                    Padded(Layout.Builder.Text(title, SmallFontSize, ViewerTheme.Palette.HeaderText),
                            ViewerTheme.Metrics.Padding)
                        .Bg(ViewerTheme.Palette.HeaderBg),
                    ViewerTheme.Metrics.HeaderHeight))
            .Bg(ViewerTheme.Palette.PanelBg);

    /// <summary>
    /// A press on a panel's empty space, offered to its list. The controller decides between the
    /// scrollbar thumb and a drag of the list itself, and the capture carries the rest of the gesture
    /// to it: the router holds a capture until the button comes up, so the list sees every move of a
    /// drag that leaves its panel.
    /// </summary>
    private static DragCapture? ScrollPress(ListScrollController scroll, PointerPress press)
    {
        if (!scroll.HandleInput(new InputEvent.MouseDown(press.X, press.Y, press.Button, press.Modifiers, press.Clicks)))
        {
            return null;
        }

        return new DragCapture(
            move => scroll.HandleInput(new InputEvent.MouseMove(move.X, move.Y, move.Button, move.Modifiers)),
            release => scroll.HandleInput(new InputEvent.MouseUp(release.X, release.Y, release.Button, release.Modifiers)));
    }

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

        // Name the running operation, and say how many clicks are waiting behind it. The queue count
        // is the answer to "I pressed that and nothing happened": every action shares one gate because
        // PTP is half-duplex, so during an exposure a click is accepted and queued rather than lost,
        // and previously nothing on screen said so.
        var queued = Volatile.Read(ref _state.QueuedOperations);
        var busyText = (_state.BusyOperation, queued) switch
        {
            (null, 0) => "idle",
            (null, var n) => $"waiting… {n} queued",
            ({ } op, 0) => op,
            ({ } op, var n) => $"{op} (+{n} queued)",
        };

        return Layout.Builder.HStack(
                Layout.Builder.Text(_state.IsBusy || queued > 0 ? _glyphs.Busy : " ", SmallFontSize, busyColor, TextAlign.Center)
                    .W(Layout.Sizing.Fixed(GlyphColumnWidth)).HStar(),
                Layout.Builder.Text(busyText, SmallFontSize, busyColor).WStar(0.5f).HStar(),
                Layout.Builder.Text(_state.StatusMessage, SmallFontSize, ViewerTheme.Palette.BodyText).WStar(2.4f).HStar(),
                Layout.Builder.Text($"log → {logFile}", SmallFontSize, ViewerTheme.Palette.DimText).WStar(0.9f).HStar())
            .WithGap(ViewerTheme.Metrics.Padding)
            .Pad(ViewerTheme.Metrics.Padding)
            .Bg(ViewerTheme.Palette.HeaderBg);
    }

    /// <summary>
    /// The due tooltip, as a card hung under the rect the pointer rests on. Anchored placement clamps it
    /// into the window, so a tooltip on the right-hand panel is not pushed off screen.
    /// </summary>
    private Layout.Node TooltipCard(TooltipRequest tooltip) =>
        Layout.Builder.AnchoredTo(tooltip.Anchor,
            Layout.Builder.HStack(
                    Layout.Builder.Text(tooltip.Text, SmallFontSize, ViewerTheme.Palette.HeaderText).WAuto().HAuto())
                .Pad(6f, 3f)
                .Bg(ViewerTheme.TooltipBg)
                .Radius(3f),
            Layout.DockSide.Bottom,
            margin: 3f);

    // ---------------------------------------------------------------- fill routing

    private void PaintFill(Layout.Content.Fill fill, RectF32 rect)
    {
        switch (fill.Key)
        {
            case "actions": PaintScrolledRows(rect, _actionScroll, BuildActionRows(), ViewerTheme.Metrics.ButtonHeight + RowGap); break;
            case "controls": PaintScrolledRows(rect, _controlScroll, BuildControlRows(), ViewerTheme.Metrics.ItemHeight + RowGap); break;
            case "log": PaintScrolledRows(rect, _logScroll, BuildLogRows(), SmallFontSize + 2f + RowGap); break;
            case "preview": PaintPreview(rect); break;
        }
    }

    /// <summary>Visual gap between virtualized rows, in design units. Part of the atom extent.</summary>
    private const float RowGap = 2f;

    /// <summary>
    /// Paints a virtualized row list. <see cref="ListScrollController"/> owns the "how many rows fit"
    /// arithmetic; each visible row is then handed back to the layout engine individually, so rows
    /// keep draw==hit and DPI scaling for free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controller models the list as UNIFORM atoms, and this method is what upholds that contract:
    /// the viewport it registers is the Fill's own rect (the panel pads around it), and every row is
    /// painted into the atom rect the controller hands back — <c>RenderLayout</c> places a root at its
    /// bounds verbatim, so a row's own <c>RowH</c> never argues with the atom extent. The previous
    /// shape (one VStack of rows with their own heterogeneous heights + an outer pad the controller
    /// never heard about) made the drawn extent disagree with the scroll math: the list's bottom edge
    /// wandered as the visible mix of row heights changed, and overflow painted over the panel below —
    /// the layout engine deliberately never clips a stack.
    /// </para>
    /// <para>
    /// Virtualized by hand rather than declared as a <c>.WithScroll</c> stack, which DIR.Lib 10.2 made a
    /// real scroll container. A container arranges every child every frame and clips at paint, and the
    /// log holds up to 4000 lines; this arranges only the rows that show. The panel's Fill still declares
    /// the list (see <see cref="Panel"/>): the painter binds its rect as the viewport, and <c>SetExtent</c>
    /// here adds the half the engine cannot know, the row count and height, against that same rect.
    /// </para>
    /// </remarks>
    private void PaintScrolledRows(RectF32 rect, ListScrollController scroll, List<Layout.Node> rows, float rowHeight)
    {
        scroll.SetExtent(rect, rowHeight * DpiScale, rows.Count, Scale);

        var gap = RowGap * DpiScale;
        foreach (var (index, atomRect) in scroll.VisibleRows())
        {
            RenderLayout(rows[index], new RectF32(atomRect.X, atomRect.Y, atomRect.Width, atomRect.Height - gap));
        }

        scroll.DrawScrollBar(FillRect, ViewerTheme.ScrollTrack, ViewerTheme.ScrollThumb);
    }

    // ---------------------------------------------------------------- left panel

    private List<Layout.Node> BuildActionRows()
    {
        var connected = _state.IsConnected;
        var open = _state.SessionOpen;
        var remote = open && _state.RemoteMode;
        var exposure = _state.Exposure;

        // Why a control does not apply, most fundamental first: a reader who is told "enter remote mode"
        // while no transport is connected is sent to a button that is itself unavailable.
        var needConnection = connected ? null : "Connect a transport first";
        var needSession = needConnection ?? (open ? null : "Open a session first");
        var needRemote = needSession ?? (remote ? null : "Enter remote mode first");
        var needIdleBody = exposure is null ? null : $"{exposure.Label} in progress";

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
                var background = selected ? ViewerTheme.Palette.Selection : ViewerTheme.Palette.PanelBg;
                rows.Add(Padded(Layout.Builder.Text(device.ToString(), SmallFontSize,
                        selected ? ViewerTheme.Palette.HeaderText : ViewerTheme.Palette.DimText), 4f)
                    .RowH(ViewerTheme.Metrics.ItemHeight)
                    .Bg(background)
                    .BgHover(ViewerTheme.Hover(background))
                    .Radius(3f)
                    .Clickable(new HitResult.ListItemHit("devices", index), _ =>
                    {
                        _state.SelectedDeviceIndex = index;
                        _state.Invalidate();
                    }));
            }
        }

        rows.Add(Button(connected ? "Reconnect transport" : "Connect transport", "connect", _actions.Connect,
            disabledReason: _state.SelectedDeviceIndex >= 0 ? null : "Select a camera first"));
        rows.Add(Button("Disconnect", "disconnect", _actions.Disconnect, needConnection,
            background: ViewerTheme.DangerBg));

        var alreadyOpen = needConnection ?? (open ? "A session is already open" : null);
        rows.Add(SectionHeader("Session"));
        rows.Add(Button("Open session", "open", () => _actions.OpenSession(remoteMode: true), alreadyOpen));
        rows.Add(Button("Open without remote mode", "open-plain", () => _actions.OpenSession(remoteMode: false), alreadyOpen));
        rows.Add(Button("Close session", "close", _actions.CloseSession, needSession));
        rows.Add(Button(remote ? "Exit remote mode" : "Enter remote mode", "remote",
            () => _actions.SetRemoteMode(!remote), needSession));

        rows.Add(SectionHeader("Capture"));

        // While the body is exposing, the shutter button becomes the exposure's own readout: the
        // release itself returned long ago, so without this there is nothing on screen to say the
        // camera is still working. Busy rather than disabled, because a second release during an
        // exposure is never what anyone meant, and greying out the most active thing the app ever does
        // says the opposite of what is happening.
        rows.Add(Button(
            exposure is null
                ? $"{_glyphs.Camera} Take picture".TrimStart()
                : $"{_glyphs.Busy} {exposure.Label}… {exposure.Elapsed.TotalSeconds:F1}s".TrimStart(),
            "shoot", _actions.TakePicture,
            needRemote,
            background: ViewerTheme.ActiveBg,
            busyReason: needIdleBody,
            tooltip: "Space"));

        rows.Add(Button("InitiateCapture (std PTP)", "initiate", _actions.InitiateCapture,
            needSession ?? needIdleBody));
        rows.Add(Button("Half-press", "halfpress", () => _actions.HalfPress(true), needRemote));
        rows.Add(Button("Release", "release", () => _actions.HalfPress(false), needRemote));
        rows.Add(Button("Cancel AF", "afcancel", _actions.CancelAutoFocus, needRemote));
        rows.Add(Button("Bulb start", "bulbstart", () => _actions.Bulb(true), needRemote ?? needIdleBody));
        rows.Add(Button("Bulb end", "bulbend", () => _actions.Bulb(false), needRemote,
            background: exposure?.Label is "Bulb" ? ViewerTheme.BusyBg : null));
        rows.Add(Layout.Builder.Checkbox("Auto-download new images", _actions.AutoDownload,
                on => { _actions.AutoDownload = on; _state.Invalidate(); },
                ViewerTheme.Checkbox, SmallFontSize, new HitResult.ButtonHit("autodl"))
            .PadX(6f)
            .RowH(ViewerTheme.Metrics.ButtonHeight)
            .Radius(4f));
        rows.Add(Button("Download last image", "download", _actions.DownloadLast,
            needSession ?? (_state.LastObjectHandle is null ? "No image announced yet" : null)));

        rows.Add(SectionHeader("Live view"));
        rows.Add(Button(_state.LiveViewActive ? "Stop live view" : "Start live view", "lv",
            () => { if (_state.LiveViewActive) _actions.StopLiveView(); else _actions.StartLiveView(); },
            needRemote, background: _state.LiveViewActive ? ViewerTheme.ActiveBg : null, tooltip: "Ctrl+L"));
        rows.Add(Button("Save one frame", "lvsave", _actions.SaveLiveViewFrame, needRemote));
        rows.Add(GlyphButton(_glyphs.FocusFar, "Focus far", "lensfar",
            () => _actions.DriveLens(EdsDriveLensStep.FarMedium), needRemote));
        rows.Add(GlyphButton(_glyphs.FocusNear, "Focus near", "lensnear",
            () => _actions.DriveLens(EdsDriveLensStep.NearMedium), needRemote));

        rows.Add(SectionHeader("Diagnostics"));
        // First in the section on purpose: it is what a bug report needs, and a reporter should not
        // have to find it among a dozen probing actions.
        rows.Add(Button("Save device report", "devreport", _actions.SaveDeviceReport, needSession));
        rows.Add(Button("Read all properties", "readall", _actions.ReadAll, needSession, tooltip: "F5"));
        rows.Add(Button("Drain event queue", "drain", _actions.DrainEvents, needSession));
        rows.Add(Button("Dump properties to file", "dump", _actions.DumpProperties, needSession, tooltip: "Ctrl+D"));
        rows.Add(Button("Read C.Fn block", "cfn", _actions.ReadCustomFunctions, needSession));
        rows.Add(Button("Report host capacity", "capacity", _actions.ReportHostCapacity, needRemote));
        rows.Add(Button("Keep device on", "keepalive", _actions.KeepDeviceOn, needRemote));
        rows.Add(Button("Lock camera UI", "uilock", () => _actions.SetUILock(true), needRemote));
        rows.Add(Button("Unlock camera UI", "uiunlock", () => _actions.SetUILock(false), needRemote));
        rows.Add(Button("Reset mirror-lockup state", "mlureset", _actions.ResetMirrorLockup, needRemote));

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

        // The tick is drawn; the cross has no member in the icon family, so it stays a text run, which
        // the fallback chain draws from whichever face has it (or "NO" where none does).
        var mark = supported
            ? Layout.Builder.Icon(Layout.IconKind.Check, color: color)
            : Layout.Builder.Text(_glyphs.No, SmallFontSize - 1f, color, TextAlign.Center);

        return Layout.Builder.HStack(
                mark.W(Layout.Sizing.Fixed(GlyphColumnWidth)).HStar(),
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

        // Busy counts as unavailable here, unlike the action buttons: a step fired while another
        // write is queued would be computed from a value the queued write is about to replace.
        var unavailable = !_state.SessionOpen ? "Open a session first"
            : _state.BusyOperation is { } busy ? $"Waiting for {busy}"
            : null;

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
            ? StepButton(Layout.IconKind.CaretLeft, $"{control.PropertyId}-prev", unavailable,
                () => _actions.SetControl(control, control.Previous(current, allowed)))
            : StepSpacer();
        var stepForward = control.Writable
            ? StepButton(Layout.IconKind.CaretRight, $"{control.PropertyId}-next", unavailable,
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

    /// <summary>
    /// One pane at a time, selected by tabs: stacked live-view + capture panes halved both images
    /// and made a dark live-view frame indistinguishable from "no frame yet". Actions auto-switch
    /// <see cref="ViewerState.PreviewMode"/> to whichever image just changed; the tabs override.
    /// </summary>
    private void PaintPreview(RectF32 rect)
    {
        var live = _state.PreviewMode == PreviewPane.LiveView;

        var label = live
            ? _state.LiveViewActive
                ? $"Live view — frame {_state.LiveViewFrameCount}"
                : "Live view (stopped)"
            // Which preview is on screen matters: the embedded thumbnail and a decoded CR2 differ by
            // a factor of ten in resolution, and "why does my capture look soft" has exactly one
            // answer worth checking first.
            : _state.LastSavedPath is { } path
                ? $"{Path.GetFileName(path)} — {_state.LastSavedBytes:N0} bytes"
                  + (_state.CapturePreviewSource is { } src ? $" — showing {src}" : "")
                : _state.LastFileName is { } name ? $"{name} — not downloaded" : "No capture yet";
        var labelColor = live
            ? _state.LiveViewActive ? ViewerTheme.Ok : ViewerTheme.Palette.DimText
            : ViewerTheme.Palette.BodyText;

        // A segmented control rather than two hand-coloured chips: the style owns which segment looks
        // chosen, the chosen one swallows its press without re-selecting, and only the other one lights
        // under the pointer.
        ReadOnlySpan<Layout.ButtonGroupOption<PreviewPane>> panes =
        [
            new(PreviewPane.LiveView, "Live view") { Hit = new HitResult.ButtonHit($"preview-{PreviewPane.LiveView}") },
            new(PreviewPane.Capture, "Last capture") { Hit = new HitResult.ButtonHit($"preview-{PreviewPane.Capture}") },
        ];

        var tree = Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.ButtonGroup(panes, _state.PreviewMode, pane =>
                            {
                                _state.PreviewMode = pane;
                                _state.Invalidate();
                            }, ViewerTheme.PreviewTabs, SmallFontSize)
                            .WAuto().HStar(),
                        Padded(Layout.Builder.Text(label, SmallFontSize, labelColor), 3f).WStar().HStar())
                    .WithGap(ViewerTheme.Metrics.Padding)
                    .RowH(ViewerTheme.Metrics.ButtonHeight),
                Layout.Builder.Fill(key: live ? "liveimage" : "thumbimage")
                    .Stretch().Bg(ViewerTheme.Palette.PanelBg))
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
                    DrawRasterOrHint(_thumbnail, imageRect, "the captured image appears here after a download");
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

    private static Layout.Node SectionHeader(string title, float fontSize) =>
        Padded(Layout.Builder.Text(title.ToUpperInvariant(), fontSize, ViewerTheme.Accent), 3f)
            .RowH(ViewerTheme.Metrics.ItemHeight)
            .Bg(ViewerTheme.Palette.HeaderBg)
            .Radius(3f);

    private Layout.Node SectionHeader(string title) => SectionHeader(title, SmallFontSize - 1f);

    private Layout.Node Note(string text) =>
        Padded(Layout.Builder.Text(text, SmallFontSize - 1f, ViewerTheme.Palette.DimText), 3f)
            .RowH(ViewerTheme.Metrics.ItemHeight - 4f);

    /// <summary>
    /// A text leaf inset by <paramref name="pad"/> on every side. Padding insets a node's CHILDREN, so on a
    /// leaf it grows the measured box and then the text is still drawn from the rect's edge: every label
    /// here that was written <c>Text(...).Pad(n)</c> sat flush against its background. A one-child stack is
    /// the node that has a child to inset; chrome (fill, hover, hit) goes on it, not on the leaf.
    /// </summary>
    private static Layout.Node Padded(Layout.Node leaf, float pad) =>
        Layout.Builder.HStack(leaf.WStar().HStar()).Pad(pad);

    /// <param name="disabledReason">
    /// Why the button does not apply right now, or null when it does. Stated rather than a bool because
    /// the painter shows it as the tooltip of the dimmed button: something a reader cannot press with no
    /// explanation teaches nothing about how to make it pressable.
    /// </param>
    /// <param name="busyReason">
    /// Why the button is un-pressable because it is already running, as opposed to one that simply
    /// does not apply. Only the latter should look inert: an exposure in progress is the most active
    /// thing the app ever does, and greying it out says the opposite. So a busy button keeps its
    /// colour-coded fill (amber) and bright label, and swallows the press with a wait cursor.
    /// </param>
    /// <param name="tooltip">Hover text for the pressable button, used for its keyboard shortcut.</param>
    private Layout.Node Button(string label, string action, Action onClick, string? disabledReason = null,
        RGBAColor32? background = null, string? busyReason = null, string? tooltip = null)
    {
        var node = Layout.Builder.Text(label, SmallFontSize, ViewerTheme.Palette.BodyText, TextAlign.Center)
            .RowH(ViewerTheme.Metrics.ButtonHeight)
            .Radius(4f);

        return Actionable(node, action, onClick, disabledReason, background ?? ViewerTheme.ButtonBg, busyReason, tooltip);
    }

    /// <summary>
    /// The three states a button can be in, stated once for every button shape: pressable (lit under the
    /// pointer), not applicable (dimmed, the press swallowed, the reason as its tooltip), or already
    /// running (its own fill, the press swallowed).
    /// </summary>
    /// <remarks>
    /// The busy case registers the hit with NO handler, which under the router is a dead region: the
    /// press is consumed and nothing runs. That is the point, since a press falling through to the panel
    /// behind would start a list drag from a button.
    /// </remarks>
    private static Layout.Node Actionable(Layout.Node node, string action, Action onClick, string? disabledReason,
        RGBAColor32 background, string? busyReason = null, string? tooltip = null)
    {
        var hit = new HitResult.ButtonHit(action);

        if (disabledReason is not null)
        {
            return node.Bg(ViewerTheme.ButtonDisabledBg).Clickable(hit).Disabled(disabledReason);
        }

        if (busyReason is not null)
        {
            return node.Bg(ViewerTheme.BusyBg).Clickable(hit, cursor: CursorKind.Wait).WithTooltip(busyReason);
        }

        node = node.Bg(background).BgHover(ViewerTheme.Hover(background)).Clickable(hit, _ => onClick());
        return tooltip is null ? node : node.WithTooltip(tooltip);
    }

    private static Layout.Node StepSpacer() =>
        Layout.Builder.Spacer().W(Layout.Sizing.Fixed(StepButtonWidth)).HStar();

    private static Layout.Node StepButton(Layout.IconKind icon, string action, string? disabledReason, Action onClick) =>
        Actionable(
            Layout.Builder.Icon(icon, color: ViewerTheme.Palette.HeaderText)
                .W(Layout.Sizing.Fixed(StepButtonWidth)).HStar()
                .Radius(3f),
            action, onClick, disabledReason, ViewerTheme.ButtonBg);

    /// <summary>
    /// A button whose label is preceded by a symbol glyph, in a column of its own so a stack of these
    /// lines up however wide each glyph is drawn.
    /// </summary>
    private Layout.Node GlyphButton(string glyph, string label, string action, Action onClick,
        string? disabledReason = null)
    {
        var fg = ViewerTheme.Palette.BodyText;

        var node = Layout.Builder.HStack(
                Layout.Builder.Text(glyph, SmallFontSize, fg, TextAlign.Center).W(Layout.Sizing.Fixed(GlyphColumnWidth)).HStar(),
                Layout.Builder.Text(label, SmallFontSize, fg).WStar().HStar())
            .WithGap(4f)
            .RowH(ViewerTheme.Metrics.ButtonHeight)
            .Radius(4f);

        return Actionable(node, action, onClick, disabledReason, ViewerTheme.ButtonBg);
    }

    // ---------------------------------------------------------------- input

    /// <summary>
    /// The keys, and only the keys: the router's <c>Unhandled</c>. Every press, move, release and wheel
    /// is answered by the router from the regions the last paint declared.
    /// </summary>
    /// <remarks>
    /// These four stay host keys rather than <c>.WithShortcut</c> on their buttons, because a declared
    /// shortcut fires only while its node is PAINTED, and the buttons live in a virtualized list: F5 would
    /// stop working the moment the Diagnostics section scrolled out of view. The buttons' tooltips name
    /// the keys instead. They were also dead until the router port: the host never set
    /// <c>OnKeyDown</c>, and the loop does not deliver keys through the pointer callback.
    /// </remarks>
    public override bool HandleInput(InputEvent evt) => evt is InputEvent.KeyDown key && HandleKey(key);

    private bool HandleKey(InputEvent.KeyDown key)
    {
        switch (key.Key)
        {
            // Not on a repeat: holding F5 would queue a full property read per auto-repeat.
            case InputKey.F5 when !key.Repeat:
                _actions.ReadAll();
                return true;

            // Not on a repeat either: a held Space is one exposure, not a burst.
            case InputKey.Space when _state.SessionOpen && !key.Repeat:
                _actions.TakePicture();
                return true;

            // A toggle, so a held chord would flip live view at the key-repeat rate.
            case InputKey.L when (key.Modifiers & InputModifier.Ctrl) != 0 && !key.Repeat:
                if (_state.LiveViewActive) _actions.StopLiveView(); else _actions.StartLiveView();
                return true;

            case InputKey.D when (key.Modifiers & InputModifier.Ctrl) != 0 && !key.Repeat:
                _actions.DumpProperties();
                return true;

            // Ctrl +/- resizes every label at once; the layout engine reflows around it. Repeats are
            // fine here: holding the chord to step through sizes is what a reader expects.
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
    /// A single-slot GPU texture fed from CPU rasters, through the renderer's own upload queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The texture is created on the render thread and handed to <c>VulkanContext.QueueTextureUpload</c>,
    /// which records its upload at the start of the next frame, before any render pass, and records it
    /// again by itself if that frame is dropped. It becomes the drawn texture once
    /// <see cref="VkTexture.IsUploaded"/> says so; until then the previous one keeps showing, so a new
    /// frame never flashes the hint.
    /// </para>
    /// <para>
    /// Retiring the outgoing texture is a plain <c>Dispose</c>: <c>VkTexture</c> defers its own destroy
    /// until every frame that could have bound it has retired, so this no longer holds a texture back
    /// by hand for a frame, which covered in-flight frames and not the one being recorded.
    /// </para>
    /// <para>
    /// One upload in flight at a time. A raster that arrives while one is queued waits for the next
    /// frame, when the host submits the newest again; replacing a texture the queue still holds would
    /// dispose it under the queue.
    /// </para>
    /// </remarks>
    private sealed class DeferredTexture(VulkanContext context) : IDisposable
    {
        private Raster? _submitted;
        private VkTexture? _shown;
        private VkTexture? _incoming;

        public VkTexture? Texture
        {
            get
            {
                Promote();
                return _shown;
            }
        }

        public bool IsUploading => _incoming is not null;

        public void Submit(Raster raster)
        {
            Promote();

            // Same instance as last time means nothing changed, so skip the upload entirely.
            if (_incoming is not null || ReferenceEquals(raster, _submitted)) return;

            var texture = VkTexture.CreateDeferred(context, raster.Rgba, raster.Width, raster.Height,
                VkFormat.R8G8B8A8Unorm);
            context.QueueTextureUpload(texture);
            _incoming = texture;
            _submitted = raster;
        }

        private void Promote()
        {
            if (_incoming is not { IsUploaded: true } ready) return;

            _shown?.Dispose();
            _shown = ready;
            _incoming = null;
        }

        public void Dispose()
        {
            _incoming?.Dispose();
            _shown?.Dispose();
        }
    }
}
