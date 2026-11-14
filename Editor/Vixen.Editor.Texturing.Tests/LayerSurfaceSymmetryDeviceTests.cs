// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Graphics.Vulkan;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Patina #132: a path's symmetry, evaluated through the stack on a real device, against a closed form.</summary>
/// <remarks>
///     <b>The fixture is a flat sheet</b>, as <c>TextureSurfaceProjectionDeviceTests</c>' is: the <c>position</c> bake
///     runs 0 to 1 across the columns and down the rows, and the <c>world</c> bake faces out of it everywhere. A path down
///     the column a fifth of the way across, mirrored through the plane half way across, is a band down that column and
///     one down the column four fifths across, and nothing between.
/// </remarks>
public class LayerSurfaceSymmetryDeviceTests(ITestOutputHelper output) {
    const int Side = 32;

    static LayerStackAsset Stack(LayerSurfaceSymmetry? symmetry) =>
        new() {
            Name = "Mirror",
            BaseWidth = Side,
            BaseHeight = Side,
            Sets = [
                new() {
                    Name = "S",
                    Channels = [new() { Usage = "baseColor", Default = [0f, 0f, 0f, 1f] }],
                    Layers = [
                        new() {
                            Id = "path",
                            Kind = LayerKind.Fill,
                            Values = { ["baseColor"] = [1f, 1f, 1f, 1f] },
                            Projection = LayerProjection.Path,
                            SurfacePath = new() {
                                Points = [new(0.2f, 0.1f, 0.5f, 0f, 0f, 1f), new(0.2f, 0.9f, 0.5f, 0f, 0f, 1f)],
                                Radius = 0.06f,
                                Hardness = 1f,
                                Symmetry = symmetry
                            }
                        }
                    ]
                }
            ]
        };

    /// <summary>
    ///     ⚠ The mirrored copy covers the reflected column and the middle stays clear; without the symmetry the same
    ///     column is clear, so the copy is the symmetry's doing and not a band that was always there.
    /// </summary>
    [Fact]
    public void A_mirrored_path_covers_its_reflection_and_nothing_between() {
        using var device = TexturingDevice.Open();

        output.WriteLine($"adapter: {TexturingDevice.Adapter(device)}");

        var mirrored = Bake(device, Stack(new() { Axis = [1f, 0f, 0f], Through = [0.5f, 0f, 0f] }));
        var alone = Bake(device, Stack(null));
        var (drawn, reflected, middle) = (Column(0.2f), Column(0.8f), Column(0.5f));
        var row = Side / 2;

        Assert.True(Red(mirrored, drawn, row) > 250, $"the drawn path baked {Red(mirrored, drawn, row)}");
        Assert.True(Red(mirrored, reflected, row) > 250, $"its reflection baked {Red(mirrored, reflected, row)}");
        Assert.True(Red(mirrored, middle, row) < 5, $"between them baked {Red(mirrored, middle, row)}");
        Assert.True(Red(alone, drawn, row) > 250, $"the path alone baked {Red(alone, drawn, row)}");
        Assert.True(
            Red(alone, reflected, row) < 5,
            $"with no symmetry its reflection baked {Red(alone, reflected, row)}"
        );
    }

    /// <summary>The texel whose place on the sheet is nearest a share of the way across.</summary>
    static int Column(float across) => (int)MathF.Round(across * (Side - 1));

    static byte Red(Bitmap picture, int x, int y) => picture.Pixels[(y * Side + x) * 4];

    static Bitmap Bake(VulkanDevice device, LayerStackAsset stack) {
        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        Assert.Empty(compilation.Problems);
        Assert.NotNull(compilation.Plan);

        var plan = compilation.Plan;

        using TextureUploads uploads = new(device);

        foreach (var external in compilation.Externals) {
            switch (external.Asset) {
                case "":
                    uploads.Add(plan, external.Image, external.Width, external.Height, external.Texels.AsSpan());

                    break;
                case "meshmap:position":
                    uploads.AddPicture(plan, external.Image, Side, Side, Sheet(), TextureFormat.Rgba8);

                    break;
                case "meshmap:world":
                    uploads.AddPicture(plan, external.Image, Side, Side, Facing(), TextureFormat.Rgba8);

                    break;
                default:
                    Assert.Fail($"the plan asks for '{external.Asset}', which this sheet has no picture for.");

                    break;
            }
        }

        using TexturePlanEvaluator evaluator = new(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        return bake.Read(LayerStackDifferential.ImageOf(compilation, "baseColor"));
    }

    /// <summary>x across the columns, y down the rows, z a half.</summary>
    static byte[] Sheet() =>
        Texels(static (x, y) => ((byte)(x * 255 / (Side - 1)), (byte)(y * 255 / (Side - 1)), (byte)128));

    /// <summary>Out of the sheet everywhere, signed as the <c>world</c> bake writes it.</summary>
    static byte[] Facing() => Texels(static (_, _) => ((byte)128, (byte)128, (byte)255));

    static byte[] Texels(Func<int, int, (byte Red, byte Green, byte Blue)> texel) {
        var texels = new byte[Side * Side * 4];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var (red, green, blue) = texel(x, y);
                var at = (y * Side + x) * 4;

                (texels[at], texels[at + 1], texels[at + 2], texels[at + 3]) = (red, green, blue, 255);
            }
        }

        return texels;
    }
}
