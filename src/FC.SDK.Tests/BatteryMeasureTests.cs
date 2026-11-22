using System.Buffers.Binary;
using FC.SDK.Canon;
using FC.SDK.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// <see cref="CanonCamera.ReadBatteryLevelPercentAsync"/> over two scripted bodies that behave as issue #57 measured:
/// an EOS R6 whose <c>BatteryInfoEx</c> (0xD21C) holds its last measurement until something reads <c>BatteryInfo</c>
/// (0xD1A6), and an EOS 6D with no 0xD1A6 whose standard 0x5001 answers fresh when asked again.
/// </summary>
public class BatteryMeasureTests
{
    /// <summary>The 0xD1A6 values an EOS R6 sent on #57: 67 % and 49 %, LP-E6NH, 26 bytes.</summary>
    [Theory]
    [InlineData("1A000000430000000000000000000000" + "4C502D45364E48000000", 67)]
    [InlineData("1A000000310000000000000000000000" + "4C502D45364E48000000", 49)]
    public void BatteryInfo_carries_the_measured_percentage(string hex, int percent) =>
        CanonBattery.PercentFromInfo(Convert.FromHexString(hex)).ShouldBe((byte)percent);

    [Theory]
    [InlineData("")]
    [InlineData("1A000000")]                                             // no percentage word
    [InlineData("1A0000004300000000000000")]                             // the size word says 26, 12 arrived
    [InlineData("1A000000FF0000000000000000000000" + "4C502D45364E48000000")] // not a percentage
    public void Unreadable_BatteryInfo_gives_nothing(string hex) =>
        CanonBattery.PercentFromInfo(Convert.FromHexString(hex)).ShouldBeNull();

    /// <summary>
    /// An R6: <see cref="CanonCamera.BatteryLevelPercent"/> answers the stored 99 for as long as nothing makes the
    /// body measure, and a measurement reads 0xD1A6, gets 67, and brings 0xD21C to 67 with it.
    /// </summary>
    [Fact]
    public async Task On_an_R6_a_measurement_reads_0xD1A6_and_brings_0xD21C_with_it()
    {
        var body = new ScriptedBody(hasBatteryInfo: true) { StoredPercent = 99, ChargePercent = 67 };
        await using var camera = new CanonCamera(body, NullLogger<CanonCamera>.Instance, new FakeTimeProvider());

        (await camera.GetRawPropertyBytesAsync(CanonBattery.InfoExCode, refresh: true)).Error.ShouldBe(EdsError.OK);
        camera.BatteryLevelPercent.ShouldBe((byte)99, "0xD21C re-sent on request is the stored figure");

        var (error, percent) = await camera.ReadBatteryLevelPercentAsync();
        (error, percent).ShouldBe((EdsError.OK, (byte?)67));
        camera.BatteryLevelPercent.ShouldBe((byte)67);
        body.Requested.ShouldContain(CanonBattery.InfoCode);
        body.StandardBatteryReads.ShouldBe(0, "an R6 refuses 0x5001; it is not asked");
    }

    /// <summary>
    /// A 6D: no 0xD1A6, so a measurement asks for 0x5001 again, which answers the current level; the figure read on
    /// session open is replaced. A second measurement does not ask for 0xD1A6 again.
    /// </summary>
    [Fact]
    public async Task On_a_6D_a_measurement_asks_for_0x5001_again_and_stops_asking_for_0xD1A6()
    {
        var body = new ScriptedBody(hasBatteryInfo: false) { ChargePercent = 100 };
        await using var camera = new CanonCamera(body, NullLogger<CanonCamera>.Instance, new FakeTimeProvider());

        (await camera.ReadBatteryLevelPercentAsync()).ShouldBe((EdsError.OK, (byte?)100));
        body.ChargePercent = 75;
        (await camera.ReadBatteryLevelPercentAsync()).ShouldBe((EdsError.OK, (byte?)75));
        camera.BatteryLevelPercent.ShouldBe((byte)75);
        body.Requested.Count(code => code == CanonBattery.InfoCode).ShouldBe(1);
        body.StandardBatteryReads.ShouldBe(2);
    }

    /// <summary>
    /// Answers what a battery measurement uses: <c>0x9127 RequestDevicePropValue</c> (queuing the property's record),
    /// <c>0x9116 GetEvent</c> (handing the queue over) and <c>0x1015 GetDevicePropValue</c> (0x5001 on a 6D, refused
    /// otherwise). On the R6 a request for 0xD21C queues the stored figure and a request for 0xD1A6 makes it measure:
    /// it queues 0xD1A6 and 0xD21C at the charge.
    /// </summary>
    private sealed class ScriptedBody(bool hasBatteryInfo) : IMtpExtTransport
    {
        private const ushort Ok = 0x2001, NotSupported = 0x2005, PropNotSupported = 0x200A;
        private readonly List<byte[]> _queued = [];

        public int StoredPercent { get; set; }
        public int ChargePercent { get; set; }
        public List<ushort> Requested { get; } = [];
        public int StandardBatteryReads { get; private set; }

        public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandAsync(
            ushort opCode, uint[] @params, CancellationToken ct = default)
        {
            if (opCode != 0x9127) return Task.FromResult((NotSupported, Array.Empty<uint>()));
            var code = (ushort)@params[0];
            Requested.Add(code);
            if (hasBatteryInfo && code == CanonBattery.InfoExCode)
                _queued.Add(Record(code, InfoEx(StoredPercent)));
            else if (hasBatteryInfo && code == CanonBattery.InfoCode)
            {
                StoredPercent = ChargePercent;
                _queued.Add(Record(code, Info(ChargePercent)));
                _queued.Add(Record(CanonBattery.InfoExCode, InfoEx(ChargePercent)));
            }
            return Task.FromResult((Ok, Array.Empty<uint>()));
        }

        public Task<(ushort ResponseCode, uint[] ResponseParams, byte[] Data)> ExecuteCommandReadDataAsync(
            ushort opCode, uint[] @params, CancellationToken ct = default)
        {
            switch (opCode)
            {
                case 0x9116:
                    var events = _queued.SelectMany(r => r).Concat(Sentinel()).ToArray();
                    _queued.Clear();
                    return Task.FromResult((Ok, Array.Empty<uint>(), events));
                case 0x1015 when @params[0] == 0x5001 && !hasBatteryInfo:
                    StandardBatteryReads++;
                    return Task.FromResult((Ok, Array.Empty<uint>(), new[] { (byte)ChargePercent }));
                case 0x1015 when @params[0] == 0x5001:
                    StandardBatteryReads++;
                    return Task.FromResult((NotSupported, Array.Empty<uint>(), Array.Empty<byte>()));
                case 0x1015:
                    return Task.FromResult((PropNotSupported, Array.Empty<uint>(), Array.Empty<byte>()));
                default:
                    return Task.FromResult((NotSupported, Array.Empty<uint>(), Array.Empty<byte>()));
            }
        }

        private static byte[] Record(ushort code, byte[] value)
        {
            var record = new byte[12 + value.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 0xC189);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), code);
            value.CopyTo(record, 12);
            return record;
        }

        private static byte[] Sentinel() => [8, 0, 0, 0, 0, 0, 0, 0];

        private static byte[] InfoEx(int percent) =>
            Convert.FromHexString($"01000000{percent:X2}0000000000004C502D45364E48000000");

        private static byte[] Info(int percent) =>
            Convert.FromHexString($"1A000000{percent:X2}0000000000000000000000" + "4C502D45364E48000000");

        public Task<(ushort ResponseCode, uint[] ResponseParams)> ExecuteCommandWriteDataAsync(
            ushort opCode, uint[] @params, byte[] data, CancellationToken ct = default) =>
            Task.FromResult((NotSupported, Array.Empty<uint>()));

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool IsConnected => true;
        public string DeviceId => "scripted";
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<int> ReceiveEventAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
