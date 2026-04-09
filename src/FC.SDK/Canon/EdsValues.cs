namespace FC.SDK.Canon;

/// <summary>Canon ISO speed values. Set via <see cref="EdsPropertyId.ISOSpeed"/>.</summary>
public enum EdsISOSpeed : uint
{
    Auto = 0x00,
    ISO_50 = 0x40,
    ISO_100 = 0x48,
    ISO_125 = 0x4B,
    ISO_160 = 0x4D,
    ISO_200 = 0x50,
    ISO_250 = 0x53,
    ISO_320 = 0x55,
    ISO_400 = 0x58,
    ISO_500 = 0x5B,
    ISO_640 = 0x5D,
    ISO_800 = 0x60,
    ISO_1000 = 0x63,
    ISO_1250 = 0x65,
    ISO_1600 = 0x68,
    ISO_2000 = 0x6B,
    ISO_2500 = 0x6D,
    ISO_3200 = 0x70,
    ISO_4000 = 0x73,
    ISO_5000 = 0x75,
    ISO_6400 = 0x78,
    ISO_8000 = 0x7B,
    ISO_10000 = 0x7D,
    ISO_12800 = 0x80,
    ISO_16000 = 0x83,
    ISO_20000 = 0x85,
    ISO_25600 = 0x88,
    ISO_51200 = 0x90,
    ISO_102400 = 0x98,
    Unknown = 0xFFFFFFFF,
}

/// <summary>Canon shutter speed (Tv) values. Set via <see cref="EdsPropertyId.Tv"/>.</summary>
public enum EdsTv : uint
{
    Bulb = 0x04,
    Tv_30s = 0x10,
    Tv_25s = 0x13,
    Tv_20s = 0x15,
    Tv_15s = 0x18,
    Tv_13s = 0x1B,
    Tv_10s = 0x1D,
    Tv_8s = 0x20,
    Tv_6s = 0x23,
    Tv_5s = 0x25,
    Tv_4s = 0x28,
    Tv_3s2 = 0x2B,  // 3.2s
    Tv_2s5 = 0x2D,  // 2.5s
    Tv_2s = 0x30,
    Tv_1s6 = 0x33,   // 1.6s
    Tv_1s3 = 0x35,   // 1.3s
    Tv_1s = 0x38,
    Tv_0s8 = 0x3B,   // 0.8s
    Tv_0s6 = 0x3D,   // 0.6s
    Tv_0s5 = 0x40,   // 0.5s = 1/2
    Tv_0s4 = 0x43,   // 0.4s
    Tv_0s3 = 0x45,   // 0.3s = 1/3
    Tv_1_4 = 0x48,
    Tv_1_5 = 0x4B,
    Tv_1_6 = 0x4D,
    Tv_1_8 = 0x50,
    Tv_1_10 = 0x53,
    Tv_1_13 = 0x55,
    Tv_1_15 = 0x58,
    Tv_1_20 = 0x5B,
    Tv_1_25 = 0x5D,
    Tv_1_30 = 0x60,
    Tv_1_40 = 0x63,
    Tv_1_50 = 0x65,
    Tv_1_60 = 0x68,
    Tv_1_80 = 0x6B,
    Tv_1_100 = 0x6D,
    Tv_1_125 = 0x70,
    Tv_1_160 = 0x73,
    Tv_1_200 = 0x75,
    Tv_1_250 = 0x78,
    Tv_1_320 = 0x7B,
    Tv_1_400 = 0x7D,
    Tv_1_500 = 0x80,
    Tv_1_640 = 0x83,
    Tv_1_800 = 0x85,
    Tv_1_1000 = 0x88,
    Tv_1_1250 = 0x8B,
    Tv_1_1600 = 0x8D,
    Tv_1_2000 = 0x90,
    Tv_1_2500 = 0x93,
    Tv_1_3200 = 0x95,
    Tv_1_4000 = 0x98,
    Tv_1_5000 = 0x9B,
    Tv_1_6400 = 0x9D,
    Tv_1_8000 = 0xA0,
    Unknown = 0xFFFFFFFF,
}

/// <summary>Canon aperture (Av) values. Set via <see cref="EdsPropertyId.Av"/>.</summary>
public enum EdsAv : uint
{
    Av_1_0 = 0x08,
    Av_1_1 = 0x0B,
    Av_1_2 = 0x0D,
    Av_1_4 = 0x10,
    Av_1_6 = 0x13,
    Av_1_8 = 0x15,
    Av_2_0 = 0x18,
    Av_2_2 = 0x1B,
    Av_2_5 = 0x1D,
    Av_2_8 = 0x20,
    Av_3_2 = 0x23,
    Av_3_5 = 0x25,
    Av_4_0 = 0x28,
    Av_4_5 = 0x2B,
    Av_5_0 = 0x2D,
    Av_5_6 = 0x30,
    Av_6_3 = 0x33,
    Av_7_1 = 0x35,
    Av_8_0 = 0x38,
    Av_9_0 = 0x3B,
    Av_10 = 0x3D,
    Av_11 = 0x40,
    Av_13 = 0x43,
    Av_14 = 0x45,
    Av_16 = 0x48,
    Av_18 = 0x4B,
    Av_20 = 0x4D,
    Av_22 = 0x50,
    Av_25 = 0x53,
    Av_29 = 0x55,
    Av_32 = 0x58,
    Av_36 = 0x5B,
    Av_40 = 0x5D,
    Av_45 = 0x60,
    Av_51 = 0x63,
    Av_57 = 0x65,
    Av_64 = 0x68,
    Unknown = 0xFFFFFFFF,
}

/// <summary>Canon shooting mode. Read via <see cref="EdsPropertyId.AEMode"/>.</summary>
public enum EdsAEMode : uint
{
    Program = 0x00,
    Tv = 0x01,
    Av = 0x02,
    Manual = 0x03,
    Bulb = 0x04,
    A_DEP = 0x05,
    DEP = 0x06,
    Custom = 0x07,
    Lock = 0x08,
    SceneIntelligentAuto = 0x16,
    Unknown = 0xFFFFFFFF,
}

/// <summary>Capture destination. Set via <see cref="EdsPropertyId.SaveTo"/>.</summary>
public enum EdsSaveTo : uint
{
    Camera = 1,
    Host = 2,
    Both = 3,
}

/// <summary>Canon metering mode. Set via <see cref="EdsPropertyId.MeteringMode"/>.</summary>
public enum EdsMeteringMode : uint
{
    Spot = 1,
    Evaluative = 3,
    Partial = 4,
    CenterWeightedAverage = 5,
    Unknown = 0xFFFFFFFF,
}

/// <summary>Canon drive mode. Set via <see cref="EdsPropertyId.DriveMode"/>.</summary>
public enum EdsDriveMode : uint
{
    SingleShooting = 0x00,
    ContinuousShooting = 0x01,
    Video = 0x02,
    HighSpeedContinuous = 0x04,
    LowSpeedContinuous = 0x05,
    SilentSingleShooting = 0x06,
    Timer_10sec_RemoteControl = 0x07,
    Timer_2sec_RemoteControl = 0x10,
    SingleSilentShooting = 0x13,
    ContinuousSilentShooting = 0x14,
    Unknown = 0xFFFFFFFF,
}

/// <summary>Canon AF mode. Set via <see cref="EdsPropertyId.AFMode"/>.</summary>
public enum EdsAFMode : uint
{
    OneShot = 0,
    AIServo = 1,
    AIFocus = 2,
    ManualFocus = 3,
    Unknown = 0xFFFFFFFF,
}

/// <summary>Canon white balance. Set via <see cref="EdsPropertyId.WhiteBalance"/>.</summary>
/// <summary>Canon white balance. Set via <see cref="EdsPropertyId.WhiteBalance"/>.</summary>
public enum EdsWhiteBalance : uint
{
    Auto = 0,
    Daylight = 1,
    Cloudy = 2,
    Tungsten = 3,
    Fluorescent = 4,
    Flash = 5,
    Manual = 6,
    Shade = 8,
    ColorTemperature = 9,
}

/// <summary>Depth of field preview during live view. Set via <see cref="EdsPropertyId.Evf_DepthOfFieldPreview"/>.</summary>
public enum EdsEvfDepthOfFieldPreview : uint
{
    Off = 0,
    On = 1,
}

/// <summary>
/// Long exposure noise reduction. Canon has no direct PTP property for this —
/// it is always a Custom Function. Use <see cref="CanonCustomFunctionBlock"/> to read/write.
/// Values: 0=Off, 1=Auto, 2=On.
/// </summary>
public enum EdsLongExposureNR : uint
{
    Off = 0,
    Auto = 1,
    On = 2,
}

/// <summary>High ISO speed noise reduction. PTP 0xD178.</summary>
public enum EdsHighIsoNR : uint
{
    Standard = 0,
    Low = 1,
    Strong = 2,
    Disable = 3,
}
