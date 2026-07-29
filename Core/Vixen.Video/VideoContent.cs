// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Video;

/// <summary>Turns the address in a <see cref="VideoClip" /> into bytes to demux.</summary>
/// <remarks>
///     <para>
///         <b>A seam rather than a dependency, and the dependency graph is the reason.</b> Nothing in
///         <c>Core/</c> references <c>Vixen.Assets</c> — the asset system is a leaf that a game and the
///         tools use, not something the modules underneath it are built on — so a video module that
///         called <c>AssetManager.Load</c> would be the first, and would make every game that plays a
///         sting link the addressables system to do it.
///     </para>
///     <para>
///         ⚠ <b>The stream has to be seekable.</b> A demuxer looks for the segment's cues at the end
///         of the file and comes back, and every seek — including the one a loop is — moves it. A
///         forward-only stream is legal to hand over and fails on the first of those, which is at
///         <c>MatroskaDemuxer</c>'s construction rather than at playback, so it fails early and says
///         so.
///     </para>
///     <para>
///         The two implementations here need nothing: <see cref="FileVideoContentSource" /> for a
///         video that shipped as a file, which is how a video usually ships, and
///         <see cref="DelegatedVideoContentSource" /> for everything else. An addressable one is
///         three lines over <c>AssetManager.Open</c> and belongs to whoever has both.
///     </para>
/// </remarks>
public interface IVideoContentSource {
    /// <summary>Opens a video's bytes.</summary>
    /// <param name="address">What <see cref="VideoClip.ContainerAddress" /> said.</param>
    /// <returns>A seekable stream the caller owns and disposes.</returns>
    /// <exception cref="VideoContentMissingException">There is nothing at that address.</exception>
    Stream Open(string address);

    /// <summary>Whether there is anything at an address, without opening it.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether there is.</returns>
    /// <remarks>
    ///     Separate from <see cref="Open" /> for the reason <c>IBundleSource.IsAvailable</c> is: a
    ///     title that wants to fall back to a still image has to be able to ask before it commits to
    ///     a cutscene.
    /// </remarks>
    bool Exists(string address);
}

/// <summary>Nothing was at the address a clip named.</summary>
/// <param name="address">The address.</param>
/// <param name="reason">Why not.</param>
/// <param name="inner">What went wrong underneath, if anything did.</param>
public sealed class VideoContentMissingException(string address, string reason, Exception? inner = null)
    : Exception($"The video at '{address}' could not be opened: {reason}", inner) {
    /// <summary>Which address.</summary>
    public string Address { get; } = address;
}

/// <summary>Videos that are files under a directory.</summary>
/// <remarks>
///     <para>
///         The ordinary arrangement, and not a fallback for one. A video is streamed rather than
///         loaded, so it is one of the few things a shipping title has a reason to leave loose beside
///         the executable instead of packing into a bundle — a bundle's whole purpose is to make many
///         small assets one read, and a cutscene is already one read.
///     </para>
///     <para>
///         ⚠ The address is joined to the root and then checked to be <i>under</i> it. An address
///         comes out of content the game shipped rather than out of a player, so this is not a
///         security boundary; what it catches is a build that wrote an absolute path into a clip,
///         which otherwise works on the machine that built it and on no other.
///     </para>
/// </remarks>
/// <param name="root">The directory addresses are relative to.</param>
/// <param name="extension">Appended when an address has none. Empty to append nothing.</param>
public sealed class FileVideoContentSource(string root, string extension = ".webm") : IVideoContentSource {
    readonly string root = Path.GetFullPath(
        root ?? throw new ArgumentNullException(nameof(root))
    );

    /// <summary>Where an address's file is.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Its full path.</returns>
    /// <exception cref="VideoContentMissingException">It does not land under the root.</exception>
    public string PathOf(string address) {
        ArgumentException.ThrowIfNullOrEmpty(address);

        var relative = Path.HasExtension(address) || extension.Length == 0
            ? address
            : address + extension;

        var full = Path.GetFullPath(Path.Combine(root, relative));

        if (!full.StartsWith(root, StringComparison.Ordinal)) {
            throw new VideoContentMissingException(
                address,
                $"it resolves to {full}, which is outside {root}. An address is a path relative to the "
                + "content root, and an absolute one in a clip is a build that recorded a path from the "
                + "machine it ran on."
            );
        }

        return full;
    }

    /// <inheritdoc />
    public bool Exists(string address) {
        try {
            return File.Exists(PathOf(address));
        } catch (VideoContentMissingException) {
            return false;
        }
    }

    /// <inheritdoc />
    public Stream Open(string address) {
        var path = PathOf(address);

        if (!File.Exists(path)) {
            throw new VideoContentMissingException(address, $"nothing is at {path}.");
        }

        // Read-shared, because the picture and the sound are opened separately whenever either side
        // loops — see MatroskaDemuxer's remarks on two readers and one seeker — and the second open
        // of the same file must not be refused by the first.
        return new FileStream(
            path,
            new FileStreamOptions {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan
            }
        );
    }
}

/// <summary>A source made out of a function, for everything that is not a file.</summary>
/// <remarks>
///     What an addressable game uses. <c>AssetManager.Open</c> hands back a seekable stream over a
///     bundle entry, so the whole of the wiring is:
///     <code>
///     var content = new DelegatedVideoContentSource(assets.Open, assets.Exists);
///     </code>
///     which is the amount of glue a seam this narrow should need. It is also what a test uses to
///     serve a video it generated in memory.
/// </remarks>
/// <param name="open">Opens an address. Must return a seekable stream.</param>
/// <param name="exists">Whether an address is there. Assumed <see langword="true" /> if not given.</param>
public sealed class DelegatedVideoContentSource(Func<string, Stream> open, Func<string, bool>? exists = null)
    : IVideoContentSource {
    readonly Func<string, Stream> open = open ?? throw new ArgumentNullException(nameof(open));

    /// <inheritdoc />
    public bool Exists(string address) => exists?.Invoke(address) ?? true;

    /// <inheritdoc />
    public Stream Open(string address) {
        ArgumentException.ThrowIfNullOrEmpty(address);

        var stream = open(address)
            ?? throw new VideoContentMissingException(address, "the source returned nothing.");

        if (!stream.CanSeek) {
            stream.Dispose();

            throw new VideoContentMissingException(
                address,
                "the source returned a stream that cannot seek. A demuxer reads the segment's index from "
                + "the end of the file and comes back, so a forward-only stream cannot be demuxed at all."
            );
        }

        return stream;
    }
}
