using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using FC.SDK.Canon;

namespace FC.SDK.Scripting;

/// <summary>
/// <see cref="IScriptCamera"/> straight over a <see cref="CanonCamera"/>, for a host with no
/// camera state of its own (a console harness). A host that runs its own live-view loop should
/// implement the interface itself, so a script's frames and the host's are the same frames.
/// </summary>
/// <remarks>
/// The session must be open and the event pump running: images are announced on the event stream,
/// and an undrained queue makes the body refuse property writes.
/// </remarks>
public sealed class CanonScriptCamera : IScriptCamera, IDisposable
{
    /// <summary>How long after the first object a second one (the JPEG of a RAW+JPEG shot) is waited for.</summary>
    private static readonly TimeSpan SecondObjectGrace = TimeSpan.FromMilliseconds(1500);

    private readonly CanonCamera _camera;
    private readonly ConcurrentQueue<uint> _objects = new();

    /// <summary>Wraps an open camera and starts listening for the images it announces.</summary>
    public CanonScriptCamera(CanonCamera camera)
    {
        _camera = camera;
        _camera.ObjectAdded += OnObjectAdded;
    }

    private void OnObjectAdded(object? sender, CanonObjectAddedEventArgs e) => _objects.Enqueue(e.ObjectHandle);

    /// <inheritdoc/>
    public async Task<uint?> ReadPropertyAsync(ushort code, CancellationToken ct)
    {
        await _camera.DrainEventsAsync(ct);
        var (err, bytes) = await _camera.GetRawPropertyBytesAsync(code, refresh: true, ct);
        return err is EdsError.OK && bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
    }

    /// <inheritdoc/>
    public Task<EdsError> WritePropertyAsync(ushort code, uint value, CancellationToken ct) =>
        _camera.SetRawPropertyAsync(code, value, ct);

    /// <inheritdoc/>
    public Task<EdsError> StartLiveViewAsync(EdsEvfOutputDevice? output, CancellationToken ct) =>
        output is { } o ? _camera.StartLiveViewAsync(o, ct) : _camera.StartLiveViewAsync(ct);

    /// <inheritdoc/>
    public Task<EdsError> StopLiveViewAsync(CancellationToken ct) => _camera.StopLiveViewAsync(ct);

    /// <inheritdoc/>
    public async Task<ScriptFrameCount> CountFramesAsync(TimeSpan window, CancellationToken ct)
    {
        var (frames, last) = (0, 0);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < window)
        {
            var (err, jpeg) = await _camera.GetLiveViewFrameAsync(ct);
            if (err is EdsError.OK && jpeg.Length > 1000 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
            {
                frames++;
                last = jpeg.Length;
            }
            else
            {
                await Task.Delay(30, ct);
            }
        }
        return new ScriptFrameCount(frames, last);
    }

    /// <inheritdoc/>
    public Task<EdsError> ReleaseAsync(CancellationToken ct) => _camera.TakePictureAsync(ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<uint>> WaitForObjectsAsync(TimeSpan window, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (_objects.IsEmpty && clock.Elapsed < window)
            await Task.Delay(100, ct);

        if (_objects.IsEmpty) return [];

        await Task.Delay(SecondObjectGrace, ct);

        var handles = new List<uint>();
        while (_objects.TryDequeue(out var handle))
        {
            handles.Add(handle);
            // Every handle, not only one that was wanted: a frame nobody releases stays in the
            // body, and a few of them are enough to make it refuse the next release.
            await _camera.TransferCompleteAsync(handle, ct);
        }
        return handles;
    }

    /// <summary>Stops listening. The camera is the caller's and stays open.</summary>
    public void Dispose() => _camera.ObjectAdded -= OnObjectAdded;
}
