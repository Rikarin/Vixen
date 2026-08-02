// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml.Meta;
using Vixen.Terrain;

namespace Vixen.Editor.Assets.Terrain;

/// <summary>How a raw heightmap is read.</summary>
/// <remarks>
///     ⚠ <b>Not empty, unlike the other importers here, because a raw file carries no header.</b>
///     A <c>.r16</c> is width × height × two bytes and nothing else — there is no dimension, no
///     endianness and no scale in the file, so every one of them has to be a setting. That is what
///     makes it a raw format and it is why World Machine asks the same three questions on export.
/// </remarks>
[DataContract("HeightmapImporter")]
public sealed record HeightmapImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;

    /// <summary>How many samples the file is, on a side.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero means "work it out from the length", and it is right far more often than not.</b>
    ///     Heightmaps come out of every terrain generator square, so the square root of half the byte
    ///     count is the answer — and a file that is not square is one this cannot guess about, which
    ///     is when an author fills these in.
    /// </remarks>
    public int Width { get; init; }

    /// <summary>And on the other side.</summary>
    public int Height { get; init; }

    /// <summary>Whether the samples are big-endian.</summary>
    /// <remarks>
    ///     ⚠ <b>The setting an author gets wrong, and the symptom does not look like an endianness
    ///     problem.</b> Sixteen-bit samples read the wrong way round produce a terrain of dense
    ///     vertical noise rather than garbage — it looks like a broken generator, not a broken read.
    ///     World Machine writes little-endian and Terragen writes big-endian.
    /// </remarks>
    public bool BigEndian { get; init; }
}

/// <summary>Imports a raw 16-bit heightmap.</summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T1]'s split, from the other side.</b> That phase kept raw <c>r16</c>
///         import in the kernel because it is bytes and needs no reference, and said the 16-bit PNG
///         half "needs <c>Vixen.Core.Imaging</c> and belongs with the importer". This is the importer;
///         what it does is turn a file into the samples <see cref="TerrainHeightmap.Import" /> already
///         knows how to resample.
///     </para>
///     <para>
///         ⚠ <b>PNG is refused rather than imported at eight bits, and that is the whole point of the
///         refusal.</b> The decoder this build has — <c>StbImageDecoder</c>, which doc 01 chose over
///         ImageSharp for licensing — reads every PNG as eight bits a channel. A 16-bit heightmap
///         through it is a terrain quantised to 256 heights, which does not look like a broken import:
///         it looks like a terrain with a faint terrace on every slope, and it would be attributed to
///         the generator. So the extension is claimed and answered with the reason.
///     </para>
///     <para>
///         ⚠ <b>Resampling is not optional and belongs to the kernel.</b> A terrain of four
///         128-sample tiles is 509 samples across and heightmaps come out of World Machine at 512,
///         1024 and 2049 — they essentially never match. <see cref="TerrainHeightmap" /> does it
///         bilinearly and edge-to-edge; mapping by scale factor instead leaves a flat lip along two
///         sides of every imported terrain.
///     </para>
/// </remarks>
[Importer(RawExtension, PngExtension)]
public sealed class HeightmapImporter : AssetImporter<HeightmapImportSettings> {
    /// <summary>What a raw 16-bit heightmap is written as.</summary>
    public const string RawExtension = ".r16";

    /// <summary>And what a 16-bit PNG one is.</summary>
    /// <remarks>
    ///     ⚠ <b>Read by <see cref="TerrainHeightmapPng" /> rather than by the generic image decoder,
    ///     and the split is the bit depth.</b> That decoder reads eight bits a channel, which is right
    ///     for a texture and wrong for a heightfield: a terrain quantised to 256 heights is a faint
    ///     terrace on every slope, and it gets attributed to whatever generated it.
    /// </remarks>
    public const string PngExtension = ".hmpng";

    /// <summary>The <c>[DataContract]</c> alias a decoded heightmap is recorded under.</summary>
    public const string Alias = "Heightmap";

    /// <inheritdoc />
    public override int Version => 1;

    /// <summary>What a square file of this many bytes is, on a side.</summary>
    /// <param name="byteCount">How long the file is.</param>
    /// <returns>The side, or 0 if the length is not a square of 16-bit samples.</returns>
    public static int SquareSideOf(long byteCount) {
        if (byteCount <= 0 || byteCount % sizeof(ushort) != 0) {
            return 0;
        }

        var samples = byteCount / sizeof(ushort);
        var side = (int)Math.Round(Math.Sqrt(samples));

        return (long)side * side == samples ? side : 0;
    }

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        HeightmapImportSettings settings,
        CancellationToken cancellationToken
    ) {
        var extension = Path.GetExtension(context.SourcePath.ToString()).ToLowerInvariant();

        byte[] bytes;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var buffer = new MemoryStream();

            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        var width = settings.Width;
        var height = settings.Height;

        // ⚠ A PNG carries its own size and its own bit depth, so the settings are ignored for one —
        // which is the whole reason to prefer it over raw. A person who filled them in for a `.r16`
        // and then imported a `.hmpng` would otherwise get whichever answer the form happened to
        // hold.
        if (extension == PngExtension) {
            try {
                var image = TerrainHeightmapPng.Decode(bytes);

                width = image.Width;
                height = image.Height;
                bytes = new byte[(long)width * height * sizeof(ushort)];

                for (var index = 0; index < image.Samples.Length; index++) {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes.AsSpan(index * sizeof(ushort)),
                        image.Samples[index]
                    );
                }
            } catch (ArgumentException failure) {
                context.Report(ImportSeverity.Error, failure.Message);

                return context.Finish();
            }
        }

        if (width <= 0 || height <= 0) {
            var side = SquareSideOf(bytes.LongLength);

            if (side == 0) {
                context.Report(
                    ImportSeverity.Error,
                    $"It is {bytes.LongLength} bytes, which is not a square of 16-bit samples — so the "
                    + "dimensions cannot be worked out from the length. Fill in the width and height, "
                    + "which is what the file does not carry."
                );

                return context.Finish();
            }

            width = side;
            height = side;
        }

        var format = new TerrainHeightmapFormat(width, height, settings.BigEndian);

        if (bytes.LongLength < format.ByteCount) {
            context.Report(
                ImportSeverity.Error,
                $"A {width} × {height} heightmap is {format.ByteCount} bytes and this file is "
                + $"{bytes.LongLength}. Either the dimensions are wrong or the export was truncated."
            );

            return context.Finish();
        }

        if (bytes.LongLength > format.ByteCount) {
            // A warning rather than an error: a file with a trailing byte is a real thing exporters
            // produce, and refusing it would cost an author an afternoon over one byte nothing reads.
            context.Report(
                ImportSeverity.Warning,
                $"It is {bytes.LongLength} bytes and a {width} × {height} heightmap is "
                + $"{format.ByteCount}. The tail is ignored."
            );
        }

        // ⚠ The bytes forward, not a Terrain. A heightmap is not a terrain: it has no tile size, no
        // height range and no layers, and every one of those is a decision the create dialog makes
        // when somebody applies this to one. TerrainHeightmap.Import is what does the applying, and
        // it resamples — which is the step that cannot be skipped, because 512 and 509 never match.
        context.Write(SubAssetId.Main, Alias, bytes.AsSpan(0, (int)format.ByteCount).ToArray());

        return context.Finish();
    }
}
