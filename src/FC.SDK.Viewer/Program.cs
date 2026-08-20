using DIR.Lib;
using FC.SDK.Viewer;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer;
using SharpAstro.AppShell;

// The viewer's reason to exist is the log file: everything it does to the camera is recorded so the
// output can be attached to a bug report. Wire the sink before anything else can fail.
var outputDirectory = args.Length > 0 && args[0] is { Length: > 0 } dir
    ? dir
    : Path.Combine(Environment.CurrentDirectory, "fc-viewer-output");

using var log = new ViewerLog(outputDirectory);
using var loggerFactory = log.CreateLoggerFactory();
var logger = loggerFactory.CreateLogger("Program");

logger.LogInformation("Output directory: {Directory}", outputDirectory);
if (log.FilePath is { } logPath) logger.LogInformation("Log file: {Path}", logPath);

var state = new ViewerState { OutputDirectory = outputDirectory };
await using var actions = new ViewerActions(state, loggerFactory);

// System fonts only, so the app ships no embedded assets. A platform UI face does not cover the
// symbol glyphs the chrome wants, so ViewerFonts resolves a text/symbol/emoji trio and checks each
// candidate's actual cmap coverage rather than assuming.
var fonts = ViewerFonts.Resolve(logger);

// --- One instance for the whole application ---
// Not keyed on anything, and for a firmer reason than tidiness: this app drives a physically
// attached camera over PTP, and two processes claiming one camera is a conflict at the driver
// level rather than a cosmetic one. It also watches for hot-plug events, which two instances
// would both react to.
//
// There is nothing to hand over -- args[0] is an output directory, not a document -- so the
// payload is empty, which is the request to activate. Claimed before the window exists so a
// second launch costs no GPU device and never touches the camera.
const string GateScope = "fc-viewer";
const string SingleInstanceEnvVar = "FC_VIEWER_SINGLE_INSTANCE";
InstanceGate? instanceGate = null;
if (!string.Equals(Environment.GetEnvironmentVariable(SingleInstanceEnvVar), "0", StringComparison.Ordinal))
{
    var gateChannel = InstanceGate.ChannelFor(GateScope);
    instanceGate = InstanceGate.TryClaim(gateChannel, logger);
    if (instanceGate is null)
    {
        if (InstanceGate.TryHandOff(gateChannel, string.Empty, TimeSpan.FromSeconds(5), logger))
        {
            logger.LogInformation("Activated the running viewer instead of starting a second one");
            return 0;
        }

        // Nobody answered. Starting anyway is the lesser evil: a launch that appears to do
        // nothing is worse than a second window, and the log above records the attempt.
        logger.LogWarning("An instance holds the viewer channel but did not answer; starting anyway");
    }
}

using var window = SdlVulkanWindow.Create("FC.SDK Viewer — Canon EOS diagnostics", 1600, 1000);
window.GetSizeInPixels(out var pixelWidth, out var pixelHeight);
logger.LogInformation("Window {Width}x{Height} px, display scale {Scale}", pixelWidth, pixelHeight, window.DisplayScale);

using var context = VulkanContext.Create(window.Instance, window.Surface, (uint)pixelWidth, (uint)pixelHeight);
using var renderer = new VkRenderer(context, (uint)pixelWidth, (uint)pixelHeight);

var widget = new ViewerWidget(renderer, state, actions, log, fonts)
{
    DpiScale = window.DisplayScale,
};

var loop = new SdlEventLoop(window, renderer)
{
    BackgroundColor = ViewerTheme.Palette.ContentBg,
};

var width = (float)pixelWidth;
var height = (float)pixelHeight;

loop.OnResize = (w, h) =>
{
    width = w;
    height = h;
    state.Invalidate();
};

loop.OnRender = () => widget.Render(new RectF32(0f, 0f, width, height));

loop.OnPointerInput = evt => widget.HandleInput(evt);

// Redraw only when something changed: the camera is the slow part, and a spinning GPU competes with
// the USB transfers we care about.
loop.CheckNeedsRedraw = () =>
{
    // Above the early return below, because this is the only callback that runs on every
    // loop iteration and a hand-off would otherwise be noticed only once something else
    // happened to need a repaint.
    while (instanceGate?.TryDequeue(out _) == true)
    {
        // Restore BEFORE raising, and not only for tidiness: SDL_RaiseWindow moves focus without
        // un-minimising, so a minimised window became the foreground window while still parked
        // off-screen at -21333,-21333 (measured). Keyboard input then goes somewhere the user
        // cannot see, which is worse than the taskbar flash this is meant to replace. Restore is a
        // no-op when the window is merely behind another one, which is the common case.
        window.Restore();
        window.Raise();
        state.Invalidate();
    }

    if (!state.NeedsRedraw && !renderer.FontAtlasDirty) return false;
    state.NeedsRedraw = false;
    return true;
};

log.LineAppended += state.Invalidate;

#if SIBLING_DEBUG_INSPECTORS
// Live UI inspector (screenshot / describe_ui / synthesized clicks over loopback). Useful beyond
// development here: it lets someone triaging a camera report drive the viewer remotely and read
// back exactly what the panels show.
//
// Gated on SIBLING_DEBUG_INSPECTORS, not plain DEBUG: DebugInspector is #if DEBUG upstream, so it
// exists only in a Debug-compiled SdlVulkan.Renderer *sibling*. The published package is built
// Release and does not contain the type at all — see src/Directory.Build.props.
// Attach turns on LayoutInspection itself once GetLayout is supplied.
using var inspector = DebugInspector.Attach(loop, new DebugInspectorOptions
{
    AppName = "FC.SDK Viewer",
    WindowTitle = () => $"FC.SDK Viewer — {state.Model ?? "no camera"}",
    GetRegions = widget.GetRegisteredRegions,
    GetLayout = widget.GetCapturedLayout,
    AppState = writer =>
    {
        writer.Set("connected", state.IsConnected);
        writer.Set("sessionOpen", state.SessionOpen);
        writer.Set("remoteMode", state.RemoteMode);
        writer.Set("liveView", state.LiveViewActive);
        writer.Set("model", state.Model);
        writer.Set("serial", state.SerialNumber);
        writer.Set("busy", state.BusyOperation);
        writer.Set("exposure", state.Exposure is { } e ? $"{e.Label} {e.Elapsed.TotalSeconds:F1}s" : null);
        writer.Set("status", state.StatusMessage);
        writer.Set("devices", state.Devices.Count);
        writer.Set("cachedProperties", state.RawProperties.Count);
    },
});
logger.LogInformation("Debug inspector listening on loopback port {Port}", inspector.Port);
#endif

// Start with a scan so the first thing on screen is the list of candidate cameras.
actions.Scan();

logger.LogInformation("Entering event loop. F5 read properties · Space capture · Ctrl+L live view · Ctrl+D dump");

try
{
    loop.Run();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Event loop terminated unexpectedly");
    throw;
}
finally
{
    widget.Dispose();
    instanceGate?.Dispose();
    logger.LogInformation("Shutting down");
}

return 0;
