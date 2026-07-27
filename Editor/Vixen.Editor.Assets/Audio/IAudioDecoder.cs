// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;

namespace Vixen.Editor.Assets.Audio;

/// <summary>Turns an authoring audio format into samples the engine can work with.</summary>
/// <remarks>
///     <para>
///         The same seam as <see cref="Textures.IImageDecoder" />, for the same reason and with the
///         same history behind it: doc 01 named a codec, the codec turned out to be unusable, and the
///         swap cost one class because nothing above the interface knew which one it was. Audio has
///         more codec churn than images, not less — Vorbis, Opus, FLAC and MP3 each have several
///         managed implementations with different licences.
///     </para>
///     <para>
///         Everything decodes to an <see cref="AudioClip" /> in whatever format the file naturally
///         holds. Converting between formats, mixing to mono and every other policy is the importer's
///         business, so a decoder is only ever asked to read a file correctly.
///     </para>
/// </remarks>
public interface IAudioDecoder {
    /// <summary>Which extensions it reads, lowercase and with their leading dots.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Decodes a file.</summary>
    /// <param name="stream">The file's bytes.</param>
    /// <param name="extension">Which extension it arrived as, for decoders that read more than one.</param>
    /// <returns>The samples, in whatever format the file held them.</returns>
    /// <exception cref="AudioFormatException">The file is not what it claims to be.</exception>
    AudioClip Decode(Stream stream, string extension);
}

/// <summary>A file that says it is audio and is not readable as any.</summary>
/// <param name="message">What is wrong with it, in a sentence naming the field.</param>
/// <remarks>
///     Its own exception rather than <see cref="InvalidDataException" />, so the importer can tell
///     "this file is malformed" — which is the author's problem and belongs in a diagnostic against
///     the asset — apart from an I/O failure, which is the machine's problem and is not.
/// </remarks>
public sealed class AudioFormatException(string message) : Exception(message);

/// <summary>The decoders an importer uses when nobody hands it a different set.</summary>
public static class AudioDecoders {
    /// <summary>Every decoder that ships.</summary>
    /// <remarks>
    ///     One. Doc 08's importer table lists wav, ogg, mp3 and flac; the last three need a codec
    ///     this repository has not chosen yet, and <see cref="AudioImporter" /> says so by name
    ///     rather than failing with "no decoder".
    /// </remarks>
    public static IReadOnlyList<IAudioDecoder> BuiltIn { get; } = [new WaveDecoder()];

    /// <summary>Finds the decoder for an extension.</summary>
    /// <param name="decoders">The set to look in.</param>
    /// <param name="extension">The extension, with its leading dot.</param>
    /// <returns>The decoder, or <see langword="null" /> if nothing reads that.</returns>
    public static IAudioDecoder? For(IReadOnlyList<IAudioDecoder> decoders, string extension) {
        ArgumentNullException.ThrowIfNull(decoders);

        var wanted = extension.ToLowerInvariant();

        foreach (var decoder in decoders) {
            if (decoder.Extensions.Contains(wanted, StringComparer.Ordinal)) {
                return decoder;
            }
        }

        return null;
    }
}
