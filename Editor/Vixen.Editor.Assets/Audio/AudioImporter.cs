// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio;
using Vixen.Core;
using Vixen.Core.Serialization;

namespace Vixen.Editor.Assets.Audio;

/// <summary>Turns a sound file into the clip a device plays.</summary>
/// <remarks>
///     <para>
///         Decode, mix, convert, write. The decode is a <see cref="IAudioDecoder" /> and everything
///         after it is policy, which is the same division <c>TextureImporter</c> runs on and for the
///         same reason: a codec is a dependency and the decisions are the engine's.
///     </para>
///     <para>
///         <b>It claims formats it cannot yet read, which is a deviation worth naming.</b>
///         <c>TextureImporter</c> claims only the extensions it decodes, so an <c>.exr</c> falls to
///         <c>RawImporter</c> and ships as a blob. That is the wrong answer for audio: doc 08's table
///         promises ogg, mp3 and flac, and an artist who drops an <c>.ogg</c> in and finds it silently
///         became an unplayable byte blob has learned nothing. Claiming it and failing with the name
///         of what is missing is the more useful of the two silences.
///     </para>
/// </remarks>
[Importer(".wav", ".wave", ".ogg", ".mp3", ".flac")]
public sealed class AudioImporter : AssetImporter<AudioImportSettings> {
    readonly IReadOnlyList<IAudioDecoder> decoders;

    /// <summary>Uses the decoders that ship.</summary>
    public AudioImporter() : this(AudioDecoders.BuiltIn) { }

    /// <summary>Uses a given set of decoders.</summary>
    /// <param name="decoders">The decoders.</param>
    public AudioImporter(IReadOnlyList<IAudioDecoder> decoders) {
        ArgumentNullException.ThrowIfNull(decoders);
        this.decoders = decoders;
    }

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        AudioImportSettings settings,
        CancellationToken cancellationToken
    ) {
        var extension = Path.GetExtension(context.SourcePath.ToString()).ToLowerInvariant();

        if (AudioDecoders.For(decoders, extension) is not { } decoder) {
            context.Report(
                ImportSeverity.Error,
                $"Nothing here decodes {extension}. WaveDecoder reads uncompressed WAV; Vorbis, MP3 and FLAC each "
                + "need a codec this repository has not chosen yet, and doc 08's table is what is owed."
            );

            return context.Finish();
        }

        AudioClip clip;

        try {
            await using var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false);
            clip = decoder.Decode(source, extension);
        } catch (AudioFormatException failure) {
            // A malformed file is the author's problem and belongs against the asset. An I/O failure
            // is the machine's and is deliberately not caught here — it is not a fact about the file.
            context.Report(ImportSeverity.Error, failure.Message);
            return context.Finish();
        }

        if (clip.FrameCount == 0) {
            context.Report(
                ImportSeverity.Warning,
                "It decodes to no samples at all. Something exported an empty file, and it will play as silence."
            );
        }

        if (settings.ForceMono && clip.Channels > 1) {
            context.Report(
                ImportSeverity.Information,
                $"Mixed {clip.Channels} channels down to one, so it can be positioned in the world."
            );

            clip = ToMono(clip);
        }

        clip = Convert(clip, settings.Format);

        context.Report(
            ImportSeverity.Information,
            $"{clip.Duration.TotalSeconds:0.##} s, {clip.SampleRate} Hz, "
            + $"{(clip.Channels == 1 ? "mono" : $"{clip.Channels} channels")}, {clip.Format}."
        );

        context.Write(SubAssetId.Main, "AudioClip", Serializer.ToBytes(clip));
        return context.Finish();
    }

    /// <summary>Averages every channel into one.</summary>
    /// <remarks>
    ///     <para>
    ///         An average and not a sum: summing two correlated channels doubles the amplitude and
    ///         clips anything that was mastered near full scale, which is most of what a sound
    ///         designer delivers.
    ///     </para>
    ///     <para>
    ///         Done before the format conversion, so a float source is averaged in float. Averaging
    ///         16-bit integers first would round every frame twice.
    ///     </para>
    /// </remarks>
    static AudioClip ToMono(AudioClip clip) {
        var frames = clip.FrameCount;
        var channels = clip.Channels;

        if (clip.Format is AudioSampleFormat.Float32) {
            var source = clip.AsFloat32();
            var samples = new byte[frames * 4];

            for (var frame = 0; frame < frames; frame++) {
                var total = 0f;

                for (var channel = 0; channel < channels; channel++) {
                    total += source[(frame * channels) + channel];
                }

                BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan(frame * 4), total / channels);
            }

            return clip with { Channels = 1, Samples = samples };
        }

        var integers = clip.AsInt16();
        var mixed = new byte[frames * 2];

        for (var frame = 0; frame < frames; frame++) {
            var total = 0;

            for (var channel = 0; channel < channels; channel++) {
                total += integers[(frame * channels) + channel];
            }

            BinaryPrimitives.WriteInt16LittleEndian(mixed.AsSpan(frame * 2), (short)(total / channels));
        }

        return clip with { Channels = 1, Samples = mixed };
    }

    /// <summary>Converts to the format the settings ask for, or leaves it alone.</summary>
    static AudioClip Convert(AudioClip clip, AudioFormatChoice choice) {
        var wanted = choice switch {
            AudioFormatChoice.Int16 => AudioSampleFormat.Int16,
            AudioFormatChoice.Float32 => AudioSampleFormat.Float32,
            _ => clip.Format
        };

        if (wanted == clip.Format) {
            return clip;
        }

        if (wanted is AudioSampleFormat.Int16) {
            var source = clip.AsFloat32();
            var samples = new byte[source.Length * 2];

            for (var index = 0; index < source.Length; index++) {
                // Scaled by 32767 and clamped, rather than by 32768. A float source is allowed to
                // reach exactly 1.0 and 32768 is not a short; clamping after multiplying by the
                // larger number would flatten the loudest sample of every normalised clip.
                var value = (int)MathF.Round(Math.Clamp(source[index], -1f, 1f) * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(index * 2), (short)value);
            }

            return clip with { Format = AudioSampleFormat.Int16, Samples = samples };
        }

        var integers = clip.AsInt16();
        var floats = new byte[integers.Length * 4];

        for (var index = 0; index < integers.Length; index++) {
            // Divided by 32768 so that the full negative range maps to exactly −1 and nothing
            // overshoots; the positive side then stops just short of 1, which is what every audio
            // API expects of a converted integer signal.
            BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(index * 4), integers[index] / 32768f);
        }

        return clip with { Format = AudioSampleFormat.Float32, Samples = floats };
    }
}
