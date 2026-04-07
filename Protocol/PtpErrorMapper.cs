using FC.SDK.Canon;

namespace FC.SDK.Protocol;

internal static class PtpErrorMapper
{
    public static EdsError Map(PtpResponseCode code) => code switch
    {
        PtpResponseCode.OK => EdsError.OK,
        PtpResponseCode.GeneralError => EdsError.InternalError,
        PtpResponseCode.SessionNotOpen => EdsError.SessionNotOpen,
        PtpResponseCode.InvalidTransactionID => EdsError.InvalidTransactionId,
        PtpResponseCode.OperationNotSupported => EdsError.NotSupported,
        PtpResponseCode.IncompleteTransfer => EdsError.IncompleteTransfer,
        PtpResponseCode.InvalidStorageID => EdsError.InvalidStorageId,
        PtpResponseCode.DevicePropNotSupported => EdsError.DevicePropNotSupported,
        PtpResponseCode.InvalidObjectFormatCode => EdsError.InvalidObjectFormatCode,
        PtpResponseCode.DeviceBusy => EdsError.DeviceBusy,
        PtpResponseCode.InvalidParameter => EdsError.InvalidParameter,
        PtpResponseCode.SessionAlreadyOpen => EdsError.SessionAlreadyOpen,
        PtpResponseCode.TransactionCancelled => EdsError.TransactionCancelled,
        PtpResponseCode.CaptureAlreadyTerminated => EdsError.CaptureAlreadyTerminated,
        PtpResponseCode.CanonUnknownCommand => EdsError.UnknownCommand,
        PtpResponseCode.CanonOperationRefused => EdsError.OperationRefused,
        PtpResponseCode.CanonLensCoverClosed => EdsError.LensCoverClose,
        PtpResponseCode.CanonLowBattery => EdsError.LowBattery,
        PtpResponseCode.CanonObjectNotReady => EdsError.ObjectNotReady,
        _ => EdsError.UnexpectedException,
    };
}
