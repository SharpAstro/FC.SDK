using FC.SDK.Canon;
using FC.SDK.Transport;

namespace FC.SDK.Viewer;

/// <summary>How the viewer reaches the camera.</summary>
public enum TransportKind { Wpd, Usb, WiFi }

/// <summary>A connectable camera the viewer found (or was told about).</summary>
/// <param name="Identifier">WPD device ID, WiFi host, or the USB device path.</param>
/// <param name="Usb">Populated for <see cref="TransportKind.Usb"/> so the exact device can be reopened.</param>
public sealed record DiscoveredCamera(TransportKind Transport, string Identifier, string Label, UsbDeviceInfo? Usb = null)
{
    public override string ToString() => $"[{Transport}] {Label}";
}

/// <summary>The value the viewer last read for one control, and how that read went.</summary>
public sealed record ControlReading(EdsError Error, uint Value, uint[]? AllowedValues)
{
    public bool Ok => Error is EdsError.OK;
}

/// <summary>A raster ready to be pushed to the GPU.</summary>
public sealed record Raster(int Width, int Height, byte[] Rgba);

/// <summary>
/// Which image the preview pane shows. One at a time on purpose: stacked panes halved both images
/// and made a dark live-view frame indistinguishable from "no frame yet".
/// </summary>
public enum PreviewPane { LiveView, Capture }

/// <summary>
/// Everything the UI draws. Mutated only from the render thread or from the action queue's
/// completion callbacks, both of which set <see cref="NeedsRedraw"/>.
/// </summary>
public sealed class ViewerState
{
    public List<DiscoveredCamera> Devices { get; } = [];
    public int SelectedDeviceIndex { get; set; } = -1;

    public CanonCamera? Camera { get; set; }
    public DiscoveredCamera? ConnectedTo { get; set; }
    public bool SessionOpen { get; set; }
    public bool RemoteMode { get; set; }

    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public byte? BatteryPercent { get; set; }

    /// <summary>PTP operations the body advertises — the fastest way to explain a NotSupported.</summary>
    public IReadOnlySet<ushort> SupportedOperations { get; set; } = new HashSet<ushort>();

    public Dictionary<EdsPropertyId, ControlReading> Readings { get; } = [];
    public IReadOnlyList<CanonPropertySnapshot> RawProperties { get; set; } = [];

    public bool LiveViewActive { get; set; }
    public Raster? LiveViewFrame { get; set; }
    public int LiveViewFrameCount { get; set; }

    /// <summary>
    /// The pane the preview area shows. The tabs set it directly; actions auto-switch it — starting
    /// live view selects <see cref="PreviewPane.LiveView"/>, a downloaded capture selects
    /// <see cref="PreviewPane.Capture"/> — so the image that just changed is the one on screen.
    /// </summary>
    public PreviewPane PreviewMode { get; set; } = PreviewPane.LiveView;

    public uint? LastObjectHandle { get; set; }
    public string? LastFileName { get; set; }
    public string? LastSavedPath { get; set; }
    public long LastSavedBytes { get; set; }
    /// <summary>
    /// The best preview available for the last capture, upgraded in place as better data arrives:
    /// the camera's embedded thumbnail first because it is small and immediate, then the decoded
    /// file once it is on disk and rendered.
    /// </summary>
    public Raster? CapturePreview { get; set; }

    /// <summary>Where <see cref="CapturePreview"/> came from, so the pane can say which it is.</summary>
    public string? CapturePreviewSource { get; set; }

    /// <summary>
    /// An exposure the camera is in the middle of, or null when none is outstanding.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BusyOperation"/>, and not derivable from it. A release command
    /// returns as soon as the body accepts it — for a 30-second exposure that is 29.9 seconds before
    /// anything is on the card — so the queued operation is long finished while the camera is still
    /// working. This is the span that actually matters to someone standing at the tripod: shutter
    /// released until <c>ObjectAdded</c>, or until the deadline says it is never coming.
    /// </remarks>
    public ExposureProgress? Exposure { get; set; }

    /// <summary>Name of the operation currently running, or null when idle.</summary>
    public string? BusyOperation { get; set; }

    /// <summary>
    /// Clicks accepted but still waiting for the camera, not counting the one running.
    /// </summary>
    /// <remarks>
    /// Shown because its absence read as a bug. Every action serialises on one semaphore, since PTP is
    /// half-duplex and two commands in flight is not a thing. So a click during a multi-second
    /// operation is accepted and queued, and with only <see cref="BusyOperation"/> on screen there was
    /// nothing to distinguish that from a click the app had dropped. During an exposure, which is
    /// exactly when someone is most likely to prod another button, the wait is long enough to look
    /// broken. Interlocked because Enqueue runs on the thread pool.
    /// </remarks>
    public int QueuedOperations;
    public string StatusMessage { get; set; } = "Not connected. Scan for cameras to begin.";

    /// <summary>Set by anything that changes what is on screen; cleared by the render loop.</summary>
    public volatile bool NeedsRedraw = true;

    public bool IsBusy => BusyOperation is not null;
    public bool IsConnected => Camera is not null;

    /// <summary>The directory captures, live-view stills and property dumps are written to.</summary>
    public string OutputDirectory { get; init; } = Environment.CurrentDirectory;

    public ControlReading? Reading(EdsPropertyId id) =>
        Readings.TryGetValue(id, out var reading) ? reading : null;

    public void Invalidate() => NeedsRedraw = true;
}

/// <summary>
/// An exposure in flight: what to call it, when the shutter opened, and how long to keep believing
/// an image is still coming.
/// </summary>
/// <param name="Label">Shown on the capture button while this is outstanding, e.g. "Exposing".</param>
/// <param name="StartedUtc">When the body accepted the release.</param>
/// <param name="Deadline">
/// How long to wait for <c>ObjectAdded</c> before giving up, or null to wait indefinitely. Null is
/// for bulb, where the operator decides the length and no deadline we could pick would be right.
/// Everything else needs one: a 450D with a flat battery or mirror lockup engaged answers OK to a
/// release it never performs, and without a deadline the button would sit there forever claiming an
/// exposure that ended before it started.
/// </param>
public sealed record ExposureProgress(string Label, DateTime StartedUtc, TimeSpan? Deadline)
{
    public TimeSpan Elapsed => DateTime.UtcNow - StartedUtc;

    public bool IsOverdue => Deadline is { } deadline && Elapsed > deadline;
}
