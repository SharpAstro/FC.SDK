using FC.SDK.Canon;

namespace FC.SDK.Protocol;

internal readonly struct PtpResponse
{
    public PtpResponseCode Code { get; init; }
    public uint Param1 { get; init; }
    public uint Param2 { get; init; }
    public uint Param3 { get; init; }

    public bool IsSuccess => Code is PtpResponseCode.OK;

    public EdsError ToEdsError() => PtpErrorMapper.Map(Code);
}
