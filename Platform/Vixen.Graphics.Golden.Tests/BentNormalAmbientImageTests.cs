// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The ambient term takes its direction from the bent normal, and the fixtures say so without
///     restating the spherical-harmonic constants.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>!Ssao</c>'s bent normal was produced and discarded.</b> The permutation existed, the
///         renderer exposed it, a document could turn it on — and <c>AmbientCombine.rvn</c> sampled
///         that plane's <c>r</c> and read no other channel, so the ambient term integrated around the
///         <em>geometric</em> normal whatever the pass had computed. Occlusion says how much light
///         arrives and a bent normal says where it arrives from; only the second changes the shading
///         direction, which is most of what makes the horizon integral worth its arithmetic on a
///         curved surface.
///     </para>
///     <para>
///         ⚠ <b>An output nothing reads is indistinguishable, to every gate in this repository, from
///         an output nothing produces — both are green.</b> <see cref="GtaoImageTests" /> holds the
///         occlusion term against its closed form and says nothing about the direction, because
///         nothing downstream would have noticed if the direction were garbage. That is the shape
///         these fixtures exist to close.
///     </para>
///     <para>
///         <b>Why the environment here is two coefficients and not nine.</b> With only <c>L00</c> and
///         <c>L11</c> set, <c>Ibl.IrradianceSh9</c> collapses to <c>a + b·n.x</c> — an affine function
///         of one component of the direction it is handed. That is what lets every assertion below be
///         a relation between renders rather than a number copied out of the shader: an affine
///         function is pinned by <em>symmetry</em> and <em>linearity</em>, and a pass that ignored the
///         direction would return the same constant three times.
///     </para>
///     <para>
///         ⚠ <b>The flat unoccluded case is exactly where the two normals agree, so it cannot be the
///         oracle — but it is the guarantee.</b> An unoccluded surface's bent normal is its geometric
///         normal, so <see cref="AnUnbentPlaneRendersExactlyAsItDidBefore" /> is what says turning the
///         permutation on cannot move an open surface. ⚠ That identity had to be <em>made</em> true:
///         the producer's raw slice sum leans toward the camera on a slanted surface, which
///         <c>GtaoImageTests.TheBentNormalLeansAwayFromTheWallAndOnlyBesideIt</c> caught the first
///         time it looked and <c>Ssao.rvn</c> now corrects. This fixture stages the direction by hand
///         and so says nothing about it; the two halves are deliberately in different files.
///     </para>
///     <para>
///         ⚠ <b>The plane's layout changes with the permutation, which is a second defect inside the
///         first.</b> With the bent normal on, the occlusion moves from <c>r</c> to <c>a</c> and
///         <c>rgb</c> becomes the direction — so the old consumer, reading <c>r</c>, would have
///         multiplied its whole ambient term by the <em>x of a direction</em>: a number in [0, 1] that
///         looks exactly like an occlusion value. <see cref="TheOcclusionIsReadFromAlphaWhenTheDirectionOccupiesRgb" />
///         is that half.
///     </para>
///     <para>
///         <b>Both sabotages, run on device at the commit that added these.</b> Aiming the harmonics
///         at the geometric normal again — <c>Ibl.IrradianceSh9(n, environmentSh)</c> — turns
///         <see cref="LeaningTheDirectionMovesTheAmbientTermSymmetrically" /> and
///         <see cref="TheDirectionIsDecodedIntoWorldSpaceAndNotLeftInViewSpace" /> red, both of them
///         reporting <c>127</c> for a lean in either direction, and leaves the other two green, which
///         is the division of labour they were written for. Reading the contact term from <c>r</c>
///         again turns three red: the quarter-open surface comes back at <c>109</c> instead of
///         <c>51</c>, the unoccluded plane at <c>64</c> instead of <c>127</c>, and the symmetry
///         breaks to <c>+110 / −57</c> because the ambient is being multiplied by the direction's own
///         x on one side and by its negation on the other.
///     </para>
///     <para>
///         ⚠ <b>The first sabotage originally left
///         <see cref="TheDirectionIsDecodedIntoWorldSpaceAndNotLeftInViewSpace" /> green, and that was
///         the test's defect and not the sabotage's.</b> It asserted only that two cameras agree, and
///         a pass that reads no direction at all agrees with itself perfectly. The gap assertion in
///         front of the equality is what makes it a claim about a rotation.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class BentNormalAmbientImageTests {
    const int Side = Fixture.Side;
    const float Near = 0.1f;
    const float Far = 100f;
    const float FieldOfView = MathF.PI / 3f;

    /// <summary>The constant term, chosen so a direction square to x renders at about half scale.</summary>
    /// <remarks>
    ///     ⚠ Chosen rather than derived, and it does not have to be exact: nothing below asserts an
    ///     absolute level. What it buys is headroom — the leaning renders have to stay inside an
    ///     eight-bit target at both ends, and a constant term too near either rail would clip one of
    ///     them and turn a linearity assertion into an assertion about <c>saturate</c>.
    /// </remarks>
    static readonly Vector3 Constant = new(1.7724f);

    /// <summary>The linear-in-x term, which is the whole of what the direction can move.</summary>
    static readonly Vector3 AlongX = new(1.3025f);

    /// <summary>How far a lean reaches along x. Forty-five degrees, so both signs stay in range.</summary>
    static readonly Vector3 Leaning = Vector3.Normalize(new(1f, 0f, 1f));

    /// <summary>The surface every fixture here shades: facing the camera, so its own x is nought.</summary>
    static readonly Vector3 Facing = new(0f, 0f, 1f);

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
    ///     A bent normal that is the geometric normal renders what the pass rendered before there was
    ///     a bent normal at all.
    /// </summary>
    /// <remarks>
    ///     The guarantee rather than the oracle, and the reason the feature can be turned on without
    ///     re-recording a golden: an open surface's bent normal is its geometric normal, so the two
    ///     paths meet there exactly. ⚠ The comparison render binds <em>no</em> contact plane, because
    ///     the plane's <c>r</c> is a direction component under the permutation — comparing against a
    ///     run that read it as occlusion would be comparing against the defect.
    /// </remarks>
    [Fact]
    public void AnUnbentPlaneRendersExactlyAsItDidBefore() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var geometric = Middle(Render(owned, bent: null));
        var unbent = Middle(Render(owned, Facing));

        Assert.True(
            Math.Abs(geometric - unbent) <= 1,
            $"an unoccluded surface read {unbent} through the bent normal and {geometric} without it — "
            + "the two normals are the same vector there, so turning the permutation on has moved a "
            + "surface it cannot have anything to say about."
        );
    }

    /// <summary>
    ///     Leaning the bent normal toward the environment's bright side brightens the surface, and
    ///     leaning it away darkens it by the same amount.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The oracle, and it is a symmetry rather than a level.</b> The environment here is
    ///         affine in <c>n.x</c>, so a direction leaning +45° and one leaning −45° are equally far
    ///         from the unbent answer in opposite directions — whatever the constants are. A pass that
    ///         read the geometric normal returns the same number for all three, which fails the gap
    ///         assertion; a pass that read the direction through a wrong basis fails the symmetry.
    ///     </para>
    ///     <para>
    ///         ⚠ The gap is asserted before the symmetry deliberately. Two equal numbers are perfectly
    ///         symmetric, so a symmetry test alone is satisfied by exactly the defect.
    ///     </para>
    /// </remarks>
    [Fact]
    public void LeaningTheDirectionMovesTheAmbientTermSymmetrically() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var unbent = Middle(Render(owned, Facing));
        var toward = Middle(Render(owned, Leaning));
        var away = Middle(Render(owned, new(-Leaning.X, Leaning.Y, Leaning.Z)));

        Assert.True(
            toward - away > 100,
            $"leaning the bent normal from −45° ({away}) to +45° ({toward}) moved the ambient term by "
            + $"{toward - away} levels of 255, which is not a shading direction being read at all."
        );

        var up = toward - unbent;
        var down = unbent - away;

        Assert.True(
            Math.Abs(up - down) <= 2,
            $"the environment is affine in the direction's x, so leaning toward it (+{up}) and away "
            + $"from it (−{down}) have to move the same distance; they did not, so the direction is "
            + "reaching the harmonics through something that is not a rotation."
        );
    }

    /// <summary>The occlusion comes out of alpha, because the direction is occupying rgb.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half of this that has nothing to do with the direction.</b> Turning the
    ///         permutation on moves the occlusion channel, so a consumer that kept reading <c>r</c>
    ///         read the direction's x — unsigned-encoded, so always in [0, 1], so always a plausible
    ///         occlusion value. This fixture holds a quarter-open surface against a fully open one at
    ///         the same direction: the term has to quarter.
    ///     </para>
    ///     <para>
    ///         Against the old consumer the same pair reads <c>0.854 ×</c> the geometric answer twice
    ///         — identical, because the alpha it ignores is the only thing that differs — so this goes
    ///         red on the ratio rather than on a level.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheOcclusionIsReadFromAlphaWhenTheDirectionOccupiesRgb() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var open = Middle(Render(owned, Leaning));
        var quarter = Middle(Render(owned, Leaning, occlusion: 0.25f));

        // Eight-bit rounding at both ends of the quarter, and nothing else: the term is a product.
        Assert.InRange(quarter, (open / 4) - 2, (open / 4) + 2);
    }

    /// <summary>
    ///     And through a camera whose view matrix is a real rotation, which is what pins the space the
    ///     direction is decoded into.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every other fixture here looks down −Z from the origin, so its view matrix is the
    ///         identity and a missing rotation is invisible.</b> That is the same arrangement
    ///         <see cref="GtaoImageTests" /> uses and for the same reason — through a non-trivial view
    ///         matrix, a mistake in the rotation and a mistake in the estimator are one picture. So one
    ///         fixture pays for the other's blind spot: the camera looks down +X, the direction is
    ///         staged in <em>view</em> space, and the answer has to match the identity-camera run that
    ///         staged the same <em>world</em> direction.
    ///     </para>
    ///     <para>
    ///         A shader that skipped <c>inverseView</c> would read the view-space vector as a world one
    ///         and land on the unbent value instead, which the gap below is sized against.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheDirectionIsDecodedIntoWorldSpaceAndNotLeftInViewSpace() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var yawed = Camera(new(1f, 0f, 0f));

        // The same world direction, expressed in that camera's view space — which is what the AO pass
        // would have written, because its horizons are found there. ⚠ Row-vector on this side and
        // column-vector in the shader, which is the same rotation and the opposite spelling: the
        // fragment writes `view * float4(v, 0)` and this writes `float4(v, 0) * view`.
        var direction = new Vector4(Leaning, 0f) * yawed.Camera!.Value.View;
        var inView = new Vector3(direction.X, direction.Y, direction.Z);

        var unbent = Middle(Render(owned, Facing));
        var straight = Middle(Render(owned, Leaning));
        var rotated = Middle(Render(owned, inView, view: yawed));

        // ⚠ First, that the lean moved anything at all. Without this the equality below is two
        // constants agreeing: a pass that read the geometric normal returns the same number through
        // both cameras and satisfies a rotation assertion by ignoring rotation and direction alike.
        // Run as a sabotage and it did exactly that.
        Assert.True(
            straight - unbent > 50,
            $"leaning the direction moved the term from {unbent} to {straight}, which is not enough "
            + "for the equality below to be about a rotation."
        );

        Assert.True(
            Math.Abs(straight - rotated) <= 2,
            $"the same world direction read {straight} through an identity view and {rotated} through "
            + "a yawed one, so the decode is not rotating the direction out of view space."
        );
    }

    // ── The fixture ─────────────────────────────────────────────────────────────────────────

    /// <summary>A camera at the origin looking the given way, at this fixture's field of view.</summary>
    static RenderView Camera(Vector3 forward) =>
        new("camera") { Camera = new(Vector3.Zero, forward, new(0f, 1f, 0f), FieldOfView, 1f, Near, Far) };

    /// <summary>
    ///     One flat white surface under a two-coefficient sky, combined with or without a bent-normal
    ///     contact plane.
    /// </summary>
    /// <param name="fixture">The device.</param>
    /// <param name="bent">
    ///     The direction to stage in the contact plane, or null to bind no contact plane at all — which
    ///     is the pass as it was before there was a direction to read.
    /// </param>
    /// <param name="occlusion">What to put in that plane's alpha.</param>
    /// <param name="view">The camera, whose view matrix is the space <paramref name="bent" /> is in.</param>
    /// <remarks>
    ///     <para>
    ///         Every plane is staged as <c>Rgba32Float</c> rather than as eight-bit texels: the
    ///         direction is unsigned-encoded, and quantising it to 256 levels before the pass reads it
    ///         would fold the fixture's own rounding into an assertion about the shader's.
    ///     </para>
    ///     <para>
    ///         The direct plane is black and the albedo white, so the frame that comes back <em>is</em>
    ///         the ambient term rather than a term inside a lit pixel. Every other switch is off:
    ///         no irradiance plane (so the sky answers), no field occlusion, no reflections, and no
    ///         depth (so the read is linear rather than bilateral — a full-resolution plane's
    ///         bilateral upsample degenerates to its own texel anyway, and the point here is the
    ///         channel and not the filter).
    ///     </para>
    /// </remarks>
    static Bitmap Render(
        Fixture fixture,
        Vector3? bent,
        float occlusion = 1f,
        RenderView? view = null
    ) {
        var device = fixture.Device;

        // ⚠ Every fixture here renders the same scene twice and compares, so the graph is reused —
        // and a compiled graph refuses the next frame's imports rather than quietly reusing the last
        // one's. One reset per render is what makes a differential fixture possible at all.
        fixture.Graph.Reset();

        var direct = new Vector4[Side * Side];
        var albedo = new Vector4[Side * Side];
        var normals = new Vector4[Side * Side];
        var contact = new Vector4[Side * Side];

        var encoded = bent is { } direction ? (Vector3.Normalize(direction) * 0.5f) + new Vector3(0.5f) : Vector3.Zero;

        for (var i = 0; i < direct.Length; i++) {
            direct[i] = new(0f, 0f, 0f, 1f);
            albedo[i] = new(1f, 1f, 1f, 1f);

            // World normal in xyz, perceptual roughness in w — the split pass's third target.
            normals[i] = new(Facing, 1f);
            contact[i] = new(encoded, occlusion);
        }

        var directPlane = fixture.Sampled(
            "direct",
            Side,
            MemoryMarshal.AsBytes<Vector4>(direct),
            PixelFormat.Rgba32Float
        );

        var albedoPlane = fixture.Sampled(
            "albedo",
            Side,
            MemoryMarshal.AsBytes<Vector4>(albedo),
            PixelFormat.Rgba32Float
        );

        var normalPlane = fixture.Sampled(
            "normals",
            Side,
            MemoryMarshal.AsBytes<Vector4>(normals),
            PixelFormat.Rgba32Float
        );

        var contactPlane = fixture.Sampled(
            "contact",
            Side,
            MemoryMarshal.AsBytes<Vector4>(contact),
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
                    ["Core", "Geometry", "Shading"],
                    Path.Combine("PostFx", "Fullscreen.rvn"),
                    Path.Combine("PostFx", "AmbientCombine.rvn")
                )
            )
        );

        // Two coefficients, so the irradiance is affine in the direction's x — see the class remarks.
        var lighting = new SceneLighting {
            Environment = new() { Irradiance = new() { L00 = Constant, L11 = AlongX }, Intensity = 1f }
        };

        using var combine = new AmbientCombineRenderer {
            Name = "Combine",
            Direct = "Direct",
            Albedo = "Albedo",
            Normals = "Normals",
            ContactOcclusion = bent is null ? null : "Contact",
            ContactBentNormal = bent is not null,
            Output = "Display",
            View = view ?? Camera(new(0f, 0f, -1f)),
            Lighting = lighting,
            Modules = describer,
            Device = device,
            Samplers = samplers,
            Allocator = allocator
        };

        var compositor = new GraphicsCompositor(system) { FrameSize = new(Side, Side), Game = combine };

        compositor.Imports["Direct"] = Import(directPlane, "direct");
        compositor.Imports["Albedo"] = Import(albedoPlane, "albedo");
        compositor.Imports["Normals"] = Import(normalPlane, "normals");
        compositor.Imports["Contact"] = Import(contactPlane, "contact");

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
        Assert.True(combine.Pass.PipelineCount > 0, "the pass compiled no pipeline, so it drew nothing");

        // ⚠ And that the node did not quietly fall back. `Degraded` is where this pass says it is
        // about to combine ambient light the wrong way, and a fixture that ignored it would be
        // asserting about whichever answer the fallback gave.
        Assert.Null(combine.Degraded);

        return fixture.Render(
            frame.Texture("harness", "Display"),
            commands => {
                Upload(commands, directPlane);
                Upload(commands, albedoPlane);
                Upload(commands, normalPlane);
                Upload(commands, contactPlane);
            }
        );
    }

    static ImportedTexture Import(
        (TextureHandle Texture, TextureViewHandle View, BufferHandle Staging) plane,
        string name
    ) =>
        new(
            plane.Texture,
            plane.View,
            new(
                PixelFormat.Rgba32Float,
                Side,
                Side,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: name
            ),
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

    /// <summary>The red channel at the middle of the frame — every pixel here is the same pixel.</summary>
    /// <remarks>
    ///     ⚠ One tap and not a mean, because a mean over a uniform frame hides a frame that is not
    ///     uniform. What keeps this honest is the staging: every plane is constant, so any variation
    ///     across the picture would be the pass inventing one, and the ratio assertions would show it.
    /// </remarks>
    static int Middle(in Bitmap image) => image.Pixels[image.Offset(image.Width / 2, image.Height / 2)];
}
