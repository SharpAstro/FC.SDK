namespace FC.SDK.Canon;

public enum CanonEventType : uint
{
    RequestGetEvent = 0xC101,
    ObjectAddedEx = 0xC181,
    ObjectRemoved = 0xC182,
    RequestGetObjectInfoEx = 0xC183,
    StorageStatusChanged = 0xC184,
    StorageInfoChanged = 0xC185,
    RequestObjectTransfer = 0xC186,
    ObjectInfoChangedEx = 0xC187,
    ObjectContentChanged = 0xC188,

    /// <summary>
    /// A device property value changed. Payload is <c>[propCode:u32][value…]</c>.
    /// This is the ONLY way an EOS body reports property values — there is no read operation.
    /// </summary>
    PropertyChanged = 0xC189,

    /// <summary>
    /// The set of selectable values for a property changed. Payload is
    /// <c>[propCode:u32][dataType:u32][count:u32][value:u32 × count]</c>.
    /// </summary>
    AllowedValuesChanged = 0xC18A,
    CameraStatusChanged = 0xC18B,
    WillSoonShutdown = 0xC18D,
    ShutdownTimerUpdated = 0xC18E,
    RequestCancelTransfer = 0xC18F,
    StoreAdded = 0xC192,
    StoreRemoved = 0xC193,
    BulbExposureTime = 0xC194,
    RecordingTime = 0xC195,
    AfResult = 0xC1A3,

    /// <summary>
    /// Packed "optical level control" bundle carrying Tv/Av/ISO and AF state. Only sent after
    /// subscribing via SetRequestOLCInfoGroup (0x913D).
    /// </summary>
    OLCInfoChanged = 0xC1A5,
    ObjectAddedEx64 = 0xC1A7,
    ObjectInfoChangedEx64 = 0xC1A8,
    RequestObjectTransfer64 = 0xC1A9,
}
