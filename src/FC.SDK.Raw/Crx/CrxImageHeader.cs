namespace FC.SDK.Raw.Crx;

/// <summary>
/// Resolved CR3 sensor-track header. Carries everything the CRX decoder
/// needs to consume the compressed payload — image + tile geometry from
/// the CMP1 box, codec parameters (encType, levels), and the per-track
/// mdat byte range from <c>co64</c>+<c>stsz</c>.
///
/// <para>The <see cref="EncType"/>/<see cref="Levels"/> pair determines
/// the decode path:</para>
/// <list type="bullet">
/// <item>encType=0, levels=0: pure Rice-coded LL band, no wavelet (Canon
///   "RAW" mode on EOS M-series).</item>
/// <item>encType=0, levels=1..3: HQ lossless with N-level CDF 5/3 wavelet
///   decomposition (Canon "CRAW" lossless mode on EOS M-series).</item>
/// <item>encType=3: cRAW lossy with QP modulation + colour transform
///   (newer bodies, full-frame R-series). Phase B does not decode this
///   path — needs a fixture.</item>
/// <item>encType=1: monochrome (rare specialty bodies).</item>
/// </list>
/// </summary>
internal sealed record CrxImageHeader(
    int Width,
    int Height,
    int TileWidth,
    int TileHeight,
    int BitDepth,
    int PlaneCount,
    CanonCfaPattern Cfa,
    int EncType,
    int Levels,
    long MdatOffset,
    int MdatSize,
    // Byte size of the structural-marker zone (0xFF01 tile / 0xFF02 plane /
    // 0xFF03 subband) at the start of this track's mdat payload. The
    // compressed subband bytes live at [MdatOffset + MdatHdrSize, MdatOffset
    // + MdatSize). Read from CMP1[+28..+32] big-endian per LibRaw's
    // crxParseImageHeader.
    int MdatHdrSize)
{
    /// <summary>Number of horizontal tiles. CR3 splits the sensor into
    /// rectangular tiles when <see cref="TileWidth"/> &lt; <see cref="Width"/>;
    /// 1 or 2 tiles is typical (Canon's encoder picks 2 horizontal tiles
    /// for full-frame raws).</summary>
    public int TileColumns => (Width + TileWidth - 1) / TileWidth;

    /// <summary>Number of vertical tiles. Almost always 1 in practice.</summary>
    public int TileRows => (Height + TileHeight - 1) / TileHeight;
}
