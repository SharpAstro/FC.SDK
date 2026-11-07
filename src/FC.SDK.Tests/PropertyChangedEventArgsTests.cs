using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// What <see cref="CanonCamera.PropertyChanged"/> hands a subscriber for one <c>PropValueChanged</c> record.
/// </summary>
/// <remarks>
/// Pinned because the old shape failed silently: the record's PTP code was cast straight to
/// <see cref="EdsPropertyId"/>, so <c>e.PropertyId is EdsPropertyId.MirrorLockUpState</c> in the diagnostics never
/// matched, and nothing reported it. Seen on an EOS R6 over WPD, where an ISO change arrives as 0xD103.
/// </remarks>
public class PropertyChangedEventArgsTests
{
    [Theory]
    [InlineData((ushort)0xD101, EdsPropertyId.Av)]
    [InlineData((ushort)0xD103, EdsPropertyId.ISOSpeed)]
    [InlineData((ushort)0xD1BF, EdsPropertyId.MirrorLockUpState)]
    public void A_mapped_code_names_its_EDSDK_id(ushort ptpCode, EdsPropertyId id)
    {
        var args = new CanonPropertyChangedEventArgs(ptpCode, 0x48);

        args.PropertyId.ShouldBe(id);
        args.IsMapped.ShouldBeTrue();
        args.PtpCode.ShouldBe(ptpCode);
        args.Value.ShouldBe(0x48u);
    }

    [Fact]
    public void An_unmapped_code_keeps_the_raw_code()
    {
        var args = new CanonPropertyChangedEventArgs((ushort)0xD138, 3);

        args.IsMapped.ShouldBeFalse();
        args.PtpCode.ShouldBe((ushort)0xD138);
        ((uint)args.PropertyId).ShouldBe(0xD138u);
    }

    [Fact]
    public void The_EDSDK_id_constructor_lands_on_the_same_code() =>
        new CanonPropertyChangedEventArgs(EdsPropertyId.ISOSpeed, 0x48).PtpCode.ShouldBe((ushort)0xD103);
}
