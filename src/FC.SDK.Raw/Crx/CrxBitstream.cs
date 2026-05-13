using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace FC.SDK.Raw.Crx;

/// <summary>
/// MSB-first big-endian bit reader for the CRX entropy stream. CR3's <c>mdat</c>
/// payload uses the standard JPEG-LS / Golomb-Rice convention: bits are packed
/// MSB-first within each byte, and the byte stream is read in big-endian 32-bit
/// words so the first byte's MSB is the first bit consumed.
///
/// <para>The whole <c>mdat</c> slice for a tile is in memory before decode
/// (Phase A's <see cref="Cr3Decoder"/> hands us the full byte span), so the
/// reader is buffer-less — no 64 KB ring or I/O refill like LibRaw's
/// <c>CrxBitstream</c>. State is just (position, 32-bit accumulator, bits-left).</para>
///
/// <para>Two consumer ops:</para>
/// <list type="bullet">
/// <item><see cref="GetZeros"/> returns the unary prefix length (count of
///   consecutive 0 bits before the first 1; the terminating 1 is consumed).
///   This is the Rice quotient.</item>
/// <item><see cref="GetBits"/> reads a fixed number of bits and returns them
///   as the LSBs of a <see cref="uint"/>. Used for the Rice remainder, run-length
///   refinement bits, and the 21-bit escape payload.</item>
/// </list>
/// </summary>
internal sealed class CrxBitstream
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;
    /// <summary>32-bit accumulator. Always left-aligned: <see cref="_bitsLeft"/>
    /// most-significant bits hold the next bits to consume; the rest is undefined.</summary>
    private uint _bitData;
    private int _bitsLeft;

    public CrxBitstream(byte[] data, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || length < 0 || offset + length > data.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        _data = data;
        _position = offset;
        _end = offset + length;
        _bitData = 0;
        _bitsLeft = 0;
    }

    /// <summary>Convenience constructor consuming a span. Copies into a heap
    /// array so the bitstream can be passed across method/closure boundaries
    /// freely — the underlying byte data is small (a CRX tile is single-digit
    /// MB), and the alternative would propagate <c>ref struct</c> all the way
    /// through the decoder, which doesn't compose with our test infrastructure.</summary>
    public CrxBitstream(ReadOnlySpan<byte> data) : this(data.ToArray(), 0, data.Length) { }

    /// <summary>True once all bits AND all bytes have been consumed. The decoder
    /// uses this to detect a malformed stream early instead of failing on a
    /// silent zero-extension.</summary>
    public bool IsEndOfStream => _bitsLeft == 0 && _position >= _end;

    /// <summary>Bits consumed from the stream so far. Useful for unit-test
    /// assertions and for debugging where the decoder ended up.</summary>
    public long BitsConsumed => ((long)_position * 8) - _bitsLeft;

    /// <summary>Count consecutive 0 bits before the next 1 bit, consuming
    /// the terminating 1. The Rice quotient — equivalent to LibRaw's
    /// <c>crxBitstreamGetZeros</c>. Uses <see cref="BitOperations.LeadingZeroCount(uint)"/>
    /// rather than the Bresenham loop dcraw uses; on x64/ARM64 this compiles
    /// to a single <c>BSR</c>/<c>CLZ</c> instruction.</summary>
    public int GetZeros()
    {
        // Fast path: there's already a 1 bit in the accumulator. The CLZ on
        // the left-aligned accumulator IS the unary prefix length.
        if (_bitData != 0)
        {
            var zeros = BitOperations.LeadingZeroCount(_bitData);
            var consume = zeros + 1;
            // C# left-shift of uint by >= 32 is masked to count & 31 — i.e.
            // shifting by 32 is a no-op, leaving the terminating 1 bit
            // stranded in the accumulator. That happens when the unary
            // count is exactly 31 (the entire accumulator is "31 zeros +
            // terminator"). Handle the boundary by clearing _bitData
            // explicitly when we'd consume all 32 bits.
            _bitData = consume == 32 ? 0u : _bitData << consume;
            _bitsLeft -= consume;
            return zeros;
        }

        // Slow path: the accumulator is all zeros (either empty or genuine
        // 32-bit zero word). Add the cleared bits to the running zero count
        // and refill from the byte stream until we hit a non-zero word.
        var total = _bitsLeft;
        _bitData = 0;
        _bitsLeft = 0;
        while (_position + 4 <= _end)
        {
            var next = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_position, 4));
            _position += 4;
            if (next != 0)
            {
                var zeros = BitOperations.LeadingZeroCount(next);
                var consume = zeros + 1;
                // Same boundary as the fast path: when the terminator
                // lands at bit 0 (unary count == 31 within the new word),
                // shifting left by 32 is a no-op in C# — clear explicitly.
                _bitData = consume == 32 ? 0u : next << consume;
                _bitsLeft = 31 - zeros;
                return total + zeros;
            }
            total += 32;
        }
        // Last few bytes (<4) — load one byte at a time.
        while (_position < _end)
        {
            uint b = _data[_position++];
            if (b != 0)
            {
                // The byte b has its high bits in positions 7..0; the unary
                // 0-run continues until the first 1 bit *within the byte*,
                // measured from the top.
                var bitInByte = BitOperations.LeadingZeroCount(b) - 24;
                _bitData = (b << (25 + bitInByte)) & 0xFFFFFFFFu;
                _bitsLeft = 7 - bitInByte;
                return total + bitInByte;
            }
            total += 8;
        }
        throw new InvalidOperationException(
            "CRX bitstream underrun while scanning for unary terminator — " +
            "the input stream is truncated or the decoder state is corrupt.");
    }

    /// <summary>Read <paramref name="n"/> bits and return them as the LSBs of
    /// a <see cref="uint"/>. <paramref name="n"/> must be 1..25 (one less than
    /// a word so we can refill without overflow on the next byte boundary —
    /// CRX never reads more than 21 bits at a time so 25 is generous headroom).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBits(int n)
    {
        if (_bitsLeft >= n)
        {
            // Fast path: the accumulator has enough bits.
            var result = _bitData >> (32 - n);
            _bitData <<= n;
            _bitsLeft -= n;
            return result;
        }
        return GetBitsSlow(n);
    }

    /// <summary>Refill-and-read path. Pulled out of <see cref="GetBits"/> so
    /// the fast path stays small and inlinable.</summary>
    private uint GetBitsSlow(int n)
    {
        // Read a fresh 32-bit big-endian word if at least 4 bytes are available.
        if (_position + 4 <= _end)
        {
            var next = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_position, 4));
            _position += 4;
            // Merge the previous tail with the new bits and shift out the answer.
            // _bitsLeft of MSBs in _bitData are valid; the rest of _bitData is
            // zero-padded after the last << consumption.
            var result = ((next >> _bitsLeft) | _bitData) >> (32 - n);
            _bitData = next << (n - _bitsLeft);
            _bitsLeft = 32 - (n - _bitsLeft);
            return result;
        }

        // Tail (<4 bytes left): pull bytes one at a time into the accumulator
        // until we have enough bits. The accumulator can hold up to 32 bits,
        // so 4 byte pulls cover all cases for n <= 25.
        var bitsNeeded = n - _bitsLeft;
        var bitsLeft = _bitsLeft;
        var bitData = _bitData;
        while (bitsLeft < n)
        {
            if (_position >= _end)
                throw new InvalidOperationException(
                    "CRX bitstream underrun in GetBits — stream truncated.");
            bitsLeft += 8;
            bitData |= ((uint)_data[_position++]) << (32 - bitsLeft);
        }
        var r = bitData >> (32 - n);
        _bitData = bitData << n;
        _bitsLeft = bitsLeft - n;
        return r;
    }
}
