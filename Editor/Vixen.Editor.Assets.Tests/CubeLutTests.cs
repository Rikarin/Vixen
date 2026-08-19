// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Textures;
using Vixen.Graphics;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The <c>.cube</c> grading table, which is the format every colour suite exports.
/// </summary>
/// <remarks>
///     The parser is the whole of the importer's logic, so it is what is tested. Everything asserted
///     here is a property of the published format rather than of this implementation — the ordering,
///     the domain, and what happens to a file that says one thing and holds another.
/// </remarks>
public class CubeLutTests {
    /// <summary>A table of the size it declared, in a format that can hold values past one.</summary>
    [Fact]
    public void AParsedTableIsACubeOfHalfFloats() {
        var table = CubeLut.Parse(Identity(2));

        Assert.Equal(PixelFormat.Rgba16Float, table.Format);
        Assert.Equal(2, table.Width);
        Assert.Equal(2, table.Height);
        Assert.Equal(2, table.Depth);
        Assert.Equal(1, table.LevelCount);
    }

    /// <summary>
    ///     ⚠ Red varies fastest, which is the format's rule and the opposite of the usual one.
    /// </summary>
    /// <remarks>
    ///     A 3D texture's memory is normally described with X fastest, and it happens to agree here —
    ///     but only because the format's first axis <em>is</em> red. Reading the axes the other way
    ///     round produces the table's own transpose, which is a plausible grade of a different
    ///     picture and nothing downstream can tell you.
    /// </remarks>
    [Fact]
    public void RedVariesFastest() {
        // A 2³ table whose entries are their own coordinates. If red is fastest, the second entry is
        // (1, 0, 0) — one step along red — and the fifth is (0, 0, 1).
        var table = CubeLut.Parse(Identity(2));

        Assert.Equal(new(1f, 0f, 0f), Texel(table, 1));
        Assert.Equal(new(0f, 1f, 0f), Texel(table, 2));
        Assert.Equal(new(0f, 0f, 1f), Texel(table, 4));
    }

    /// <summary>An identity table maps every corner to itself.</summary>
    [Fact]
    public void AnIdentityTableRoundTripsItsCorners() {
        var table = CubeLut.Parse(Identity(3));

        Assert.Equal(new(0f, 0f, 0f), Texel(table, 0));
        Assert.Equal(new(1f, 1f, 1f), Texel(table, (3 * 3 * 3) - 1));
    }

    /// <summary>A declared domain is normalised out, so the sampler always indexes 0..1.</summary>
    /// <remarks>
    ///     Almost every exporter writes 0..1 and this is a multiply by one. The files that do not are
    ///     usually log-encoded, and a table read without undoing its domain is a grade applied to the
    ///     wrong part of the curve.
    /// </remarks>
    [Fact]
    public void ADeclaredDomainIsUndone() {
        var table = CubeLut.Parse(
            """
            LUT_3D_SIZE 2
            DOMAIN_MIN 0 0 0
            DOMAIN_MAX 2 2 2
            0 0 0
            2 0 0
            0 2 0
            2 2 0
            0 0 2
            2 0 2
            0 2 2
            2 2 2
            """
        );

        Assert.Equal(new(1f, 0f, 0f), Texel(table, 1));
        Assert.Equal(new(1f, 1f, 1f), Texel(table, 7));
    }

    /// <summary>Comments, titles and blank lines are not entries.</summary>
    [Fact]
    public void TitlesAndCommentsAreSkipped() {
        var table = CubeLut.Parse(
            """
            # exported by something

            TITLE "a look"
            LUT_3D_SIZE 2

            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
            """
        );

        Assert.Equal(2, table.Width);
    }

    /// <summary>
    ///     ⚠ A file that holds fewer entries than it declared is refused, and says both numbers.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents is the quiet one: a truncated table read as far as it goes leaves
    ///     the rest of the volume at zero, which grades everything above some luminance to black.
    /// </remarks>
    [Fact]
    public void ATruncatedTableIsRefusedRatherThanPadded() {
        var thrown = Assert.Throws<FormatException>(
            () => CubeLut.Parse("LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n")
        );

        Assert.Contains("8", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("3", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A 1D table is named rather than misread as a short 3D one.</summary>
    [Fact]
    public void AOneDimensionalTableIsNamed() {
        var thrown = Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_1D_SIZE 16\n0 0 0\n"));

        Assert.Contains("1D", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A file with no size has no shape, and says so.</summary>
    [Fact]
    public void AFileWithNoSizeIsRefused() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse("0 0 0\n1 1 1\n"));

    /// <summary>An absurd size is refused before it is allocated.</summary>
    [Fact]
    public void AnAbsurdSizeIsRefusedBeforeItIsAllocated() =>
        Assert.Throws<FormatException>(() => CubeLut.Parse("LUT_3D_SIZE 4096\n"));

    /// <summary>And that the build's own registry hands a <c>.cube</c> to the LUT importer.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The gap the rest of this file stepped over, and why the defect survived review.</b>
    ///         Everything above tests <see cref="CubeLut.Parse" />, which is the importer's logic and
    ///         is not the importer — so the suite was green while <see cref="CubeLutImporter" /> was
    ///         absent from <see cref="BuiltInImporters.Create()" /> and no <c>.cube</c> in any project
    ///         ever reached it. The file fell through to <c>RawImporter</c>, became a blob under the
    ///         type name <c>"Blob"</c>, and <c>Tonemap</c>'s <c>lut:</c> — which
    ///         <c>docs/guide/rendering/post-processing.md</c> tells an author to write — resolved to
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         What is asserted is which importer claimed it, not that an artefact appeared:
    ///         <c>RawImporter</c> produces one of those too, which is the whole reason the failure was
    ///         silent.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheBuildsOwnRegistryHandsACubeFileToIt() {
        // ⚠ A contribution set of its own, not ImporterContributions.Default: the default is
        // process-wide and ImporterContributionTests mutates it, so reading it here would race.
        var registry = BuiltInImporters.Create(new ImporterContributions());

        Assert.True(registry.TryGetForFile("Assets/Looks/evening.cube", out var importer));
        Assert.IsType<CubeLutImporter>(importer);
        Assert.Contains(".cube", importer.Extensions);
    }

    /// <summary>A <c>.cube</c> driven through the importer is a KTX2 texture, mipless.</summary>
    /// <remarks>
    ///     ⚠ <b>The type name is <c>"Texture"</c> — the same one every image ships as</b> — because
    ///     what the tonemapper binds is a texture. Asserted here so that a later importer writing its
    ///     own type name breaks a test rather than a frame: the sampler would find nothing and the
    ///     grade would silently not apply, which looks like a grade somebody authored flat.
    /// </remarks>
    [Fact]
    public async Task ACubeFileImportsToATextureArtefact() {
        var path = new VirtualPath("/Assets/Looks/evening.cube");
        var files = new MemoryFileProvider();

        files.Seed(path, Identity(2));

        var importer = new CubeLutImporter();
        var context = new ImportContext(
            AssetId.New(),
            path,
            importer.CreateSettings(),
            files,
            importer.Name,
            "Windows"
        );

        var result = await importer.ImportAsync(context, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        var artefact = Assert.Single(result.Artifacts);

        Assert.Equal("Texture", artefact.Type);
        Assert.NotEmpty(artefact.Content.ToArray());
    }

    /// <summary>An identity table of a given edge, with red varying fastest.</summary>
    static string Identity(int size) {
        var lines = new List<string> { $"LUT_3D_SIZE {size}" };
        var last = size - 1f;

        for (var b = 0; b < size; b++) {
            for (var g = 0; g < size; g++) {
                for (var r = 0; r < size; r++) {
                    lines.Add($"{r / last} {g / last} {b / last}");
                }
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>One texel's three colour channels, read back out of the half-float volume.</summary>
    static Vixen.Core.Mathematics.Vector3 Texel(Vixen.Core.Imaging.TextureData table, int index) {
        var pixels = table.Pixels;
        var at = index * 4 * sizeof(ushort);

        return new(Half(pixels, at), Half(pixels, at + 2), Half(pixels, at + 4));
    }

    static float Half(ReadOnlySpan<byte> pixels, int at) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(pixels[at] | (pixels[at + 1] << 8)));
}
