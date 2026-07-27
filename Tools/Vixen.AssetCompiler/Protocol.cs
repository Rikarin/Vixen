// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core;
using Vixen.Core.Serialization;

namespace Vixen.AssetCompiler;

/// <summary>One import, on the wire.</summary>
/// <remarks>
///     A flat mirror of <c>ImportJob</c> rather than the type itself. That one holds a
///     <c>VirtualPath</c> and an <c>AssetId</c>, which are engine identity structs and could be
///     serialised; the mirror exists so the wire format is a thing this file defines completely and
///     a change to a domain type is not silently a protocol break.
/// </remarks>
[DataContract("VixenImportRequest")]
public sealed record ImportRequestMessage {
    /// <summary>Which asset, as 32 hex digits.</summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>Which importer, by name.</summary>
    public string Importer { get; set; } = string.Empty;

    /// <summary>Where its source is, as a virtual path.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Its settings, as YAML with the per-target overrides already resolved.</summary>
    public string Settings { get; set; } = string.Empty;

    /// <summary>Which build target.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Whether an undeclared read fails the import.</summary>
    public bool EnforceDeclaredReads { get; set; } = true;
}

/// <summary>One chunk an import produced, on the wire.</summary>
[DataContract("VixenImportArtifact")]
public sealed record ArtifactMessage {
    /// <summary>Which sub-asset it is, as eight hex digits, or empty for the main object.</summary>
    public string SubAsset { get; set; } = string.Empty;

    /// <summary>What kind of thing it is.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Its bytes.</summary>
    public byte[] Content { get; set; } = [];
}

/// <summary>One sub-asset an import declared, on the wire.</summary>
[DataContract("VixenImportSubAsset")]
public sealed record SubAssetMessage {
    /// <summary>Its id, as eight hex digits.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What kind of thing it is.</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>One thing an importer said, on the wire.</summary>
[DataContract("VixenImportDiagnostic")]
public sealed record DiagnosticMessage {
    /// <summary>How much attention it needs, as the enum's value.</summary>
    public int Severity { get; set; }

    /// <summary>What it says.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>What one import produced, on the wire.</summary>
[DataContract("VixenImportResponse")]
public sealed record ImportResponseMessage {
    /// <summary>Whether it came out with no errors.</summary>
    public bool Succeeded { get; set; }

    /// <summary>The chunks.</summary>
    public ArtifactMessage[] Artifacts { get; set; } = [];

    /// <summary>What the asset now declares it contains.</summary>
    public SubAssetMessage[] SubAssets { get; set; } = [];

    /// <summary>Everything the importer said.</summary>
    public DiagnosticMessage[] Diagnostics { get; set; } = [];

    /// <summary>Every file it declared, including its own source.</summary>
    public string[] FileDependencies { get; set; } = [];

    /// <summary>Every other asset it declared, as 32 hex digits each.</summary>
    public string[] AssetDependencies { get; set; } = [];
}

/// <summary>Reads and writes whole messages over a stream.</summary>
/// <remarks>
///     <para>
///         <b>Length-prefixed, because a pipe is a stream and not a sequence of messages.</b> A named
///         pipe in byte mode delivers whatever has arrived, which is regularly less than one message
///         and occasionally more than one. A reader that treated one <c>ReadAsync</c> as one message
///         would work for every small mesh and fail for the first big one — which is the worst
///         possible distribution of that bug.
///     </para>
///     <para>
///         The prefix is four bytes little-endian, like everything else the serializer writes. A
///         length that is not plausible is refused rather than allocated: the number arrived from
///         another process, and a corrupt or hostile one should not turn into a request for two
///         gigabytes.
///     </para>
/// </remarks>
public static class Framing {
    /// <summary>The largest message that will be read.</summary>
    /// <remarks>
    ///     Generous rather than tight — an uncompressed mesh chunk is easily tens of megabytes — and
    ///     present rather than absent, because the alternative is trusting a length from another
    ///     process with an allocation.
    /// </remarks>
    public const int MaximumLength = 512 * 1024 * 1024;

    /// <summary>Writes one message.</summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="stream">Where to write it.</param>
    /// <param name="value">The message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The task.</returns>
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(stream);

        var payload = Serializer.ToBytes(value);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message.</summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="stream">Where to read from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The message, or <see langword="null" /> if the other end went away.</returns>
    /// <remarks>
    ///     <b>Null and not an exception when the stream ends.</b> The other end going away is the
    ///     ordinary way this conversation finishes — a pool shutting its workers down — and it is also
    ///     what a crash looks like. Telling those apart is the caller's business and it has the
    ///     context to do it; here they are the same event.
    /// </remarks>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];

        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false)) {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length < 0 || length > MaximumLength) {
            throw new InvalidDataException(
                $"A message announced {length} bytes, which is not a length this reads. The stream is out of "
                + "step with the writer, or it is not carrying this protocol at all."
            );
        }

        var payload = new byte[length];

        return await FillAsync(stream, payload, cancellationToken).ConfigureAwait(false)
            ? Serializer.Read<T>(payload)
            : default;
    }

    /// <summary>Reads until the buffer is full or the stream ends.</summary>
    static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) {
        var filled = 0;

        while (filled < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);

            if (read == 0) {
                return false;
            }

            filled += read;
        }

        return true;
    }
}
