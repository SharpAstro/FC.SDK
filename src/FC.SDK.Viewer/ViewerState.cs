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

    public uint? LastObjectHandle { get; set; }
    public string? LastFileName { get; set; }
    public string? LastSavedPath { get; set; }
    public long LastSavedBytes { get; set; }
    public Raster? LastThumbnail { get; set; }

    /// <summary>Name of the operation currently running, or null when idle.</summary>
    public string? BusyOperation { get; set; }
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
