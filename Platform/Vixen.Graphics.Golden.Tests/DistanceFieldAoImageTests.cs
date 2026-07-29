// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The traced pass, drawing pixels for the first time.
/// </summary>
/// <remarks>
///     <para>
///         Everything about the distance-field path has so far been checked against something that
///         agrees with it: the bake against closed forms, the clipmap against the bake, the shader
///         against the CPU tracer by reading its text. <see cref="DistanceFieldDeviceTests" /> went one
///         step further and compiled the variant a frame asks for — but nothing had ever <i>run</i> the
///         shader's arithmetic, and the parts checked only against their own mirrors are the parts most
///         likely to be wrong.
///     </para>
///     <para>
///         <b>It starts with the null field, because that is the case with an exactly knowable answer.</b>
///         <c>NoDistanceField</c> reports that nothing is near, so the occlusion integral finds nothing
///         to occlude and the shadow march finds nothing to block: every pixel is <c>(1, 1, 0)</c>,
///         whatever the depth and normals happen to be. A shader that did not run gives black, and the
///         two are as far apart as a frame can be — which is what makes this worth doing before the
///         one with volumes in it.
///     </para>
///     <para>
///         Building it found the thing worth finding: <b>a full-screen pass had no way to fill a
///         compose slot at all</b>, so <c>DistanceFieldAo</c> could not be built by a compositor —
///         under any composition, traced or not. See <see cref="FullScreenRenderer.Composition" />.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class DistanceFieldAoImageTests {
    /// <summary>
    ///     A world with nothing in it is fully open and fully lit, and the pass says so in every pixel.
    /// </summary>
    [Fact]
    public void TheNullFieldIsOpenAndLitEverywhere() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, traced: false);

        for (var y = 0; y < image.Height; y += image.Height / 8) {
            for (var x = 0; x < image.Width; x += image.Width / 8) {
                var pixel = Pixel(image, x, y);

                // Occlusion in red, sun visibility in green, and nothing in blue — the pass keeps the
                // two apart on purpose, because pre-combining them darkens direct lighting with
                // ambient occlusion.
                Assert.True(pixel.X > 0.98f, $"({x}, {y}) came back occluded by nothing: {pixel}");
                Assert.True(pixel.Y > 0.98f, $"({x}, {y}) came back shadowed by nothing: {pixel}");
                Assert.True(pixel.Z < 0.02f, $"({x}, {y}) put something in blue: {pixel}");
            }
        }
    }

    /// <summary>
    ///     Without a sun there is no shadow march, and green is the constant the shader writes rather
    ///     than the answer to a question. The permutation is what makes the difference — with it off
    ///     the march is not merely skipped, it is not compiled — so this is the second variant of the
    ///     pass to have executed at all.
    /// </summary>
    [Fact]
    public void TheVariantWithoutASunAlsoDraws() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, traced: false, sun: false);
        var pixel = Pixel(image, image.Width / 2, image.Height / 2);

        Assert.True(pixel.X > 0.98f, $"the centre came back occluded by nothing: {pixel}");
        Assert.True(pixel.Y > 0.98f, $"the centre came back shadowed with no sun to shadow it: {pixel}");
    }

    /// <summary>
    ///     <b>A frame that traces an actual clipmap, which is what all of L1 was for.</b> A sphere of
    ///     radius one at the origin, a camera above it, and the pass reading the field the frame
    ///     composited and copied up a moment earlier — every part of the path at once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The assertion is on the sun channel, not the occlusion one, and that is not a
    ///         convenience.</b> A ray leaving a sphere's surface along the radius reads a clearance
    ///         exactly equal to the step it took, so the occlusion integral correctly finds nothing —
    ///         the same reason a flat floor occludes nothing at all, seen from the other side. The
    ///         shadow march is the term this scene actually asks a question of, and it answers: black
    ///         under the ball, lit at the corners.
    ///     </para>
    ///     <para>
    ///         The two ends rather than a value. Anything sharper would be asserting the tracer's
    ///         arithmetic, which <c>DistanceFieldTracerTests</c> already does against closed forms —
    ///         what is new here is that the arithmetic ran on a device against a field a frame
    ///         composited and copied up a pass earlier.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ATracedFrameSeesWhatTheFieldHolds() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, traced: true);

        var centre = Pixel(image, image.Width / 2, image.Height / 2);
        var corner = Pixel(image, 2, 2);

        Assert.True(centre.Y < 0.1f, $"the ball cast no shadow on what is under it: {centre}");
        Assert.True(corner.Y > 0.5f, $"the corner was shadowed by a ball nowhere near it: {corner}");
    }

    /// <summary>Builds one frame of the pass, and reads the picture back when it can be drawn.</summary>
    static Bitmap Render(Fixture fixture, bool traced, bool sun = true, bool draw = true) {
        var device = fixture.Device;

        // Nothing draws into these and nothing needs to: with the null field the answer does not
        // depend on them, which is exactly why this is the frame to start with.
        var depth = fixture.Owned("depth", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var normals = fixture.Owned("normals", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        using var scene = new SceneConstants(device) { Descriptors = allocator };

        // A ball hanging above the reconstructed plane, so the sun ray from the middle of the frame
        // hits it and the ones from the corners pass either side.
        using var clipmap = new GlobalDistanceFieldRenderer {
            Name = "GlobalDistanceField",
            Field = new(16, 4f, 4),
            SceneConstants = scene,
            ViewPosition = Vector3.Zero,
            Device = device,
            Instances = { new(Ball(0.8f), new(0f, 3f, 0.5f), Quaternion.Identity, 1f) }
        };

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // ⚠ Not the whole library, and it is the same reason ClusterCulling cannot have it: a source
        // set that holds the material tree compiles nothing without a material's composition, because
        // every slot the sources declare has to be bound whether or not this shader reaches it. What
        // this pass actually needs is what it imports, plus the full-screen vertex stage.
        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "DistanceFields"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "DistanceFieldAo.rvn")
                )
            )
        );

        // A pass that clears and draws nothing, which is all this needs: it exists so the graph moves
        // the two textures into a state the AO pass can sample, rather than leaving them UNDEFINED.
        var gbuffer = new RenderPassRenderer { Name = "GBuffer", ClearColour = new(0.5f, 0.5f, 1f, 1f) };

        gbuffer.ColourTargets.Add("Depth");
        gbuffer.ColourTargets.Add("Normals");

        using var ao = new DistanceFieldAoRenderer {
            Name = "DistanceFieldAo",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            Source = traced ? "GlobalDistanceField" : "NoDistanceField",
            SunShadow = sun,

            // Where the clipmap's own bindings live. Null for the untraced frame, which declares none
            // of them — and passing it anyway would be harmless, since a set the effect does not have
            // is a bind that does nothing.
            SceneConstants = traced ? scene : null,

            // Straight up, and the ball is straight up from the middle of the frame.
            SunDirection = new(0f, 1f, 0f),
            SunDistance = 8f,

            // Full resolution, so the picture read back is the pass's own output rather than an
            // upsample of it — there is no upsampling node here to do that honestly.
            Scale = 1f,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        // The clipmap only when something traces it: a frame with no clipmap is the null-field case,
        // and putting one in anyway would upload volumes nothing reads.
        var sequence = new SceneRendererSequence { Children = { gbuffer } };

        if (traced) {
            sequence.Children.Add(clipmap);
        }

        sequence.Children.Add(ao);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = sequence
        };

        compositor.Imports["Depth"] = new(depth.Texture, depth.View, depth.Description);
        compositor.Imports["Normals"] = new(normals.Texture, normals.View, normals.Description);

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();

        var frame = compositor.Build(fixture.Graph, effects, device);

        // ⚠ Asserted rather than assumed, and it is the assertion that would have caught the missing
        // composition: an effect the system cannot resolve is a miss, and a node that got no effect
        // draws nothing — which is a picture indistinguishable from a pass nobody scheduled.
        Assert.Empty(effects.Misses);
        Assert.True(ao.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        var picture = draw ? fixture.Render(frame.Texture("harness", "Display")) : default;

        if (draw && traced) {
            // ⚠ Asserted rather than assumed, and for the reason set 0 exists to be asserted about: a
            // set that fell one binding short is not bound at all, and the pass then traces whatever
            // set 0 held before — which here is nothing, and reads as a field with no geometry in it.
            Assert.True(clipmap.Composites > 0, "the clipmap never composited, so there was nothing to trace");
            Assert.True(scene.IsComplete, "set 0 was left incomplete, so the frame bound none of it");
            Assert.True(scene.WriteCount > 0, "set 0 was never written");
        }

        return picture;
    }

    /// <summary>A sphere's field, written from its own equation rather than baked from triangles.</summary>
    /// <remarks>
    ///     The bake has its own tests against exactly this closed form; using it here would put a
    ///     tessellation's error into a picture whose point is that the <i>path</i> works.
    /// </remarks>
    static MeshDistanceField Ball(float radius, int resolution = 16) {
        var extent = radius * 2f;
        var bounds = new BoundingBox(new(-extent), new(extent));
        var distances = new float[resolution * resolution * resolution];
        var field = new MeshDistanceField(bounds, new(resolution), distances);

        for (var z = 0; z < resolution; z++) {
            for (var y = 0; y < resolution; y++) {
                for (var x = 0; x < resolution; x++) {
                    distances[x + (resolution * (y + (resolution * z)))] =
                        field.PositionOf(x, y, z).Length() - radius;
                }
            }
        }

        return field;
    }

    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
