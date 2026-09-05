// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.Assets.Textures;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

namespace Vixen.Editor.Assets.MeshMaps;

/// <summary>The bake, and the encoding that turns what it measured into files.</summary>
/// <remarks>
///     <para>
///         <b>⚠ This is the caller <c>MapBaker.Bake</c> did not have.</b> Doc 48 § D12's seven
///         measurements landed on <c>BakedMaps</c> and nothing outside
///         <c>Vixen.Geometry.Remeshing.Tests</c> called the bake — not an importer, not a content
///         build, not the editor — which is this repository's commonest defect wearing its usual
///         disguise: a finished thing with no caller. Everything below exists to be that caller and
///         to hand what comes back to <see cref="IMeshMapBaker" />, which puts it in the project.
///     </para>
///     <para>
///         <b>Encoding is separate from baking and both are here.</b> <see cref="Encode" /> is a
///         pure function of a <c>BakedMaps</c>, so every decision that can be wrong — which row is
///         the top one, how a signed measurement survives eight bits, which import settings each
///         usage needs — is provable against arrays a test wrote by hand, with no source mesh, no
///         ray cast and no disk.
///     </para>
/// </remarks>
public static class MeshMapBake {
    /// <summary>The maps every bake produces, whatever was asked for.</summary>
    /// <remarks>
    ///     <c>BakeSettings.Maps</c> is empty by default because three of the seven cast rays; the
    ///     normal and the displacement are not in that flags enum at all, because they fall out of
    ///     the one ray the bake already casts. So a set always has these two and has the rest only
    ///     where they were asked for and measured.
    /// </remarks>
    public static IReadOnlyList<MeshMapUsage> Always { get; } = [MeshMapUsage.Normal, MeshMapUsage.Displacement];

    /// <summary>Bakes a mesh's maps and encodes them.</summary>
    /// <param name="mesh">What the set is called, which is the stem of every file in it.</param>
    /// <param name="source">The high-resolution surface. Read, never modified.</param>
    /// <param name="target">The mesh with the atlas the maps land in.</param>
    /// <param name="settings">The size, the gutter, the search radius and which maps to measure.</param>
    /// <returns>The files, and what the bake could not do.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">The mesh name is empty, or the target has no atlas.</exception>
    /// <remarks>
    ///     ⚠ <b><paramref name="source" /> and <paramref name="target" /> may be the same mesh, and
    ///     usually are.</b> A separate high-poly is the retopology case; asking for the ambient
    ///     occlusion, the curvature and the thickness <i>of the mesh you are texturing</i> is the
    ///     ordinary one, and it is what every generator in § 4.8 reads. The bake is the same either
    ///     way — the rays are cast from the target's atlas at whatever surface it is handed.
    /// </remarks>
    public static IReadOnlyList<MeshMapImage> Run(string mesh, EditMesh source, EditMesh target, BakeSettings settings) {
        ArgumentException.ThrowIfNullOrEmpty(mesh);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);

        return Encode(mesh, MapBaker.Bake(source, target, settings));
    }

    /// <summary>Turns what a bake measured into the files it becomes.</summary>
    /// <param name="mesh">What the set is called.</param>
    /// <param name="maps">What the bake measured.</param>
    /// <returns>One image per map the bake actually produced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="maps" /> is null.</exception>
    /// <exception cref="ArgumentException">The mesh name is empty.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rows are flipped.</b> A <c>BakedMaps</c> array is row-major <i>from the
    ///         bottom left</i>, because that is where a texture coordinate's origin is; a PNG's first
    ///         row is the top one. Copying straight across gives a map that is correct, plausible and
    ///         upside down against the very atlas it was baked from — which is invisible on a
    ///         symmetric test shape and obvious on nothing until a generator is masked by it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is compressed and nothing gets a mip chain.</b> A mesh map is an
    ///         authoring input a generator samples at atlas resolution, not a texture a surface
    ///         minifies: a mip chain is a third more memory for levels nothing reads, and block
    ///         compression adds quantization underneath a mask threshold. It also means § D12's
    ///         requirement that the id map is never filtered is the same rule as everything else here
    ///         rather than a special case somebody later optimises away.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<MeshMapImage> Encode(string mesh, BakedMaps maps) {
        ArgumentException.ThrowIfNullOrEmpty(mesh);
        ArgumentNullException.ThrowIfNull(maps);

        var size = maps.Resolution;
        var made = new List<MeshMapImage>();

        // ⚠ Object space, and the two-channel format is why. `TextureContent.NormalMap` means BC5
        // plus a shader reconstructing Z as +sqrt(1 - x² - y²), which is true of a tangent-space map
        // and false of an object-space one, whose Z is signed. Declaring the content honestly here
        // is what stops half of an object-space bake being turned inside out the day somebody sets
        // the compression back to automatic.
        var normalContent = maps.Space == BakeSpace.Tangent ? TextureContent.NormalMap : TextureContent.Linear;

        made.Add(Vector(mesh, MeshMapUsage.Normal, size, maps.Normals, signed: true, normalContent));
        made.Add(Signed(mesh, MeshMapUsage.Displacement, size, maps.Displacement, maps.DisplacementRange));

        if (maps.AmbientOcclusion is { } occlusion) {
            made.Add(Scalar(mesh, MeshMapUsage.AmbientOcclusion, size, occlusion));
        }

        if (maps.BentNormal is { } bent) {
            made.Add(Vector(mesh, MeshMapUsage.BentNormal, size, bent, signed: true, TextureContent.Linear));
        }

        if (maps.Curvature is { } curvature) {
            made.Add(Signed(mesh, MeshMapUsage.Curvature, size, curvature, maps.CurvatureRange));
        }

        if (maps.Thickness is { } thickness) {
            made.Add(Scalar(mesh, MeshMapUsage.Thickness, size, thickness));
        }

        if (maps.Position is { } position) {
            made.Add(Vector(mesh, MeshMapUsage.Position, size, position, signed: false, TextureContent.Linear));
        }

        if (maps.WorldNormal is { } world) {
            made.Add(Vector(mesh, MeshMapUsage.WorldNormal, size, world, signed: true, TextureContent.Linear));
        }

        if (maps.Ids is { } ids) {
            made.Add(Identifiers(mesh, size, ids));
        }

        return made;
    }

    /// <summary>The import settings every mesh map shares.</summary>
    /// <param name="content">What the bytes mean.</param>
    /// <returns>The settings.</returns>
    static TextureImportSettings Common(TextureContent content) =>
        new() {
            Content = content,
            Compression = TextureCompression.None,
            GenerateMips = false,
            AlphaIsTransparency = false
        };

    static MeshMapImage Scalar(string mesh, MeshMapUsage usage, int size, IReadOnlyList<float> values) {
        var pixels = Blank(size);

        Fill(size, values.Count, (index, at) => {
            var level = Byte(values[index]);
            pixels[at] = level;
            pixels[at + 1] = level;
            pixels[at + 2] = level;
        });

        return Made(mesh, usage, size, pixels, Common(TextureContent.Linear), 0f);
    }

    /// <summary>A measurement that is signed and in the model's own units, remapped about a half.</summary>
    /// <remarks>
    ///     ⚠ <b>A range of zero writes a flat half and a scale of zero, and that is the honest
    ///     answer.</b> A perfectly flat displacement or a sphere with no curvature to speak of would
    ///     otherwise divide by nothing; a half everywhere decodes to zero everywhere, which is what
    ///     was measured.
    /// </remarks>
    static MeshMapImage Signed(string mesh, MeshMapUsage usage, int size, IReadOnlyList<float> values, float range) {
        var pixels = Blank(size);
        var scale = range > 0f ? range : 0f;
        var inverse = scale > 0f ? 1f / scale : 0f;

        Fill(size, values.Count, (index, at) => {
            var level = Byte(0.5f + (0.5f * values[index] * inverse));
            pixels[at] = level;
            pixels[at + 1] = level;
            pixels[at + 2] = level;
        });

        return Made(mesh, usage, size, pixels, Common(TextureContent.Linear), scale);
    }

    static MeshMapImage Vector(
        string mesh,
        MeshMapUsage usage,
        int size,
        IReadOnlyList<Vector3> values,
        bool signed,
        TextureContent content
    ) {
        var pixels = Blank(size);

        Fill(size, values.Count, (index, at) => {
            var value = values[index];

            if (signed) {
                value = (value * 0.5f) + new Vector3(0.5f);
            }

            pixels[at] = Byte(value.X);
            pixels[at + 1] = Byte(value.Y);
            pixels[at + 2] = Byte(value.Z);
        });

        return Made(mesh, usage, size, pixels, Common(content), 0f);
    }

    /// <summary>The id map, coloured at the last possible moment.</summary>
    /// <remarks>
    ///     ⚠ <b><c>MapBaker.IdColour</c> is applied here and nowhere earlier</b>, which is the whole
    ///     of § D12's warning: an id is a label, the average of two labels is a third label, and a
    ///     map that has been through any filter at all grows a hairline of a material that does not
    ///     exist along every chart border. The bake dilates ids by copying a neighbour rather than
    ///     averaging, and this writes the colour after that.
    /// </remarks>
    static MeshMapImage Identifiers(string mesh, int size, IReadOnlyList<int> ids) {
        var pixels = Blank(size);

        Fill(size, ids.Count, (index, at) => {
            var colour = MapBaker.IdColour(ids[index]);
            pixels[at] = Byte(colour.X);
            pixels[at + 1] = Byte(colour.Y);
            pixels[at + 2] = Byte(colour.Z);
        });

        return Made(mesh, MeshMapUsage.Id, size, pixels, Common(TextureContent.Linear), 0f);
    }

    static MeshMapImage Made(
        string mesh,
        MeshMapUsage usage,
        int size,
        byte[] pixels,
        TextureImportSettings settings,
        float scale
    ) =>
        new() {
            Usage = usage,
            FileName = MeshMapNaming.FileName(mesh, usage),
            Png = PngCodec.Encode(new Bitmap(size, size, pixels)),
            Settings = settings,
            Scale = scale
        };

    /// <summary>Opaque black, which is what a texel no measurement reached looks like.</summary>
    static byte[] Blank(int size) {
        var pixels = new byte[size * size * 4];

        for (var at = 3; at < pixels.Length; at += 4) {
            pixels[at] = byte.MaxValue;
        }

        return pixels;
    }

    /// <summary>Walks the picture top-down and hands back the index of the texel under each pixel.</summary>
    /// <remarks>
    ///     The row flip lives here, once, rather than in each of the four encoders — which is the
    ///     point: a flip that is written out four times is a flip that is right three times.
    /// </remarks>
    static void Fill(int size, int available, Action<int, int> write) {
        for (var y = 0; y < size; y++) {
            var row = (size - 1 - y) * size;

            for (var x = 0; x < size; x++) {
                var index = row + x;

                if (index >= available) {
                    continue;
                }

                write(index, ((y * size) + x) * 4);
            }
        }
    }

    static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
}
