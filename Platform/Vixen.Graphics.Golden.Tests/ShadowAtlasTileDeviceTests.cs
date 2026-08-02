// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Where a viewport lands in a texture, and whether the atlas fold agrees with it.
/// </summary>
/// <remarks>
///     <para>
///         <b>One fact, settled on a device because deriving it has failed twice.</b> A shadow atlas
///         is two halves that never meet in code: <see cref="ShadowCascades.TileViewport" /> decides
///         where a cascade is <em>rendered</em>, and <see cref="ShadowCascades.AtlasProjection" />
///         decides where it is <em>read</em>. Nothing checks that they agree, and the failure is
///         invisible in every other test — a lookup landing in the wrong tile reads a real depth,
///         from a map fitted to a different centre at a different scale, so the picture has shadows
///         in it and they are in the wrong place.
///     </para>
///     <para>
///         The question underneath is one line of the backend:
///         <c>VulkanCommandList.SetViewport</c> submits <c>Y = viewport.Y + viewport.Height</c> with
///         a <em>negative</em> height, to land the engine's y-up clip space. Whether that leaves a
///         viewport at <c>y = 0</c> covering the top row of the image or the bottom one decides the
///         sign of the fold's y translation, and it is not something an argument should be trusted
///         with: this was got wrong once already, in the direction that made cascade zero read
///         cascade two's tile.
///     </para>
///     <para>
///         So the test paints, rather than reasons. It draws into one tile at a time and reads the
///         image back, which answers where the viewport went in the only terms that matter — texels,
///         from the top-left, as a sampler addresses them.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class ShadowAtlasTileDeviceTests {
    const int Side = Fixture.Side;

    /// <summary>Four tiles in a 2 × 2 grid over the fixture's target.</summary>
    const int Cascades = 4;

    const int Resolution = Side / 2;

    static bool TryOpen(out Fixture? fixture, out string? reason) {
        if (Fixture.TryOpen(out fixture, out reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so this may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>
    ///     A viewport covers the texels its rectangle names, counted from the top-left.
    /// </summary>
    /// <remarks>
    ///     The half of the contract the backend owns. Everything the atlas does rests on it and
    ///     nothing else in the suite states it, because every other fixture renders to the whole
    ///     target — where a y flip is invisible in a symmetric picture and catastrophic in a tiled
    ///     one.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_tile_viewport_covers_the_texels_it_names(int index) {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var painted = Paint(owned, index);
        var expected = ShadowCascades.TileViewport(index, Cascades, Resolution);

        Assert.NotEmpty(painted);

        foreach (var (x, y) in painted) {
            Assert.InRange(x, (int)expected.X, (int)(expected.X + expected.Width) - 1);
            Assert.InRange(y, (int)expected.Y, (int)(expected.Y + expected.Height) - 1);
        }
    }

    /// <summary>
    ///     And the fold reads the tile the viewport painted.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two halves joined, which is the assertion that has never existed. A world point
    ///         inside a cascade is projected through <see cref="ShadowCascades.AtlasProjection" /> and
    ///         converted to a UV exactly as <c>Transform.NdcToUv</c> does — negation included, which
    ///         is the step a CPU-side test computed its own way and therefore never checked. The
    ///         texel it names has to be one the viewport actually wrote.
    ///     </para>
    ///     <para>
    ///         A single tile is drawn per case, so a lookup landing anywhere else lands on cleared
    ///         black and the assertion says which tile it found instead of a bare inequality.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_fold_reads_the_tile_the_viewport_painted(int index) {
        if (!TryOpen(out var fixture, out _)) {
            return;
        }

        using var owned = fixture!;
        var painted = Paint(owned, index);

        Assert.NotEmpty(painted);

        // A cascade fitted to a camera at the origin, and the point at its own centre — which is
        // inside it by construction, whatever the fit decided.
        var cascade = ShadowCascades.Fit(
            Vector3.Zero,
            new(0f, 0f, 1f),
            new(0f, 1f, 0f),
            Vector3.Normalize(new(-0.4f, -1f, -0.3f)),
            MathF.PI / 3f,
            16f / 9f,
            1f,
            50f,
            Resolution
        );

        var atlas = ShadowCascades.AtlasProjection(cascade, index, Cascades);
        var clip = Matrix4x4.TransformVector4(new(cascade.Centre, 1f), atlas);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);

        // Transform.NdcToUv, negation and all.
        var uv = new Vector2((ndc.X * 0.5f) + 0.5f, (-ndc.Y * 0.5f) + 0.5f);
        var texel = (X: (int)(uv.X * Side), Y: (int)(uv.Y * Side));

        Assert.True(
            painted.Contains(texel),
            $"cascade {index} was drawn into {ShadowCascades.TileViewport(index, Cascades, Resolution)} "
            + $"and its own centre reads texel {texel}, which is not one of them"
        );
    }

    /// <summary>Draws one tile and returns every texel that came back non-black.</summary>
    /// <remarks>
    ///     The scissor as well as the viewport, because that is what a shadow atlas sets — and a
    ///     triangle crossing a tile edge with no scissor would write a neighbour's texels, which is
    ///     the artefact the scissor exists to prevent and would make this test agree for the wrong
    ///     reason.
    /// </remarks>
    static HashSet<(int X, int Y)> Paint(Fixture fixture, int index) {
        var colour = fixture.ColourTarget($"atlas tile {index}");

        var pipeline = fixture.Pipeline(
            fixture.Shader("triangle.vert.spv", ShaderStage.Vertex),
            fixture.Shader("triangle.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled
        );

        var viewport = ShadowCascades.TileViewport(index, Cascades, Resolution);

        fixture.Graph.AddPass($"tile {index}", pass => {
            pass.ColourAttachment(colour, LoadAction.Clear, new(0f, 0f, 0f, 1f));
            pass.SideEffect();

            pass.Execute(context => {
                context.CommandList.SetViewport(viewport);

                context.CommandList.SetScissor(
                    new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height)
                );

                context.CommandList.BindPipeline(pipeline);
                context.CommandList.Draw(3);
            });
        });

        var bitmap = fixture.Render(colour);
        var painted = new HashSet<(int X, int Y)>();

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var offset = ((y * Side) + x) * 4;

                if (bitmap.Pixels[offset] > 8 || bitmap.Pixels[offset + 1] > 8 || bitmap.Pixels[offset + 2] > 8) {
                    painted.Add((x, y));
                }
            }
        }

        return painted;
    }
}
