// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Editor.Assets.Textures;
using Vixen.Geometry.Remeshing;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>The step between what a bake measured and the files it becomes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>MapBaker.Bake</c> had no caller anywhere in the repository.</b> Doc 48 § D12's
///         seven measurements landed on <c>BakedMaps</c> and every reference to the bake outside its
///         own tests was a sentence in a guide — no importer, no content build, no editor. So none of
///         the decisions below had ever been made anywhere, which is why they are asserted here
///         rather than assumed: what the file is called, which way up it is, and what a signed
///         measurement turns into in eight bits.
///     </para>
///     <para>
///         These run on arrays a test wrote by hand. There is no source mesh, no ray and no disk in
///         any of it, which is the point of <see cref="MeshMapBake.Encode" /> being a separate
///         function from the bake it usually follows.
///     </para>
/// </remarks>
public sealed class MeshMapEncodingTests {
    /// <summary>Every usage has its own suffix, and a file name reads back into what wrote it.</summary>
    [Fact]
    public void Every_usage_names_a_file_that_parses_back() {
        var suffixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var usage in MeshMapNaming.Every) {
            Assert.True(suffixes.Add(MeshMapNaming.Suffix(usage)), $"{usage} shares a suffix with another usage.");

            var file = MeshMapNaming.FileName("Barrel", usage);

            Assert.True(MeshMapNaming.TryParseFileName(file, out var mesh, out var read), file);
            Assert.Equal("Barrel", mesh);
            Assert.Equal(usage, read);
        }
    }

    /// <summary>A mesh whose own name has an underscore still parses, because the split is the last one.</summary>
    /// <remarks>
    ///     ⚠ The failure this stops is silent and partial: <c>Old_Barrel_ao.png</c> read at the first
    ///     separator is a map of <c>Old</c> with an unknown usage, so a set of nine loses however
    ///     many of its members happened to look like a suffix.
    /// </remarks>
    [Fact]
    public void A_mesh_named_with_an_underscore_still_parses() {
        Assert.True(
            MeshMapNaming.TryParseFileName(
                MeshMapNaming.FileName("Old_Barrel", MeshMapUsage.AmbientOcclusion),
                out var mesh,
                out var usage
            )
        );

        Assert.Equal("Old_Barrel", mesh);
        Assert.Equal(MeshMapUsage.AmbientOcclusion, usage);
    }

    /// <summary>Something that is not one of ours is refused rather than half-read.</summary>
    [Fact]
    public void A_file_that_is_not_a_mesh_map_is_refused() {
        Assert.False(MeshMapNaming.TryParseFileName("Barrel_albedo.png", out _, out _));
        Assert.False(MeshMapNaming.TryParseFileName("Barrel.png", out _, out _));
        Assert.False(MeshMapNaming.TryParseFileName("Barrel_ao.tga", out _, out _));
        Assert.False(MeshMapNaming.TryParseFileName("_ao.png", out _, out _));
        Assert.False(MeshMapNaming.TryParseFileName(null, out _, out _));
    }

    /// <summary>A bake nobody asked for extra maps from produces exactly the two that are not optional.</summary>
    [Fact]
    public void Only_the_maps_that_were_measured_become_files() {
        var made = MeshMapBake.Encode("Barrel", Measured(2));

        Assert.Equal(MeshMapBake.Always, made.Select(image => image.Usage).ToList());
        Assert.Equal("Barrel_normal.png", made[0].FileName);
        Assert.Equal("Barrel_height.png", made[1].FileName);
    }

    /// <summary>Everything § D12 lists becomes one file, and one only.</summary>
    [Fact]
    public void Every_measured_map_becomes_one_file() {
        var made = MeshMapBake.Encode("Barrel", Everything(2));

        Assert.Equal(MeshMapNaming.Every, made.Select(image => image.Usage).ToList());
        Assert.Equal(made.Count, made.Select(image => image.FileName).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The first row of the file is the top of the atlas, not the bottom.</summary>
    /// <remarks>
    ///     ⚠ <b>The closed-form oracle for a flip.</b> A <c>BakedMaps</c> array is row-major from the
    ///     bottom left because that is where a texture coordinate's origin is; a PNG's first row is
    ///     the top one. So a map whose bottom row is black and whose top row is white must decode to
    ///     a picture whose <i>first</i> row is white — and a map copied straight across is correct,
    ///     plausible and upside down, which is invisible on every symmetric test shape there is.
    /// </remarks>
    [Fact]
    public void The_first_row_of_the_file_is_the_top_of_the_atlas() {
        var occlusion = new[] { 0f, 0f, 1f, 1f };
        var maps = Measured(2) with { AmbientOcclusion = occlusion };
        var image = Single(MeshMapBake.Encode("Barrel", maps), MeshMapUsage.AmbientOcclusion);
        var decoded = PngCodec.Decode(image.Png);

        Assert.Equal(byte.MaxValue, decoded.Pixels[decoded.Offset(0, 0)]);
        Assert.Equal(byte.MaxValue, decoded.Pixels[decoded.Offset(1, 0)]);
        Assert.Equal(0, decoded.Pixels[decoded.Offset(0, 1)]);
        Assert.Equal(0, decoded.Pixels[decoded.Offset(1, 1)]);
    }

    /// <summary>A signed measurement survives eight bits, because the scale goes with it.</summary>
    /// <remarks>
    ///     Displacement is in the model's own units and deliberately not normalised by the bake —
    ///     <c>BakedMaps.DisplacementRange</c> says a caller quantizes with it. This is that caller,
    ///     and what it must not do is write the pixels and the scale from two different places.
    /// </remarks>
    [Fact]
    public void A_signed_map_carries_the_scale_that_decodes_it() {
        const float range = 0.25f;
        var maps = Measured(2) with {
            Displacement = [-range, 0f, range, range / 2f],
            DisplacementRange = range
        };

        var image = Single(MeshMapBake.Encode("Barrel", maps), MeshMapUsage.Displacement);
        var decoded = PngCodec.Decode(image.Png);

        Assert.Equal(range, image.Scale);

        // Bottom-left is the array's first entry, so it is the picture's last row.
        Assert.Equal(0, decoded.Pixels[decoded.Offset(0, 1)]);
        Assert.Equal(128, decoded.Pixels[decoded.Offset(1, 1)]);
        Assert.Equal(byte.MaxValue, decoded.Pixels[decoded.Offset(0, 0)]);

        // And it decodes back to what was measured, which is the only reason the scale is written.
        var quarter = ((decoded.Pixels[decoded.Offset(1, 0)] / 255f * 2f) - 1f) * image.Scale;

        Assert.Equal(range / 2f, quarter, 0.002f);
    }

    /// <summary>A range of nothing writes a flat half and a scale of nothing, rather than dividing by it.</summary>
    [Fact]
    public void A_measurement_with_no_range_is_flat_rather_than_infinite() {
        var maps = Measured(2) with { Displacement = [0f, 0f, 0f, 0f], DisplacementRange = 0f };
        var image = Single(MeshMapBake.Encode("Barrel", maps), MeshMapUsage.Displacement);
        var decoded = PngCodec.Decode(image.Png);

        Assert.Equal(0f, image.Scale);
        Assert.All(Enumerable.Range(0, 4), at => Assert.Equal(128, decoded.Pixels[at * 4]));
    }

    /// <summary>The id map is the baker's own colours, applied after everything that could filter them.</summary>
    /// <remarks>
    ///     ⚠ § D12: an id is a label, the average of two labels is a third label, and a map that has
    ///     been through a filter grows a hairline of a material that does not exist along every chart
    ///     border. What this asserts is the last step of that: the colour a texel gets is
    ///     <c>MapBaker.IdColour</c> of <i>that texel's</i> id and of nothing else.
    /// </remarks>
    [Fact]
    public void The_id_map_is_the_bakers_own_colour_per_texel() {
        var maps = Measured(2) with { Ids = [-1, 0, 1, 7] };
        var image = Single(MeshMapBake.Encode("Barrel", maps), MeshMapUsage.Id);
        var decoded = PngCodec.Decode(image.Png);

        // The array runs from the bottom left, so ids 0 and 1 are the picture's second row.
        Assert.Equal(Quantized(MapBaker.IdColour(-1)), Pixel(decoded, 0, 1));
        Assert.Equal(Quantized(MapBaker.IdColour(0)), Pixel(decoded, 1, 1));
        Assert.Equal(Quantized(MapBaker.IdColour(1)), Pixel(decoded, 0, 0));
        Assert.Equal(Quantized(MapBaker.IdColour(7)), Pixel(decoded, 1, 0));
    }

    /// <summary>Nothing a bake writes is compressed and nothing gets a mip chain.</summary>
    /// <remarks>
    ///     § D12 demands it of the id map — a filtered id is a colour belonging to no material — and
    ///     it is the right answer for all nine: a mesh map is an authoring input a generator samples
    ///     at atlas resolution, so a mip chain is a third more memory for levels nothing reads and
    ///     block compression is quantization underneath a mask threshold.
    /// </remarks>
    [Fact]
    public void No_mesh_map_is_compressed_or_mipped() {
        foreach (var image in MeshMapBake.Encode("Barrel", Everything(2))) {
            Assert.Equal(TextureCompression.None, image.Settings.Compression);
            Assert.False(image.Settings.GenerateMips, $"{image.FileName} would be mipped.");
        }
    }

    /// <summary>An object-space normal map is not declared to be a tangent-space one.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap is two channels.</b> <c>TextureContent.NormalMap</c> means BC5 and a shader
    ///     reconstructing Z as <c>+sqrt(1 − x² − y²)</c>, which is true of a tangent-space map and
    ///     false of an object-space one, whose Z is signed. Declared wrongly, half of the bake comes
    ///     back inside out the day somebody sets the compression to automatic.
    /// </remarks>
    [Fact]
    public void An_object_space_normal_map_is_not_declared_tangent_space() {
        var tangent = Single(MeshMapBake.Encode("Barrel", Measured(2)), MeshMapUsage.Normal);
        var objectSpace = Single(
            MeshMapBake.Encode("Barrel", Measured(2) with { Space = BakeSpace.Object }),
            MeshMapUsage.Normal
        );

        Assert.Equal(TextureContent.NormalMap, tangent.Settings.Content);
        Assert.Equal(TextureContent.Linear, objectSpace.Settings.Content);
    }

    /// <summary>A vector map is remapped about a half, so a flat normal is the middle of the range.</summary>
    [Fact]
    public void A_direction_is_stored_about_a_half() {
        var maps = Measured(1) with { Normals = [Vector3.UnitZ] };
        var decoded = PngCodec.Decode(Single(MeshMapBake.Encode("Barrel", maps), MeshMapUsage.Normal).Png);

        Assert.Equal(128, decoded.Pixels[0]);
        Assert.Equal(128, decoded.Pixels[1]);
        Assert.Equal(byte.MaxValue, decoded.Pixels[2]);
    }

    /// <summary>The position map is already in the unit cube, so it is not remapped again.</summary>
    [Fact]
    public void A_position_is_stored_as_it_was_measured() {
        var maps = Measured(1) with { Position = [Vector3.One] };
        var decoded = PngCodec.Decode(Single(MeshMapBake.Encode("Barrel", maps), MeshMapUsage.Position).Png);

        Assert.Equal(byte.MaxValue, decoded.Pixels[0]);
        Assert.Equal(byte.MaxValue, decoded.Pixels[1]);
        Assert.Equal(byte.MaxValue, decoded.Pixels[2]);
    }

    static MeshMapImage Single(IReadOnlyList<MeshMapImage> made, MeshMapUsage usage) =>
        made.Single(image => image.Usage == usage);

    static (byte R, byte G, byte B) Pixel(Bitmap image, int x, int y) {
        var at = image.Offset(x, y);
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    static (byte R, byte G, byte B) Quantized(Vector3 colour) =>
        (Level(colour.X), Level(colour.Y), Level(colour.Z));

    static byte Level(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

    /// <summary>A bake of the two maps that are never optional, over a square of texels.</summary>
    static BakedMaps Measured(int resolution) {
        var texels = resolution * resolution;

        return new() {
            Resolution = resolution,
            Normals = Enumerable.Repeat(Vector3.UnitZ, texels).ToArray(),
            Displacement = new float[texels],
            Coverage = Enumerable.Repeat(true, texels).ToArray(),
            Space = BakeSpace.Tangent,
            Covered = texels,
            Dilated = 0,
            Missed = 0,
            DisplacementRange = 0f
        };
    }

    /// <summary>The same, with every optional map measured.</summary>
    static BakedMaps Everything(int resolution) {
        var texels = resolution * resolution;

        return Measured(resolution) with {
            AmbientOcclusion = new float[texels],
            BentNormal = Enumerable.Repeat(Vector3.UnitZ, texels).ToArray(),
            Curvature = new float[texels],
            CurvatureRange = 1f,
            Thickness = new float[texels],
            Position = new Vector3[texels],
            WorldNormal = Enumerable.Repeat(Vector3.UnitY, texels).ToArray(),
            Ids = new int[texels]
        };
    }
}
