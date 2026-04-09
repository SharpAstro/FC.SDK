# FC.SDK — TODO

PTP codes below are verified against [libgphoto2 ptp.h](https://github.com/gphoto/libgphoto2/blob/master/camlibs/ptp2/ptp.h) unless noted otherwise.

## Long exposure noise reduction

Canon has **no direct PTP property** for LENR — it is always a Custom Function, even on newer bodies. The `EdsLongExposureNR` enum and `CanonCustomFunctionBlock` API exist for this. The C.Fn function IDs in `CanonCustomFunctionId` need on-camera validation per model.

## Live view zoom / position

libgphoto2 shows:
- 0xD1B3 = `EVFSharpness` (NOT zoom)
- 0xD1B4 = `EVFWBMode` (NOT zoom position)

LV zoom and pan are likely controlled through the live view data stream or a different mechanism (possibly SetPropValue with a Canon-specific encoding). Needs further reverse engineering.

- [ ] Evf_Zoom — mechanism unknown, no direct PTP property code
- [ ] Evf_ZoomPosition — mechanism unknown
- [ ] Evf_ImagePosition

## Unmapped properties (PTP codes known from libgphoto2)

### Live view data stream
- [ ] Evf_HistogramY/R/G/B — embedded in GetViewfinderData (0x9153) response header, not a property read
- [ ] Evf_CoordinateSystem — likely in LV data header
- [ ] Evf_ZoomRect — likely in LV data header
- [ ] Evf_RollingPitching (0x01000544) — electronic level, likely in LV data
- [ ] Evf_ClickWBCoeffs — 0xD1B5 (`EVFClickWBCoeffs`)
- [ ] Evf_VisibleRect

### Bracketing
- [ ] AEBracket value encoding — mapped to 0xD11D (`BracketMode`) but value format needs reverse engineering. Separate `AEB` property at 0xD1D9 also exists.
- [ ] FEBracket — flash exposure bracket
- [ ] ISOBracket — ISO bracket
- [ ] WhiteBalanceBracket — WB bracket
- [ ] FocusShiftSetting (0x01000457) — focus bracketing, newer bodies, complex structure

### Image / style
- [ ] Orientation
- [ ] ICCProfile
- [ ] WhiteBalanceShift — 0xD10B (AdjustA) + 0xD10C (AdjustB)
- [ ] PictureStyleDesc — individual styles at 0xD150..0xD165
- [ ] Aspect
- [ ] ManualWhiteBalanceData

### Video
- [ ] Record — start/stop recording
- [ ] FixedMovie — 0xD1C2
- [ ] MovieParam — 0xD1BE, 0xD1CA, 0xD1CC, 0xD1CD
- [ ] MovieHFRSetting
- [ ] MovSize — 0xD1BB

### Lens / focus
- [ ] LensName — mapped (0xD1D8) but returns packed data, not a string via uint32 read. Needs a string-capable read method.
- [ ] FocalLength — may be in event data or DeviceInfoEx
- [ ] LensStatus — 0xD1A8
- [ ] LensBarrelStatus — 0xD128
- [ ] FocusInfo / FocusInfoEx — 0xD1D3, complex AF point structure
- [ ] LensID — 0xD1DD

### Flash
- [ ] FlashOn
- [ ] FlashMode
- [ ] RedEye
- [ ] BuiltinStroboMode — 0xD1C6
- [ ] StroboExpComposition — 0xD1CB

### Device info
- [ ] FirmwareVersion — in PTP DeviceInfo, could parse from existing `ParseDeviceInfo`
- [ ] BodyIDEx
- [ ] ModelID — 0xD116
- [ ] ShutterCounter — 0xD1AC (shutter actuations!)
- [ ] SerialNumber — 0xD1AF (Canon-specific, vs standard PTP serial)

### Storage / card
- [ ] CurrentStorage — 0xD11E
- [ ] CurrentFolder — 0xD11F
- [ ] CardExtension — 0xD1AA
- [ ] ImageFormatCF/SD/ExtHD — 0xD121/0xD122/0xD123

### Time
- [ ] UTCTime — 0xD17C
- [ ] Timezone — 0xD17D
- [ ] Summertime — 0xD17E

### GPS
- [ ] GPSLogCtrl — 0xD14B
- [ ] GPSLogListNum — 0xD14C
- [ ] Standard PTP GPS properties (lat/lon/alt) — complex data formats

### Other interesting properties from libgphoto2
- [ ] SilentShutterSetting — 0xD129
- [ ] IntervalShootSetting — 0xD134 (intervalometer!)
- [ ] IntervalShootState — 0xD135
- [ ] HDRSetting — 0xD13D
- [ ] HighlightTonePriority — 0xD13B
- [ ] LV_AF_EyeDetect — 0xD12C
- [ ] CanonLogGamma — 0xD176
- [ ] BatteryInfo — 0xD1A6 (detailed battery data)
- [ ] ShutterReleaseCounter — 0xD167
- [ ] LCDBrightness — 0xD1DE

## Unused PTP opcodes

### Storage (standard PTP)
- [ ] GetStorageIDs (0x1004)
- [ ] GetStorageInfo (0x1005)

### Object browsing (standard PTP)
- [ ] GetNumObjects (0x1006)
- [ ] GetObjectHandles (0x1007) — browse card without WPD
- [ ] DeleteObject (0x100B)
- [ ] GetDevicePropDesc (0x1014) — property descriptor (allowed values, range)

### Canon vendor
- [ ] CanonGetStorageIDs (0x9101) / CanonGetStorageInfo (0x9102)
- [ ] CanonGetPartialObject (0x9107) — partial/resumable download
- [ ] CanonGetDeviceInfoEx (0x9108) — extended device info
- [ ] CanonGetObjectInfoEx (0x9109)
- [ ] CanonGetRemoteMode (0x9113)
- [ ] CanonChangeUSBProtocol (0x901F)
- [ ] CanonZoom (0x9158)

### 64-bit object support (video files >4GB)
- [ ] CanonGetObjectInfo64 (0x9170)
- [ ] CanonGetObject64 (0x9171)
- [ ] CanonGetPartialObject64 (0x9172)
