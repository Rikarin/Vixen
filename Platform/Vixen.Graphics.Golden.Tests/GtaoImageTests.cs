// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The horizon integral, against surfaces whose occlusion is known in closed form.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>Ssao.rvn</c> was not GTAO and its own header said it was.</b> What it ran was the
///         horizon-<em>based</em> estimator — the largest cosine of a sample's elevation above the
///         tangent plane, averaged over the directions and subtracted from one. For a single occluder
///         at elevation <c>e</c> that gives <c>1 − sin(e)/2</c>, where the cosine-weighted visibility
///         is <c>1 − sin²(e)/2</c>: equal at nought and at ninety degrees and nowhere between.
///         <c>docs/plan/06</c>'s table was right that the integral was owed and this repository's
///         audit was wrong that it had landed.
///     </para>
///     <para>
///         <b>What makes the integral worth asserting is not accuracy at a horizon but the
///         normalisation.</b> Each slice is weighted by how much of the surface normal lies in it, and
///         the weights sum so that an <em>unoccluded</em> surface reads exactly one whatever direction
///         it faces — at eight directions, to four decimal places, at every tilt. That is a closed-form
///         answer a picture can be held to, and it is what the two flat-plane fixtures below are.
///     </para>
///     <para>
///         ⚠ <b>Neither flat-plane fixture tells the two estimators apart, and saying so is the
///         point.</b> On any flat surface every sample lies in the tangent plane, so the old
///         estimator's elevation term is nought and it reads one as well — both of these were run
///         against the previous shader and both passed. What they discriminate against is a
///         <em>wrong integral</em>, and both sabotages were run. Lifting the screen direction into
///         view space without negating y mirrors every slice: the y-tilted plane drops to 0.745 and
///         four of the five fixtures here go red. Removing the hemisphere clamp turns every one of
///         them red, with the corner scene's open floor reading 0.245 — the halo, arriving exactly
///         where the fixture says to look for it.
///     </para>
///     <para>
///         ⚠ One sabotage they do <em>not</em> catch, and it is worth writing down: dropping the
///         projected normal's length from the accumulation leaves an unoccluded plane reading 1.26,
///         which <c>saturate</c> clips back to one. The flat fixtures see nothing. What catches it is
///         the corner, where the same over-weighting saturates the occlusion away and the contact
///         reads 1.000 — so the occluded fixture is load-bearing for the unoccluded claim.
///     </para>
///     <para>
///         <b>What separates the two estimators is measured rather than asserted.</b> The corner
///         scene below, rendered through both on MoltenVK:
///     </para>
///     <para>
///         <c>row 90 (wall, 7 cm up): 0.676 → 0.781 · row 92 (the corner): 0.754 → 0.713 ·
///         row 96 (floor, 59 cm out): 0.842 → 0.884 · row 100 (96 cm): 0.880 → 0.925 ·
///         row 126 (2.2 m): 0.950 → 0.944 · whole frame: 0.9441 → 0.9653</c>
///     </para>
///     <para>
///         Which is the predicted signature and not a wash: <c>1 − sin(e)/2</c> against
///         <c>1 − sin²(e)/2</c> over-darkens everywhere the elevation is moderate — the wall face and
///         the floor either side — and <em>under</em>-darkens at the one place the elevation is near
///         ninety degrees, which is the contact itself. So the corner got darker by four points while
///         everything around it got lighter by two to eleven, and the far field did not move. The
///         worst single pixel moved forty levels of two hundred and fifty five.
///     </para>
///     <para>
///         <b>And what it comes to in a real frame, which is a smaller number than the corner scene
///         suggests.</b> <c>StandardFrameTierImageTests.ASplitFrameLooksLikeItsReference</c> renders a
///         whole High-tier frame with the ambient split on, and its committed reference was recorded
///         under the old estimator — so re-rendering it against the new one is a free A/B over lit
///         geometry rather than over a fixture. <b>20.7% of the frame moves, by a mean of 0.230 of
///         255, with not one pixel past 12 levels.</b> <c>Tolerance.Shaded</c> allows a mean of 0.350,
///         so that reference did not have to be re-recorded and no golden in the suite did.
///     </para>
///     <para>
///         ⚠ That is the honest scale of it, and the reason is structural rather than disappointing:
///         the pass runs at half resolution, its answer multiplies the ambient term only, and a 128²
///         frame of boxes has few tight contacts. Where the estimator differs is exactly where
///         geometry meets geometry, which is what the corner scene isolates and what a wide shot
///         averages away.
///     </para>
///     <para>
///         ⚠ <b>Run on the leg that will actually judge them.</b> The macOS and Windows runners have
///         no Vulkan driver, so every fixture here skips on both and a reference generated on a Mac is
///         checked in exactly one place. These five were run under lavapipe in the container the
///         suite's README describes, at the commit that added them: five passed, and the corner
///         reference is within its tolerance on both drivers rather than on the one that made it.
///     </para>
///     <para>
///         The planes are staged as <c>R32Float</c> depth and <c>Rgba32Float</c> normals rather than
///         as the eight-bit textures the other fixtures here use, and that is not fastidiousness: two
///         hundred and fifty six depth levels across a tilted plane is a staircase, and a screen-space
///         effect that reconstructs positions from it finds a real horizon at every step. The fixture
///         would be handing the shader a flight of stairs and asserting that it saw a plane.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class GtaoImageTests {
    const int Side = Fixture.Side;
    const float Near = 0.1f;
    const float Far = 100f;
    const float FieldOfView = MathF.PI / 3f;

    /// <summary>How far the corner case's floor is below the camera.</summary>
    const float FloorDrop = 1f;

    /// <summary>And how far in front of it the wall stands.</summary>
    const float WallDistance = 4f;

    /// <summary>
    ///     What the corner scene multiplies the radius by before fading an occluder out, over the
    ///     shipped one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Stated rather than inherited. <c>falloff</c> is the multiple of the radius over which an
    ///     occluder's weight falls linearly to nothing, so at the shipped value one halfway out
    ///     already counts half. This scene's wall stands where the floor is receding steeply, which
    ///     puts the samples that matter well out along the march — at the default the contact reads
    ///     0.94 and there is no signal left to assert about. The flat planes keep the shipped value,
    ///     so what they say about the normalisation, they say about what ships.
    /// </remarks>
    const float WallFalloff = 3f;

    /// <summary>Opens a device, or skips — unless the environment promised one.</summary>
    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>
    ///     A plane square to the camera occludes nothing, and the pass says so in every pixel.
    /// </summary>
    /// <remarks>
    ///     The floor of the suite rather than its point: it catches a pass that did not run, a
    ///     descriptor set written short, an integral that came back <c>NaN</c>, and a sign that turned
    ///     visibility into occlusion. It does not discriminate between estimators — see the class
    ///     remarks — which is what the next one is for.
    /// </remarks>
    [Fact]
    public void AFlatPlaneFacingTheCameraIsCompletelyUnoccluded() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, new(0f, 0f, 1f));

        AssertUniform(image, 1f, 0.02f, "a plane square to the camera");
    }

    /// <summary>
    ///     <b>And so does one at forty-five degrees, which is the claim the integral is for.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An unoccluded surface has to read exactly one at every tilt, and that only falls out if
    ///         each slice is weighted by how much of the normal lies in it. The weights are what turn
    ///         a per-slice arc that is <em>larger</em> than one at a tilt — <c>cos γ + γ sin γ</c>, so
    ///         1.26 at forty-five degrees — back into a whole that is exactly one. ⚠ It is the
    ///         <em>integral</em> this discriminates against getting wrong, not the estimator it
    ///         replaced; see the class remarks.
    ///     </para>
    ///     <para>
    ///         ⚠ A steeper plane than this would be a fixture about the depth buffer rather than about
    ///         the estimator: at seventy-five degrees a screen texel spans several metres of surface
    ///         and the march's own quantisation dominates. Forty-five is where the weighting is far
    ///         from one and the reconstruction is still exact.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AFlatPlaneAtFortyFiveDegreesIsAlsoCompletelyUnoccluded() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, Vector3.Normalize(new(1f, 0f, 1f)));

        AssertUniform(image, 1f, 0.04f, "a plane tilted forty-five degrees");
    }

    /// <summary>And tilted the other way, because a sign error is symmetric about nothing.</summary>
    /// <remarks>
    ///     The pair is what pins the lift's <c>-y</c>. A slice direction lifted into view space with
    ///     the sign of y copied rather than negated mirrors every slice about the horizontal, which is
    ///     invisible on a plane tilted in x and moves the answer on one tilted in y — so this is the
    ///     one of the two that would have caught it.
    /// </remarks>
    [Fact]
    public void AndTiltedTheOtherWay() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, Vector3.Normalize(new(0f, -1f, 1f)));

        AssertUniform(image, 1f, 0.04f, "a plane tilted forty-five degrees about the other axis");
    }

    /// <summary>
    ///     A wall standing on the floor darkens the floor beside it, and leaves the floor far from it
    ///     nearly alone.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>An ordering, not a closed form, and the distinction is the honest part.</b> The
    ///         cosine-weighted visibility at the foot of an infinite wall is exactly a half, and this
    ///         fixture does not assert it: the march is discrete, <c>bias</c> rejects the samples
    ///         closest in, and the falloff weights an occluder down by how far away it is — all three
    ///         deliberately, and all three lighten a contact. Asserting a half here would be asserting
    ///         that those three choices cancel, which they do not and are not meant to.
    ///     </para>
    ///     <para>
    ///         What is asserted is what the estimator is <em>for</em>: the corner is markedly dark, the
    ///         floor two metres from the wall is nearly open, and the gap between them is large enough
    ///         that no amount of driver rounding closes it. Measured on MoltenVK: 0.713 at the corner
    ///         row, 0.833 a third of a metre from it, 0.944 two metres away.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The far-field half is the one worth having.</b> Screen-space occlusion's
    ///         characteristic failure is not a contact that is too light, it is the halo — occlusion
    ///         smeared across a surface nowhere near the occluder, from horizons the surface cannot
    ///         actually see. The hemisphere clamp in the shader is what bounds it, and the 5.6% that
    ///         remains at two metres is the falloff below reaching further than the geometry does, not
    ///         a horizon behind the surface.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AWallDarkensItsContactAndNothingFarFromIt() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, new(0f, 0f, 1f), wall: true, falloff: WallFalloff);

        // The corner projects to row 92 — the floor is a metre down and the wall four metres out, and
        // this projection puts their meeting there. Below it is floor running back toward the camera,
        // so row 94 is a third of a metre from the corner and row 126 is over two metres from it,
        // which is more than twice the search radius.
        var contact = Band(image, 94);
        var open = Band(image, 126);

        var corner = Band(image, 92);

        Assert.True(corner < 0.80f, $"the corner itself came back at {corner:F3}, barely occluded");
        Assert.True(contact < 0.88f, $"the floor beside the wall came back at {contact:F3}, barely occluded");
        Assert.True(open > 0.92f, $"floor two metres from the wall came back at {open:F3} — that is the halo");

        Assert.True(
            open - contact > 0.06f,
            $"the contact ({contact:F3}) and the open floor ({open:F3}) are the same brightness, so "
            + "whatever darkened one darkened both — which is a constant, not an occluder."
        );
    }

    /// <summary>The picture, so the shape of the contact shadow is pinned and not only its ends.</summary>
    /// <remarks>
    ///     ⚠ Its tolerance is not <see cref="Tolerance.Edges" />. The signal is a soft gradient across
    ///     a band tens of pixels wide, so a per-pixel bound that admits a fixed handful of outliers
    ///     says nothing about it; what matters is that the whole band keeps its shape, which is what
    ///     the mean is for.
    /// </remarks>
    [Fact]
    public void TheContactShadowKeepsItsShape() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        GoldenImage.Verify(
            "gtao-wall-contact",
            Render(owned, new(0f, 0f, 1f), wall: true, falloff: WallFalloff),
            new(10, 0.02, 0.5)
        );
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One plane through the middle of the view, at the given view-space normal, with an optional
    ///     wall standing on it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The depth is computed by projecting the plane rather than written by a shader, so the
    ///         surface the fixture means and the surface the pass reconstructs are the same surface by
    ///         construction. Both directions go through the library's own <c>PerspectiveFieldOfView</c>
    ///         — a matrix written out here would be the fixture's opinion about the reverse-Z
    ///         convention, and the opinion is exactly what would get encoded.
    ///     </para>
    ///     <para>
    ///         ⚠ The camera is at the origin looking down −Z, so the view matrix is the identity and
    ///         the world normals the fixture stages are also the view normals the shader wants. That is
    ///         what lets a normal be asserted about at all: through a non-trivial view matrix, a
    ///         mistake in the rotation and a mistake in the estimator are the same picture.
    ///     </para>
    /// </remarks>
    static Bitmap Render(Fixture fixture, Vector3 normal, bool wall = false, float falloff = 1f) {
        var device = fixture.Device;
        var projection = Matrix4x4.PerspectiveFieldOfView(FieldOfView, 1f, Near, Far);
        Assert.True(Matrix4x4.Invert(projection, out var inverse));

        var depths = new float[Side * Side];
        var normals = new Vector4[Side * Side];

        // The plane, three metres in front of the camera. Its normal is a view-space direction and
        // −Z is forward, so a normal with a positive z faces the camera.
        var through = new Vector3(0f, 0f, -3f);

        // ⚠ And the wall, which is a *floor* meeting one and not a second facing plane. A plane at
        // right angles to a plane the camera looks straight at is a plane the camera sees edge-on: it
        // projects to a line and occupies no pixels. So the wall case replaces the whole scene with a
        // floor a metre below the camera, running back to a wall four metres out — the arrangement the
        // corner case actually is, and the one where the two surfaces both cover real area.
        var floorNormal = new Vector3(0f, 1f, 0f);
        var floorThrough = new Vector3(0f, -FloorDrop, 0f);
        var wallNormal = new Vector3(0f, 0f, 1f);
        var wallThrough = new Vector3(0f, 0f, -WallDistance);

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var uv = new Vector2((x + 0.5f) / Side, (y + 0.5f) / Side);

                // Clip y = +1 is the TOP and texel row zero is the top row, so the fold negates y.
                var ndc = new Vector3((uv.X * 2f) - 1f, 1f - (uv.Y * 2f), 0.5f);
                var ray = Unproject(inverse, ndc);

                Vector3 hit;
                Vector3 surface;

                if (wall) {
                    // Whichever the ray meets first. A ray heading up never meets the floor at all,
                    // which is what makes the wall fill the top of the frame.
                    var toWall = Distance(ray, wallThrough, wallNormal);
                    var toFloor = Distance(ray, floorThrough, floorNormal);
                    var onFloor = toFloor > 0f && toFloor < toWall;

                    hit = ray * (onFloor ? toFloor : toWall);
                    surface = onFloor ? floorNormal : wallNormal;
                } else {
                    hit = ray * Distance(ray, through, normal);
                    surface = normal;
                }

                var index = (y * Side) + x;
                depths[index] = DeviceDepth(projection, hit);
                normals[index] = new(surface, 0f);
            }
        }

        var depthPlane = fixture.Sampled("depth", Side, MemoryMarshal.AsBytes<float>(depths), PixelFormat.R32Float);

        var normalPlane = fixture.Sampled(
            "normals",
            Side,
            MemoryMarshal.AsBytes<Vector4>(normals),
            PixelFormat.Rgba32Float
        );

        var display = fixture.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(
                    ["Core", "Geometry"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "Ssao.rvn")
                )
            )
        );

        var view = new RenderView("camera") {
            Camera = new(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f), FieldOfView, 1f, Near, Far)
        };

        using var ao = new AmbientOcclusionRenderer {
            Name = "Ssao",
            Depth = "Depth",
            Normals = "Normals",
            Output = "Display",
            View = view,

            // Full resolution, so what is read back is the pass's own answer rather than an upsample
            // of it — there is no upsampling node here to do that honestly.
            Scale = 1f,

            // A metre, rather than the shipped half. The corner scene's floor recedes fast — the row
            // below the corner is already a third of a metre from the wall and eight rows down is
            // most of one — so at the default radius the contact band is a handful of rows and the
            // fixture would be asserting about where a gradient crosses a threshold. A metre makes
            // the band tens of rows wide and still leaves the bottom of the picture outside every
            // search, which is the half that catches a halo.
            Radius = 1f,
            // The corner scene raises this; see there. The flat planes keep the shipped one, so
            // what they assert about the normalisation is asserted at the settings that ship.
            Falloff = falloff,

            // One, so the picture is the estimator's own answer. `intensity` is a power the artist
            // dials, and a fixture that took anything else would be asserting about `pow`.
            Intensity = 1f,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Side, Side),
            Game = ao
        };

        compositor.Imports["Depth"] = Import(depthPlane, "depth", PixelFormat.R32Float);
        compositor.Imports["Normals"] = Import(normalPlane, "normals", PixelFormat.Rgba32Float);

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();

        var frame = compositor.Build(fixture.Graph, effects, device);

        // ⚠ Asserted rather than assumed: an effect the system cannot resolve is a miss, and a node
        // that got no effect draws nothing — a picture indistinguishable from a pass nobody scheduled.
        Assert.Empty(effects.Misses);
        Assert.True(ao.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        return fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                Upload(commands, depthPlane);
                Upload(commands, normalPlane);
            }
        );
    }

    /// <summary>The view-space direction through a point on the near-to-far axis at a given NDC.</summary>
    static Vector3 Unproject(Matrix4x4 inverse, Vector3 ndc) {
        var clip = new Vector4(ndc, 1f) * inverse;
        return Vector3.Normalize(new Vector3(clip.X, clip.Y, clip.Z) / clip.W);
    }

    /// <summary>How far along a ray from the origin the plane is, or a negative for never.</summary>
    static float Distance(Vector3 ray, Vector3 through, Vector3 normal) {
        var facing = Vector3.Dot(ray, normal);

        return MathF.Abs(facing) < 1e-6f ? -1f : Vector3.Dot(through, normal) / facing;
    }

    /// <summary>The device depth a view-space point has. ⚠ Reversed-Z: near is one and far is zero.</summary>
    static float DeviceDepth(Matrix4x4 projection, Vector3 position) {
        var clip = new Vector4(position, 1f) * projection;
        return clip.Z / clip.W;
    }

    static ImportedTexture Import(
        (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) plane,
        string name,
        PixelFormat format
    ) =>
        new(
            plane.Texture,
            plane.View,
            new(format, Side, Side, TextureUsage.Sampled | TextureUsage.CopyDestination, Name: name),
            ResourceState.ShaderRead
        );

    static void Upload(
        ICommandList commands,
        (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) plane
    ) {
        commands.Barrier(new([], [new(plane.Texture, ResourceState.Undefined, ResourceState.CopyDestination)]));
        commands.CopyBufferToTexture(plane.Staging, 0, new(plane.Texture), new(Side, Side, 1));
        commands.Barrier(new([], [new(plane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)]));
    }

    /// <summary>Every pixel, against one value.</summary>
    /// <remarks>
    ///     ⚠ The border is skipped, and only the border. A search that walks off the viewport stops
    ///     early — <c>Fullscreen.InBounds</c> breaks the march — so the outermost pixels see fewer
    ///     samples than the middle ones, which is a property of every screen-space effect there is and
    ///     not of this one. What is asserted is the interior, which is all of the picture that has a
    ///     closed-form answer.
    /// </remarks>
    static void AssertUniform(in Bitmap image, float expected, float tolerance, string what) {
        var margin = Side / 8;
        var worst = expected;
        var at = (X: 0, Y: 0);

        for (var y = margin; y < image.Height - margin; y++) {
            for (var x = margin; x < image.Width - margin; x++) {
                var value = image.Pixels[image.Offset(x, y)] / 255f;

                if (MathF.Abs(value - expected) > MathF.Abs(worst - expected)) {
                    worst = value;
                    at = (x, y);
                }
            }
        }

        Assert.True(
            MathF.Abs(worst - expected) <= tolerance,
            $"{what} should read {expected:F2} everywhere and ({at.X}, {at.Y}) came back {worst:F3}."
        );
    }

    /// <summary>
    ///     The occlusion along one row, averaged across the middle columns to keep the jitter out.
    /// </summary>
    /// <remarks>
    ///     Across rather than down, because down is the axis the corner scene varies along: the floor
    ///     runs away from the camera as the row climbs, and one row is one distance from the wall.
    ///     The middle half only — a search that walks off the side of the viewport stops early.
    /// </remarks>
    static float Band(in Bitmap image, int y) {
        var total = 0f;
        var from = image.Width / 4;
        var to = image.Width * 3 / 4;

        for (var x = from; x < to; x++) {
            total += image.Pixels[image.Offset(x, y)] / 255f;
        }

        return total / (to - from);
    }
}
