// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video;

/// <summary>One decoded picture and the moment it should be shown.</summary>
/// <remarks>
///     <para>
///         <b>One allocation, not one per plane.</b> The planes live end to end in a single array,
///         which is what lets the whole frame reach the GPU as one <c>memcpy</c> into a staging
///         buffer and one copy per plane out of it. Three arrays would mean three copies and three
///         things for a pool to keep in step.
///     </para>
///     <para>
///         <b>Rows are tightly packed.</b> <see cref="Stride" /> is the plane's width in bytes and
///         nothing else — there is no alignment padding, because the only consumer that would want it
///         is the upload path, and a staging buffer is written by this code rather than by a driver.
///         A codec that decodes into padded rows copies out; that cost is the codec's, and it is what
///         every codec does anyway when it hands a picture over.
///     </para>
///     <para>
///         <b>A frame is mutable and reused.</b> It is rented from a
///         <see cref="VideoFramePool" />, decoded into, shown, and returned. Holding one past
///         <see cref="VideoFramePool.Return" /> is a use-after-free that the type cannot prevent and
///         the player is careful about — see <c>VideoPlayer</c>, where the queue owns every frame it
///         has not handed out.
///     </para>
/// </remarks>
public sealed class VideoFrame {
    readonly int[] offsets = new int[4];
    readonly int[] strides = new int[4];

    byte[] pixels = [];

    /// <summary>What the frame holds and how to read it.</summary>
    public VideoFormat Format { get; private set; }

    /// <summary>When it should be shown, measured from the start of the stream.</summary>
    public TimeSpan Timestamp { get; set; }

    /// <summary>
    ///     How long it stays on screen, or <see cref="TimeSpan.Zero" /> if nothing said.
    /// </summary>
    /// <remarks>
    ///     WebM states a duration only for the last block of a track, so this is usually zero and the
    ///     next frame's timestamp is what actually retires this one. It is carried because a stream
    ///     that <em>does</em> state it lets the last frame of a file be held for the right length of
    ///     time rather than for a frame's guess.
    /// </remarks>
    public TimeSpan Duration { get; set; }

    /// <summary>Whether the stream could be joined at this frame.</summary>
    public bool IsKeyFrame { get; set; }

    /// <summary>How many bytes of <see cref="Pixels" /> the picture occupies.</summary>
    public int Size => Format.FrameSize;

    /// <summary>Every plane, end to end.</summary>
    /// <remarks>
    ///     Exposed as the whole buffer as well as per plane because the upload path wants exactly
    ///     this: one contiguous range to copy into a staging buffer. The array may be longer than
    ///     <see cref="Size" /> — it is pooled, and a pool that reallocated to shrink would not be one.
    /// </remarks>
    public ReadOnlySpan<byte> Pixels => pixels.AsSpan(0, Size);

    /// <summary>The same bytes, to write into.</summary>
    /// <remarks>
    ///     Internal because a decoder outside this assembly writes plane by plane and should: the
    ///     whole-buffer view is only correct for a source whose planes are already in this order and
    ///     tightly packed, which is a fact about <see cref="Codecs.UncompressedVideoCodec" /> rather
    ///     than about frames.
    /// </remarks>
    internal Span<byte> Writable => pixels.AsSpan(0, Size);

    /// <summary>Points the frame at a format, allocating if what it has will not do.</summary>
    /// <param name="format">The format to hold.</param>
    /// <exception cref="ArgumentException"><paramref name="format" /> is not a valid format.</exception>
    /// <remarks>
    ///     Public because a decoder that produces frames without a pool — a test, a tool — still has
    ///     to say what it is about to write. The pool calls this too; there is one path.
    /// </remarks>
    public void Reset(in VideoFormat format) {
        if (!format.IsValid) {
            throw new ArgumentException(
                $"A frame cannot hold a {format.Width}×{format.Height} {format.Layout} picture.",
                nameof(format)
            );
        }

        Format = format;
        Timestamp = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        IsKeyFrame = false;

        var offset = 0;

        for (var plane = 0; plane < format.PlaneCount; plane++) {
            offsets[plane] = offset;
            strides[plane] = format.PlaneWidth(plane);
            offset += strides[plane] * format.PlaneHeight(plane);
        }

        for (var plane = format.PlaneCount; plane < offsets.Length; plane++) {
            offsets[plane] = 0;
            strides[plane] = 0;
        }

        if (pixels.Length < offset) {
            pixels = new byte[offset];
        }
    }

    /// <summary>How many bytes one row of a plane takes.</summary>
    /// <param name="plane">Which plane.</param>
    /// <returns>The stride, or <c>0</c> if the plane does not exist.</returns>
    public int Stride(int plane) =>
        plane >= 0 && plane < Format.PlaneCount ? strides[plane] : 0;

    /// <summary>Where a plane starts within <see cref="Pixels" />.</summary>
    /// <param name="plane">Which plane.</param>
    /// <returns>The byte offset, or <c>0</c> if the plane does not exist.</returns>
    public int Offset(int plane) =>
        plane >= 0 && plane < Format.PlaneCount ? offsets[plane] : 0;

    /// <summary>A plane, to write into.</summary>
    /// <param name="plane">Which plane.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such plane in this format.</exception>
    public Span<byte> Plane(int plane) {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, Format.PlaneCount);

        return pixels.AsSpan(offsets[plane], strides[plane] * Format.PlaneHeight(plane));
    }

    /// <summary>One row of a plane, to write into.</summary>
    /// <param name="plane">Which plane.</param>
    /// <param name="row">Which row, from the top.</param>
    /// <returns>The row's bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such plane or no such row.</exception>
    /// <remarks>
    ///     A row at a time is how a decoder that owns padded rows copies out, and how a test states
    ///     what a picture is without arithmetic at every call site.
    /// </remarks>
    public Span<byte> Row(int plane, int row) {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, Format.PlaneCount);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Format.PlaneHeight(plane));

        return pixels.AsSpan(offsets[plane] + (row * strides[plane]), strides[plane]);
    }

    /// <summary>Copies another frame's pixels and timing into this one.</summary>
    /// <param name="source">The frame to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <remarks>
    ///     What a caller that wants to keep a frame past the queue's ownership of it does. The
    ///     alternative — handing out the pooled instance and hoping — is the bug this exists to make
    ///     unnecessary.
    /// </remarks>
    public void CopyFrom(VideoFrame source) {
        ArgumentNullException.ThrowIfNull(source);

        Reset(source.Format);
        source.Pixels.CopyTo(pixels);
        Timestamp = source.Timestamp;
        Duration = source.Duration;
        IsKeyFrame = source.IsKeyFrame;
    }

    /// <summary>Fills the frame with black.</summary>
    /// <remarks>
    ///     Black is not zero for YUV. Luma zero is black only in full range; in limited range it is
    ///     below black and clips, and chroma zero is a strong green in either. A frame cleared with
    ///     <see cref="Array.Clear(Array)" /> and shown is the green screen everybody has seen once.
    /// </remarks>
    public void Clear() {
        switch (Format.Layout) {
            case VideoPixelLayout.Bgra8:
                Plane(0).Clear();

                break;

            case VideoPixelLayout.Grey8:
                Plane(0).Fill(Format.Range == VideoColourRange.Full ? (byte)0 : (byte)16);

                break;

            case VideoPixelLayout.Yuv420Planar:
            case VideoPixelLayout.Yuv422Planar:
            case VideoPixelLayout.Yuv444Planar:
                Plane(0).Fill(Format.Range == VideoColourRange.Full ? (byte)0 : (byte)16);
                Plane(1).Fill(128);
                Plane(2).Fill(128);

                break;

            default:
                break;
        }
    }
}

/// <summary>Where frames come from, so that playing a video allocates nothing per frame.</summary>
/// <remarks>
///     <para>
///         A 1080p 4:2:0 frame is three megabytes. At sixty a second that is 180 MB/s through the
///         allocator and into gen 2, for buffers that are all exactly the same size and all dead
///         within four frames — the textbook case for pooling, and one of the few places in the
///         engine where the garbage collector would genuinely be the bottleneck.
///     </para>
///     <para>
///         <b>Not thread-safe, and it does not need to be.</b> One player owns one pool: its decode
///         thread rents and its presentation returns, and the queue between them is what crosses the
///         thread boundary. A lock here would be taken twice a frame to protect a list two threads
///         are already coordinating around.
///     </para>
/// </remarks>
public sealed class VideoFramePool {
    readonly Stack<VideoFrame> free = new();

    /// <summary>Creates a pool that will hold at most a number of frames.</summary>
    /// <param name="capacity">
    ///     How many returned frames to keep. Beyond this, a returned frame is dropped for the
    ///     collector — which is right after a format change, when the old frames are the wrong size
    ///     and keeping them would be keeping garbage warm.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity" /> is not positive.</exception>
    public VideoFramePool(int capacity = 8) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Capacity = capacity;
    }

    /// <summary>The most frames it will hold onto.</summary>
    public int Capacity { get; }

    /// <summary>How many frames are available without allocating.</summary>
    public int Available => free.Count;

    /// <summary>How many frames the pool has ever had to allocate.</summary>
    /// <remarks>
    ///     The number a test asserts on and a profile reads. Steady-state playback allocates
    ///     <see cref="Capacity" /> frames once and then never again; a number that keeps climbing
    ///     means somebody is not returning frames.
    /// </remarks>
    public int Allocations { get; private set; }

    /// <summary>Takes a frame sized for a format.</summary>
    /// <param name="format">What it has to hold.</param>
    /// <returns>The frame, with its contents undefined.</returns>
    public VideoFrame Rent(in VideoFormat format) {
        if (!free.TryPop(out var frame)) {
            frame = new VideoFrame();
            Allocations++;
        }

        frame.Reset(format);

        return frame;
    }

    /// <summary>Gives a frame back.</summary>
    /// <param name="frame">The frame. Must not be used afterwards.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    public void Return(VideoFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);

        if (free.Count < Capacity) {
            free.Push(frame);
        }
    }

    /// <summary>Drops everything it is holding.</summary>
    public void Clear() => free.Clear();
}
