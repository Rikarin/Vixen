// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace Vixen.Video.Containers;

/// <summary>Pulls tracks and blocks out of a Matroska or WebM segment.</summary>
/// <remarks>
///     <para>
///         <b>Why the container is ours.</b> The same argument
///         <c>Vixen.Audio.Codecs.OggReader</c> makes, one layer up: a video codec takes a packet and
///         knows nothing about where it came from, so somebody has to turn a file into packets, and
///         a managed reader for a format this small is cheaper in every sense than a native demuxer
///         plus a binary per RID. WebM is also the container that is unencumbered, that browsers
///         play, and that <c>docs/plan/08</c> names for the importer.
///     </para>
///     <para>
///         <b>MP4 is not here.</b> Doc 08 names it too, and it is a genuinely larger job — a box
///         parser, sample tables, chunk offsets, and an <c>stsd</c> that hands out codec
///         configuration in a different shape per codec. It is additive: the seam every layer above
///         this one uses is <see cref="IVideoStreamDecoder" />, and an <c>Mp4Demuxer</c> plugs into
///         the same place. What is here first is the format that the free codecs actually ship in.
///     </para>
///     <para>
///         <b>Unknown elements are skipped, not rejected.</b> That is the property EBML exists for,
///         and it is why this reader — which understands about twenty elements out of several
///         hundred — plays files written by muxers that did not exist when it was written.
///     </para>
///     <para>
///         <b>A caller must drain every track it reads from.</b> Blocks arrive interleaved, so
///         asking for a video packet decodes the audio packets that were in front of it and holds
///         them. Reading video and never reading audio grows that queue for the length of the film.
///         A track that is never asked for at all costs nothing — its blocks are skipped where they
///         lie.
///     </para>
/// </remarks>
public sealed class MatroskaDemuxer : IDisposable {
    readonly List<(TimeSpan Time, long Position, int Track)> cues = [];
    readonly Stack<MatroskaPacket> free = new();
    readonly Stream? owned;
    readonly Dictionary<int, Queue<MatroskaPacket>> pending = [];
    readonly EbmlReader reader;
    readonly List<MatroskaPacket> scratch = [];
    readonly List<MatroskaTrack> tracks = [];

    long clusterEnd = -1;
    long clusterTimestamp;
    bool ended;
    long firstClusterPosition = -1;
    bool inCluster;
    EbmlElement? pushedBack;
    long segmentDataStart;
    long segmentEnd = long.MaxValue;
    long timestampScale = 1_000_000;

    /// <summary>Opens a file.</summary>
    /// <param name="path">Where it is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not a Matroska segment.</exception>
    public MatroskaDemuxer(string path)
        : this(OpenFile(path), leaveOpen: false) { }

    /// <summary>Opens a stream.</summary>
    /// <param name="stream">The bytes. Must be seekable for <see cref="SeekTo" /> to work.</param>
    /// <param name="leaveOpen">Whether the stream outlives this demuxer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream" /> is null.</exception>
    /// <exception cref="InvalidDataException">It is not a Matroska segment.</exception>
    public MatroskaDemuxer(Stream stream, bool leaveOpen = false) {
        ArgumentNullException.ThrowIfNull(stream);

        reader = new EbmlReader(stream);
        owned = leaveOpen ? null : stream;

        try {
            ReadHeader();
        } catch {
            owned?.Dispose();

            throw;
        }
    }

    /// <summary>Every track the segment declares, in the order it declared them.</summary>
    public IReadOnlyList<MatroskaTrack> Tracks => tracks;

    /// <summary>How long the segment is, or zero if it did not say.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>What the file called itself — <c>webm</c> or <c>matroska</c>.</summary>
    public string DocType { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="SeekTo" /> works.</summary>
    public bool CanSeek => reader.Stream.CanSeek;

    /// <summary>Whether the segment declared a cue index, and can therefore seek without scanning.</summary>
    public bool HasCues => cues.Count > 0;

    /// <inheritdoc />
    public void Dispose() {
        owned?.Dispose();
        pending.Clear();
        free.Clear();
    }

    /// <summary>Finds the first track of a kind.</summary>
    /// <param name="kind">What to look for.</param>
    /// <returns>The track, or <see langword="null" /> if the segment has none.</returns>
    /// <remarks>
    ///     First rather than best, because a WebM with two video tracks is a thing that exists in the
    ///     specification and essentially never in the wild, and choosing between them is a policy a
    ///     player should state rather than a demuxer should guess.
    /// </remarks>
    public MatroskaTrack? FindTrack(MatroskaTrackKind kind) {
        foreach (var track in tracks) {
            if (track.Kind == kind) {
                return track;
            }
        }

        return null;
    }

    /// <summary>Starts buffering a track's blocks, before anybody reads one.</summary>
    /// <param name="trackNumber">Which track.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Order matters, and this is what makes it not matter.</b> Blocks are skipped unless
    ///         somebody is reading their track, and a track becomes read on its first
    ///         <see cref="ReadPacket" /> — so opening a video decoder, decoding a second of picture,
    ///         and only then opening the audio decoder would silently lose the first second of sound.
    ///         Both stream decoders call this when they are constructed, so a track is followed from
    ///         the moment something exists that could read it.
    ///     </para>
    ///     <para>
    ///         Idempotent, and following a track that has no blocks costs a dictionary entry.
    ///     </para>
    /// </remarks>
    public void Follow(int trackNumber) {
        if (!pending.ContainsKey(trackNumber)) {
            pending[trackNumber] = new Queue<MatroskaPacket>();
        }
    }

    /// <summary>Reads the next packet of a track.</summary>
    /// <param name="trackNumber">Which track.</param>
    /// <returns>The packet, or <see langword="null" /> at the end of the segment.</returns>
    /// <exception cref="InvalidDataException">The segment is damaged.</exception>
    /// <remarks>
    ///     Asking for a track is what registers it: from this call on, its blocks are buffered rather
    ///     than skipped. A track nobody asks for costs one skip per block.
    /// </remarks>
    public MatroskaPacket? ReadPacket(int trackNumber) {
        if (!pending.TryGetValue(trackNumber, out var queue)) {
            queue = new Queue<MatroskaPacket>();
            pending[trackNumber] = queue;
        }

        while (queue.Count == 0) {
            if (!Pump()) {
                return null;
            }
        }

        return queue.Dequeue();
    }

    /// <summary>Gives a packet back.</summary>
    /// <param name="packet">The packet. Must not be read afterwards.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packet" /> is null.</exception>
    public void Release(MatroskaPacket packet) {
        ArgumentNullException.ThrowIfNull(packet);

        if (free.Count < 64) {
            free.Push(packet);
        }
    }

    /// <summary>Moves to the cluster covering a position.</summary>
    /// <param name="position">Where to go.</param>
    /// <param name="trackNumber">
    ///     Which track's cues to consult. A cue index usually covers only the video track, so a seek
    ///     for audio lands on the same cluster and the audio is decoded forward from there.
    /// </param>
    /// <exception cref="NotSupportedException">The stream cannot seek.</exception>
    /// <remarks>
    ///     <para>
    ///         Lands on a cluster boundary at or before the position, and everything buffered is
    ///         dropped. What it does <em>not</em> do is land on a frame: the caller decodes forward
    ///         from the cluster to the frame it wanted, because only the codec knows which of the
    ///         frames in between it needed to see.
    ///     </para>
    ///     <para>
    ///         With no cue index this rewinds to the first cluster and scans. That is the same trade
    ///         <c>OpusStreamDecoder</c> makes and for the same reason: bisecting a file on block
    ///         timestamps is real code for something that happens at a loop point, and scanning is
    ///         correct at every position.
    ///     </para>
    /// </remarks>
    public void SeekTo(TimeSpan position, int trackNumber) {
        if (!CanSeek) {
            throw new NotSupportedException("The stream cannot seek, so neither can the demuxer.");
        }

        var target = firstClusterPosition >= 0 ? firstClusterPosition : segmentDataStart;
        var best = TimeSpan.Zero;

        foreach (var (time, at, track) in cues) {
            if (time <= position && time >= best && (track == trackNumber || track == 0)) {
                best = time;
                target = at;
            }
        }

        foreach (var queue in pending.Values) {
            while (queue.TryDequeue(out var packet)) {
                Release(packet);
            }
        }

        reader.Position = target;
        inCluster = false;
        clusterEnd = -1;
        clusterTimestamp = 0;
        pushedBack = null;
        ended = false;
    }

    /// <summary>Turns a count of timestamp ticks into a duration.</summary>
    /// <param name="ticks">The count.</param>
    /// <returns>The duration.</returns>
    internal TimeSpan Scaled(long ticks) => TimeSpan.FromTicks(ticks * timestampScale / 100);

    static FileStream OpenFile(string path) {
        ArgumentNullException.ThrowIfNull(path);

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    }

    static bool IsSegmentLevel(uint id) =>
        id is MatroskaIds.Cluster or MatroskaIds.Cues or MatroskaIds.Tracks or MatroskaIds.Info
            or MatroskaIds.SeekHead or MatroskaIds.Segment or MatroskaIds.EbmlHeader;

    // ── Header ──────────────────────────────────────────────────────────────────────────────

    void ReadHeader() {
        if (!reader.TryReadElement(out var header) || header.Id != MatroskaIds.EbmlHeader) {
            throw new InvalidDataException("The stream does not begin with an EBML header, so it is not Matroska.");
        }

        ReadEbmlHeader(header.Size);

        // Everything between the header and the segment is other people's business — a second EBML
        // header, padding, a stray void — and skipping it is what the format asks of a reader.
        while (reader.TryReadElement(out var element)) {
            if (element.Id == MatroskaIds.Segment) {
                segmentDataStart = reader.Position;
                segmentEnd = element.IsUnknownSize
                    ? reader.Length >= 0 ? reader.Length : long.MaxValue
                    : segmentDataStart + element.Size;

                ScanSegment();

                return;
            }

            reader.Skip(element.Size);
        }

        throw new InvalidDataException("The stream has an EBML header and no segment.");
    }

    void ReadEbmlHeader(long size) {
        var end = reader.Position + size;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            if (element.Id == MatroskaIds.DocType) {
                DocType = reader.ReadString(element.Size);
            } else {
                reader.Skip(element.Size);
            }
        }

        if (DocType is not ("webm" or "matroska")) {
            throw new InvalidDataException(
                $"The file calls itself '{DocType}', which is an EBML document this reader does not know."
            );
        }
    }

    /// <summary>Walks the segment's top level for the parts a player needs before it starts.</summary>
    /// <remarks>
    ///     A seekable stream is scanned to the end, because the cue index is almost always written
    ///     after the clusters it indexes — a muxer does not know where a cluster is until it has
    ///     written it — and skipping a cluster is a seek. A non-seekable stream stops at the first
    ///     cluster and plays from there without cues, which is what streaming means.
    /// </remarks>
    void ScanSegment() {
        while (reader.Position < segmentEnd && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.Info:
                    ReadInfo(element.Size);

                    break;

                case MatroskaIds.Tracks:
                    ReadTracks(element.Size);

                    break;

                case MatroskaIds.Cues:
                    ReadCues(element.Size);

                    break;

                case MatroskaIds.Cluster:
                    if (firstClusterPosition < 0) {
                        firstClusterPosition = reader.Position - element.HeaderSize;
                    }

                    if (!CanSeek) {
                        // Already inside the first cluster's header. Hand it to the pump rather than
                        // losing it: there is no going back on a stream that cannot seek.
                        inCluster = true;
                        clusterTimestamp = 0;
                        clusterEnd = element.IsUnknownSize ? long.MaxValue : reader.Position + element.Size;

                        return;
                    }

                    reader.Skip(element.Size);

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }

        if (tracks.Count == 0) {
            throw new InvalidDataException("The segment declares no tracks.");
        }

        reader.Position = firstClusterPosition >= 0 ? firstClusterPosition : segmentDataStart;
    }

    void ReadInfo(long size) {
        var end = reader.Position + size;
        var duration = 0d;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.TimestampScale:
                    timestampScale = (long)reader.ReadUnsigned(element.Size);

                    if (timestampScale <= 0) {
                        throw new InvalidDataException("The segment's timestamp scale is zero, so no block has a time.");
                    }

                    break;

                case MatroskaIds.Duration:
                    duration = reader.ReadFloat(element.Size);

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }

        // Stated in timestamp ticks and as a float, which is the one place Matroska mixes the two.
        Duration = duration > 0 ? Scaled((long)duration) : TimeSpan.Zero;
    }

    void ReadTracks(long size) {
        var end = reader.Position + size;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            if (element.Id == MatroskaIds.TrackEntry) {
                ReadTrackEntry(element.Size);
            } else {
                reader.Skip(element.Size);
            }
        }
    }

    void ReadTrackEntry(long size) {
        var end = reader.Position + size;
        var track = new MatroskaTrack();

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.TrackNumber:
                    track.Number = (int)reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.TrackUid:
                    track.Uid = reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.TrackType:
                    track.Kind = reader.ReadUnsigned(element.Size) switch {
                        1 => MatroskaTrackKind.Video,
                        2 => MatroskaTrackKind.Audio,
                        _ => MatroskaTrackKind.Other
                    };

                    break;

                case MatroskaIds.CodecId:
                    track.CodecId = reader.ReadString(element.Size);

                    break;

                case MatroskaIds.CodecPrivate: {
                    var bytes = new byte[element.Size];

                    reader.ReadBytes(bytes);
                    track.CodecPrivate = bytes;

                    break;
                }

                case MatroskaIds.DefaultDuration:
                    track.DefaultDuration = TimeSpan.FromTicks((long)reader.ReadUnsigned(element.Size) / 100);

                    break;

                case MatroskaIds.TrackVideo:
                    ReadVideoSettings(track, element.Size);

                    break;

                case MatroskaIds.TrackAudio:
                    ReadAudioSettings(track, element.Size);

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }

        if (track.DisplayWidth == 0) {
            track.DisplayWidth = track.PixelWidth;
        }

        if (track.DisplayHeight == 0) {
            track.DisplayHeight = track.PixelHeight;
        }

        tracks.Add(track);
    }

    void ReadVideoSettings(MatroskaTrack track, long size) {
        var end = reader.Position + size;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.PixelWidth:
                    track.PixelWidth = (int)reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.PixelHeight:
                    track.PixelHeight = (int)reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.DisplayWidth:
                    track.DisplayWidth = (int)reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.DisplayHeight:
                    track.DisplayHeight = (int)reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.ColourSpace:
                    track.ColourSpace = reader.ReadString(element.Size);

                    break;

                case MatroskaIds.Colour:
                    ReadColour(track, element.Size);

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }
    }

    void ReadColour(MatroskaTrack track, long size) {
        var end = reader.Position + size;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.MatrixCoefficients:
                    // The ITU-T H.273 register. 1 is BT.709; 5 and 6 are the two spellings of BT.601,
                    // which differ in a way no eight-bit conversion can see. Anything else — BT.2020,
                    // YCoCg — belongs with a wider pixel format than this module has.
                    track.ColourMatrix = reader.ReadUnsigned(element.Size) switch {
                        5 or 6 => VideoColourMatrix.Bt601,
                        _ => VideoColourMatrix.Bt709
                    };

                    break;

                case MatroskaIds.ColourRange:
                    track.ColourRange = reader.ReadUnsigned(element.Size) == 2
                        ? VideoColourRange.Full
                        : VideoColourRange.Limited;

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }
    }

    void ReadAudioSettings(MatroskaTrack track, long size) {
        var end = reader.Position + size;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.SamplingFrequency:
                    track.SampleRate = (int)Math.Round(reader.ReadFloat(element.Size));

                    break;

                case MatroskaIds.Channels:
                    track.Channels = (int)reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.BitDepth:
                    track.BitDepth = (int)reader.ReadUnsigned(element.Size);

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }
    }

    void ReadCues(long size) {
        var end = reader.Position + size;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            if (element.Id == MatroskaIds.CuePoint) {
                ReadCuePoint(element.Size);
            } else {
                reader.Skip(element.Size);
            }
        }
    }

    void ReadCuePoint(long size) {
        var end = reader.Position + size;
        var time = TimeSpan.Zero;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.CueTime:
                    time = Scaled((long)reader.ReadUnsigned(element.Size));

                    break;

                case MatroskaIds.CueTrackPositions:
                    ReadCueTrackPositions(element.Size, time);

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }
    }

    void ReadCueTrackPositions(long size, TimeSpan time) {
        var end = reader.Position + size;
        var track = 0;
        var position = -1L;

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.CueTrack:
                    track = (int)reader.ReadUnsigned(element.Size);

                    break;

                case MatroskaIds.CueClusterPosition:
                    // Relative to the start of the segment's data, not to the file. Every
                    // implementation gets this wrong once and lands in the middle of a block.
                    position = segmentDataStart + (long)reader.ReadUnsigned(element.Size);

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }

        if (position >= 0) {
            cues.Add((time, position, track));
        }
    }

    // ── Blocks ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Advances the reader until it has produced at least one packet, or the segment ends.</summary>
    /// <returns>Whether anything was produced.</returns>
    bool Pump() {
        while (true) {
            if (ended) {
                return false;
            }

            if (inCluster) {
                if (reader.Position >= clusterEnd) {
                    inCluster = false;

                    continue;
                }

                if (!reader.TryReadElement(out var element)) {
                    ended = true;

                    return false;
                }

                switch (element.Id) {
                    case MatroskaIds.ClusterTimestamp:
                        clusterTimestamp = (long)reader.ReadUnsigned(element.Size);

                        break;

                    case MatroskaIds.SimpleBlock:
                        if (ReadBlock(element.Size, simple: true)) {
                            return true;
                        }

                        break;

                    case MatroskaIds.BlockGroup:
                        if (ReadBlockGroup(element.Size)) {
                            return true;
                        }

                        break;

                    default:
                        if (IsSegmentLevel(element.Id)) {
                            // An unknown-size cluster ends where the next thing that cannot be inside
                            // one begins. Hand the element back to the segment level rather than
                            // rewinding, which a non-seekable stream could not do.
                            pushedBack = element;
                            inCluster = false;
                        } else {
                            reader.Skip(element.Size);
                        }

                        break;
                }

                continue;
            }

            EbmlElement next;

            if (pushedBack is { } held) {
                next = held;
                pushedBack = null;
            } else if (reader.Position >= segmentEnd || !reader.TryReadElement(out next)) {
                ended = true;

                return false;
            }

            switch (next.Id) {
                case MatroskaIds.Cluster:
                    inCluster = true;
                    clusterTimestamp = 0;
                    clusterEnd = next.IsUnknownSize ? long.MaxValue : reader.Position + next.Size;

                    break;

                case MatroskaIds.Cues when cues.Count == 0:
                    ReadCues(next.Size);

                    break;

                default:
                    reader.Skip(next.Size);

                    break;
            }
        }
    }

    /// <summary>Reads a block group, which is a block plus the things a simple block cannot say.</summary>
    /// <returns>Whether it produced a packet for a track somebody is reading.</returns>
    bool ReadBlockGroup(long size) {
        var end = reader.Position + size;
        var duration = TimeSpan.Zero;
        var hasDuration = false;
        var referenced = false;

        scratch.Clear();

        while (reader.Position < end && reader.TryReadElement(out var element)) {
            switch (element.Id) {
                case MatroskaIds.Block:
                    ReadBlockInto(element.Size, scratch);

                    break;

                case MatroskaIds.BlockDuration:
                    duration = Scaled((long)reader.ReadUnsigned(element.Size));
                    hasDuration = true;

                    break;

                case MatroskaIds.ReferenceBlock:
                    // Its presence is the whole signal: a block that references another is not one
                    // the stream can be joined at. The value — where the reference points — matters
                    // only to a codec that reorders, which is not this reader's business.
                    reader.Skip(element.Size);
                    referenced = true;

                    break;

                default:
                    reader.Skip(element.Size);

                    break;
            }
        }

        var produced = false;

        foreach (var packet in scratch) {
            packet.IsKeyFrame = !referenced;

            if (hasDuration) {
                packet.Duration = duration;
            }

            produced |= Enqueue(packet);
        }

        scratch.Clear();

        return produced;
    }

    /// <summary>Reads a simple block straight into the pending queues.</summary>
    bool ReadBlock(long size, bool simple) {
        scratch.Clear();
        ReadBlockInto(size, scratch, simple);

        var produced = false;

        foreach (var packet in scratch) {
            produced |= Enqueue(packet);
        }

        scratch.Clear();

        return produced;
    }

    /// <summary>Parses one block's header and its frames.</summary>
    /// <param name="size">The block's payload size.</param>
    /// <param name="destination">Where the frames go. Not enqueued yet — a block group patches them.</param>
    /// <param name="simple">Whether the flags byte carries the keyframe bit.</param>
    void ReadBlockInto(long size, List<MatroskaPacket> destination, bool simple = false) {
        var start = reader.Position;
        var trackNumber = (int)reader.ReadSize(out _);

        Span<byte> header = stackalloc byte[3];

        reader.ReadBytes(header);

        var relative = BinaryPrimitives.ReadInt16BigEndian(header);
        var flags = header[2];
        var payload = size - (reader.Position - start);

        if (payload < 0) {
            throw new InvalidDataException($"The block at {start} declares a size smaller than its own header.");
        }

        var track = TrackOf(trackNumber);

        // A track nobody has asked for is skipped where it lies. This is the difference between
        // playing the video of a file with six audio languages and demuxing all six.
        if (!pending.ContainsKey(trackNumber)) {
            reader.Skip(payload);

            return;
        }

        var timestamp = Scaled(clusterTimestamp + relative);
        var keyFrame = simple && (flags & 0x80) != 0;
        var lacing = (flags >> 1) & 0x03;

        if (lacing == 0) {
            var packet = Take(trackNumber, timestamp, keyFrame, track);

            reader.ReadBytes(packet.Allocate((int)payload));
            destination.Add(packet);

            return;
        }

        ReadLaced(lacing, payload, trackNumber, timestamp, keyFrame, track, destination);
    }

    /// <summary>Splits a laced block into its frames.</summary>
    /// <remarks>
    ///     <para>
    ///         Lacing exists because an Opus packet is twenty milliseconds and a block header is five
    ///         bytes: without it, a stereo stream would spend a measurable fraction of its bitrate on
    ///         framing. Every muxer uses it for audio, so a demuxer that skipped it would play no
    ///         WebM ever produced.
    ///     </para>
    ///     <para>
    ///         Three schemes, because the format grew: Xiph's is Ogg's segment-table trick, fixed
    ///         needs no sizes at all, and EBML's stores the first size and then the differences —
    ///         which is what makes a lace of near-equal packets cost a byte apiece.
    ///     </para>
    ///     <para>
    ///         <b>The frames of a lace share one timestamp.</b> They are spread across the block by
    ///         the track's default duration, because that is the only rate anybody has stated; a
    ///         track with no default duration gets frames that all claim the block's time, which is
    ///         what every player does with them.
    ///     </para>
    /// </remarks>
    void ReadLaced(
        int lacing,
        long payload,
        int trackNumber,
        TimeSpan timestamp,
        bool keyFrame,
        MatroskaTrack? track,
        List<MatroskaPacket> destination
    ) {
        var before = reader.Position;
        var count = reader.Stream.ReadByte() + 1;

        if (count <= 0) {
            throw new InvalidDataException("A laced block claims no frames.");
        }

        var sizes = new int[count];
        var stated = 0;

        switch (lacing) {
            case 1: // Xiph: each size is a run of 255s and a remainder.
                for (var index = 0; index < count - 1; index++) {
                    var size = 0;

                    while (true) {
                        var part = reader.Stream.ReadByte();

                        if (part < 0) {
                            throw new InvalidDataException("A Xiph lace ran past the end of the stream.");
                        }

                        size += part;

                        if (part != 255) {
                            break;
                        }
                    }

                    sizes[index] = size;
                    stated += size;
                }

                break;

            case 2: // Fixed: every frame is the same size, and none of them are written down.
                break;

            default: { // EBML: the first size, then signed differences.
                var size = (int)reader.ReadSize(out _);

                sizes[0] = size;
                stated = size;

                for (var index = 1; index < count - 1; index++) {
                    size += (int)ReadSignedVint();
                    sizes[index] = size;
                    stated += size;
                }

                break;
            }
        }

        var remaining = payload - (reader.Position - before);

        if (lacing == 2) {
            if (remaining % count != 0) {
                throw new InvalidDataException(
                    $"A fixed lace of {count} frames has {remaining} bytes, which does not divide."
                );
            }

            for (var index = 0; index < count; index++) {
                sizes[index] = (int)(remaining / count);
            }
        } else {
            sizes[count - 1] = (int)(remaining - stated);

            if (sizes[count - 1] < 0) {
                throw new InvalidDataException("A lace states frame sizes larger than the block holds.");
            }
        }

        var step = track?.DefaultDuration ?? TimeSpan.Zero;

        for (var index = 0; index < count; index++) {
            var packet = Take(trackNumber, timestamp + (step * index), keyFrame, track);

            reader.ReadBytes(packet.Allocate(sizes[index]));
            destination.Add(packet);
        }
    }

    long ReadSignedVint() {
        var value = reader.ReadSize(out var length);

        // The difference is stored biased so that it can be negative without a sign bit: subtract
        // half of what the width can hold. A one-byte difference of 0x3F is zero, not 63.
        return value - ((1L << ((7 * length) - 1)) - 1);
    }

    MatroskaTrack? TrackOf(int number) {
        foreach (var track in tracks) {
            if (track.Number == number) {
                return track;
            }
        }

        return null;
    }

    MatroskaPacket Take(int trackNumber, TimeSpan timestamp, bool keyFrame, MatroskaTrack? track) {
        if (!free.TryPop(out var packet)) {
            packet = new MatroskaPacket();
        }

        packet.TrackNumber = trackNumber;
        packet.Timestamp = timestamp;
        packet.Duration = track?.DefaultDuration ?? TimeSpan.Zero;
        packet.IsKeyFrame = keyFrame;

        return packet;
    }

    bool Enqueue(MatroskaPacket packet) {
        if (pending.TryGetValue(packet.TrackNumber, out var queue)) {
            queue.Enqueue(packet);

            return true;
        }

        Release(packet);

        return false;
    }
}
