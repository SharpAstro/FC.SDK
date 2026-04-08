namespace FC.SDK.Canon;

public enum CanonEventType : uint
{
    RequestGetEvent = 0xC101,
    ObjectAddedEx = 0xC181,
    ObjectRemoved = 0xC182,
    StorageStatusChanged = 0xC184,
    PropertyChanged = 0xC189,
    AllowedValuesChanged = 0xC18A,
    CameraStatusChanged = 0xC18B,
    ShutdownTimerUpdated = 0xC18E,
    BulbExposureTime = 0xC194,
    RecordingTime = 0xC195,
    AfResult = 0xC1A3,
    ObjectAddedEx64 = 0xC1A7,
    RequestObjectTransfer64 = 0xC1A9,
}
