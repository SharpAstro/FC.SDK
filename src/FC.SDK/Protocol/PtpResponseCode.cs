namespace FC.SDK.Protocol;

internal enum PtpResponseCode : ushort
{
    Undefined = 0x0000,

    // Standard PTP
    OK = 0x2001,
    GeneralError = 0x2002,
    SessionNotOpen = 0x2003,
    InvalidTransactionID = 0x2004,
    OperationNotSupported = 0x2005,
    ParameterNotSupported = 0x2006,
    IncompleteTransfer = 0x2007,
    InvalidStorageID = 0x2008,
    InvalidObjectHandle = 0x2009,
    DevicePropNotSupported = 0x200A,
    InvalidObjectFormatCode = 0x200B,
    StoreFull = 0x200C,
    ObjectWriteProtected = 0x200D,
    StoreReadOnly = 0x200E,
    AccessDenied = 0x200F,
    NoThumbnailPresent = 0x2010,
    SelfTestFailed = 0x2011,
    PartialDeletion = 0x2012,
    StoreNotAvailable = 0x2013,
    SpecByFormatUnsupported = 0x2014,
    NoValidObjectInfo = 0x2015,
    InvalidCodeFormat = 0x2016,
    UnknownVendorCode = 0x2017,
    CaptureAlreadyTerminated = 0x2018,
    DeviceBusy = 0x2019,
    InvalidParentObject = 0x201A,
    InvalidDevicePropFormat = 0x201B,
    InvalidDevicePropValue = 0x201C,
    InvalidParameter = 0x201D,
    SessionAlreadyOpen = 0x201E,
    TransactionCancelled = 0x201F,
    DestinationUnsupported = 0x2020,

    /// <summary>
    /// Not a wire code. Synthesised locally when a command does not come back within its deadline —
    /// a camera that accepted a request and never answered, or a cable pulled mid-transaction.
    /// 0xFFFF is safe as a sentinel: PTP response codes are allocated from 0x2000/0xA000 upwards.
    /// </summary>
    LocalTimeout = 0xFFFF,

    // Canon vendor extensions
    CanonUnknownCommand = 0xA001,
    CanonOperationRefused = 0xA005,
    CanonLensCoverClosed = 0xA006,
    CanonLowBattery = 0xA101,
    CanonObjectNotReady = 0xA102,
}
