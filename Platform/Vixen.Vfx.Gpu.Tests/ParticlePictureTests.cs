// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>
///     <see cref="ParticlePicture" /> draws the thing it says it draws.
/// </summary>
/// <remarks>
///     <para>
///         <b>The harness's own test, and it is a picture for the same reason the frame it will be
///         used to compare is.</b> Everything between a <see cref="VfxSystem" /> and a pixel here
///         fails silently: a vertex attribute bound at the wrong offset draws a quad in the texture
///         coordinate's colours, a set allocated from the wrong layout is a draw the driver skips,
///         and a uniform member written at the wrong std140 offset is a sprite at the wrong
///         brightness. None of them throws, and all of them are visible in one pixel.
///     </para>
///     <para>
///         It is <c>ParticleSpriteDeviceTests.AParticleSystemReachesTheScreen</c>, against a harness
///         that reaches the device through the RHI rather than through the compositor — so the two
///         projects assert the same claim about the same shader by two different routes, which is
///         what makes either of them evidence that the shader is right rather than that one host is.
///     </para>
/// </remarks>
public sealed class ParticlePictureTests {
    /// <summary>How far in front of the camera the particle sits.</summary>
    const float Depth = -6f;

    const int Side = 128;

    /// <summary>A <see cref="VfxSystem" />'s particles reach the picture, in their own colour.</summary>
    [Fact]
    public void One_orange_particle_lands_in_the_middle_of_the_frame() {
        VulkanRequirement.Available(VulkanDevice.TryCreate(new(), out var device, out var reason), reason);

        using var owned = device!;
        VulkanDiagnostics.Reset();

        using var effect = Effect();

        var pixels = ParticlePicture.Render(owned, effect, Camera, out var particles, Side);

        // ⚠ Asserted rather than assumed. A picture that is nothing but the clear is what a broken
        // pipeline produces *and* what an effect with nothing alive in it produces, and only this
        // tells them apart.
        Assert.Equal(1, particles);

        var corner = ParticlePicture.Pixel(pixels, Side, 2, 2);

        Assert.True(corner.Z > 0.2f && corner.X < 0.05f, $"the pass did not clear: {corner}");

        var centre = ParticlePicture.Pixel(pixels, Side, Side / 2, Side / 2);

        Assert.True(centre.X > 0.2f, $"nothing was drawn where the particle is: {centre}");

        // Orange, which is what the graph set — and more red than green, which a colour read out of
        // the texture coordinate's bytes cannot reliably be. The blue is the clear showing through
        // the additive blend, which is why the margin against it is the narrower of the two.
        Assert.True(centre.X > centre.Y * 1.4f, $"the sprite is not the colour the effect set: {centre}");
        Assert.True(centre.X > centre.Z * 2f, $"the sprite is not the colour the effect set: {centre}");

        Assert.True(
            VulkanDiagnostics.ErrorCount == 0,
            "Drawing produced validation errors: " + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
        );
    }

    /// <summary>The camera the quads are expanded against and projected through.</summary>
    static RenderCamera Camera => RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

    /// <summary>One large orange particle in front of the camera, stepped once.</summary>
    /// <remarks>
    ///     <para>
    ///         A burst rather than a rate, because a rate spawns from the elapsed time and one step of
    ///         a sixtieth of a second at any sane rate is zero particles. A metre across because the
    ///         assertion is about one pixel in the middle of a 128-pixel picture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Exactly one, and at the sphere's centre.</b> The blend is additive, so sixty-four
    ///         overlapping orange sprites sum past one in every channel and come back as white — which
    ///         passes "something is there" and fails "it is the colour the effect set" for a reason
    ///         that has nothing to do with the shader.
    ///     </para>
    /// </remarks>
    static VfxSystem Effect() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, Depth, 0f)),
                new(VfxOpcode.SetSize, new Vector4(1f, 1f, 0f, 0f)),
                new(VfxOpcode.SetColour, new Vector4(1f, 0.45f, 0.08f, 1f)),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            256,
            VfxRenderer.Billboard
        );

        var effect = new VfxSystem(graph);

        effect.Step(1f / 60f);

        return effect;
    }
}
