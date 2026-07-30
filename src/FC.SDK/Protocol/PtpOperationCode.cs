namespace FC.SDK.Protocol;

internal enum PtpOperationCode : ushort
{
    // Standard PTP (ISO 15740)
    GetDeviceInfo = 0x1001,
    OpenSession = 0x1002,
    CloseSession = 0x1003,
    GetStorageIDs = 0x1004,
    GetStorageInfo = 0x1005,
    GetNumObjects = 0x1006,
    GetObjectHandles = 0x1007,
    GetObjectInfo = 0x1008,
    GetObject = 0x1009,
    GetThumb = 0x100A,
    DeleteObject = 0x100B,
    InitiateCapture = 0x100E,
    GetDevicePropDesc = 0x1014,
    GetDevicePropValue = 0x1015,
    SetDevicePropValue = 0x1016,

    // Canon EOS vendor extensions (0x9xxx)
    CanonGetStorageIDs = 0x9101,
    CanonGetStorageInfo = 0x9102,
    CanonGetObjectInfo = 0x9103,
    CanonGetObject = 0x9104,
    CanonGetPartialObject = 0x9107,
    CanonGetDeviceInfoEx = 0x9108,
    CanonGetObjectInfoEx = 0x9109,

    /// <summary>
    /// Single-shot release used by DIGIC III bodies (450D, 40D, 1000D era), which do NOT implement the
    /// RemoteReleaseOn/Off pair. libgphoto2 accepts either one as proof the body supports EOS capture.
    /// </summary>
    CanonRemoteRelease = 0x910F,
    CanonSetPropValue = 0x9110,
    CanonGetRemoteMode = 0x9113,
    CanonSetRemoteMode = 0x9114,
    CanonSetEventMode = 0x9115,
    CanonGetEvent = 0x9116,
    CanonTransferComplete = 0x9117,
    CanonCancelTransfer = 0x9118,
    CanonResetTransfer = 0x9119,

    /// <summary>
    /// Reports host free space to the camera: (freeClusters, bytesPerSector, reset).
    /// Required before capturing to host, otherwise AvailableShots stays 0 and capture fails.
    /// </summary>
    CanonPcHddCapacity = 0x911A,
    CanonSetUILock = 0x911B,
    CanonResetUILock = 0x911C,
    CanonKeepDeviceOn = 0x911D,
    CanonBulbStart = 0x9125,
    CanonBulbEnd = 0x9126,

    /// <summary>
    /// Asks the camera to push a property value into the event stream. There is no EOS
    /// "get property" operation — values only ever arrive via GetEvent (0x9116).
    /// </summary>
    CanonRequestDevicePropValue = 0x9127,
    CanonRemoteReleaseOn = 0x9128,
    CanonRemoteReleaseOff = 0x9129,
    CanonResetMirrorLockupState = 0x9130,

    /// <summary>
    /// Subscribes to the "on-screen level control" info group (mask 0x1fff) delivered via
    /// OLCInfoChanged events. Without it newer bodies never report Tv/Av/ISO/AvailableShots.
    /// </summary>
    CanonSetRequestOLCInfoGroup = 0x913D,
    CanonInitiateViewfinder = 0x9151,
    CanonTerminateViewfinder = 0x9152,
    CanonGetViewfinderData = 0x9153,
    CanonDoAf = 0x9154,
    CanonDriveLens = 0x9155,
    CanonDepthOfFieldPreview = 0x9156,
    CanonAfCancel = 0x9160,
    CanonChangeUSBProtocol = 0x901F,
    CanonZoom = 0x9158,
    CanonGetObjectInfo64 = 0x9170,
    CanonGetObject64 = 0x9171,
    CanonGetPartialObject64 = 0x9172,
}
