// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Lighting;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>What this project contributes to the frame's set 0: a baked sky, and two stand-ins.</summary>
/// <remarks>
///     <para>
///         <b>The rule that makes this type necessary.</b> <c>ForwardPlus</c> declares thirteen
///         bindings in its per-frame set and declares all of them <i>whatever the permutations
///         say</i> — a variant with shadows, image-based lighting, reflection probes, the probe field
///         and clustered lights all switched off still has a <c>shadowMap</c>, an <c>environment</c>,
///         a <c>probes</c> array, a <c>clusters</c> buffer and five irradiance volumes in its plan.
///         <c>EffectSetWriter</c> writes every binding of a set or none, so one unfilled resource is
///         not one missing effect: it is a set that never binds and a driver that refuses every draw
///         in the pass. A black window, from four permutations somebody turned off to simplify things.
///     </para>
///     <para>
///         <b>So the thirteen are filled by whoever owns them.</b> The block and the two scene buffers
///         are the renderer's. Five volumes and a sampler come from the document's
///         <c>!IrradianceField</c> node, which names <c>ForwardPlus</c> among its <c>passes</c>. Four
///         come from <b>one baked sky</b> — the cube, its sampler, and the probe array, whose empty
///         slots fall back to that same cube because a surface with no probe reflects the sky. That
///         leaves two, and this project renders neither of them.
///     </para>
///     <para>
///         ⚠ <b>The two are the shadow atlas and the cluster list.</b> A cascaded shadow map means a
///         caster stage, a depth-only variant per material and a second extraction mask; a cluster
///         list means a culling dispatch and a buffer for it to write. Both are real features with
///         real costs and neither is what this sample is about. What it binds instead is a one-texel
///         texture, a sampler and an empty froxel buffer, with <c>UseShadows</c> and the clustered
///         permutation off so that nothing samples any of them. Those are valid descriptors rather
///         than convincing ones, and the distinction is the point: the frame draws because the set is
///         complete, and the shadows are absent because the project renders none.
///     </para>
///     <para>
///         <b>A stand-in works for these two and would not work generically.</b>
///         <c>EffectBinding</c> carries no texture <i>dimension</i>, so nothing can pick a neutral
///         resource by kind alone: <c>environment</c> and <c>probes</c> are cubes and the irradiance
///         volumes are 3D, and a 2D view bound to any of them is a different validation error rather
///         than a fallback. Knowing that <c>shadowMap</c> is 2D and that a froxel grid is
///         <see cref="ClusterGrid.BufferSize" /> bytes is knowing something about <i>this</i> shader,
///         which is why this lives in the project and not in the engine.
///     </para>
/// </remarks>
public sealed class ArenaFrame : IDisposable {
    /// <summary>One side of the source cube, before prefiltering.</summary>
    /// <remarks>
    ///     Small deliberately. The convolution is on the CPU at sixty-four importance samples per
    ///     texel per face per level, so the source size is the load time — and a gradient has no
    ///     detail that a larger cube would preserve.
    /// </remarks>
    const int SourceSize = 32;

    /// <summary>How many roughness levels the chain holds.</summary>
    const int Levels = 5;

    readonly IGraphicsDevice device;

    TextureHandle shadowStandIn;
    TextureViewHandle shadowStandInView;
    SamplerHandle shadowSampler;
    BufferHandle clusterStandIn;
    bool disposed;

    ArenaFrame(IGraphicsDevice graphics, EnvironmentTexture texture, ShCoefficients irradiance) {
        device = graphics;
        Sky = texture;
        Irradiance = irradiance;
    }

    /// <summary>The prefiltered cube, uploaded by <c>WorldRenderer.Draw</c> before the first pass.</summary>
    public EnvironmentTexture Sky { get; }

    /// <summary>The diffuse half, projected from the same source the chain was filtered from.</summary>
    /// <remarks>
    ///     From the <em>source</em>, not from level zero of the chain. Level zero is already convolved
    ///     with the narrowest lobe, so projecting it would give a surface whose ambient and whose
    ///     reflection disagree — which reads as the wrong roughness rather than as two bakes that do
    ///     not match. <c>EnvironmentLight</c> says the same thing in its own remarks.
    /// </remarks>
    public ShCoefficients Irradiance { get; }

    /// <summary>Bakes an overcast-sky gradient and allocates the stand-ins beside it.</summary>
    /// <param name="graphics">The device the resources live on.</param>
    /// <returns>The frame's contribution.</returns>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    /// <remarks>
    ///     A gradient rather than a loaded HDR, for the reason every other asset in this project is
    ///     generated: the sample commits its content, and a committed sky is either a few lines of
    ///     arithmetic or a megabyte of binary nobody can review. Swapping in a real capture changes
    ///     this method and nothing else.
    /// </remarks>
    public static ArenaFrame Bake(IGraphicsDevice graphics) {
        ArgumentNullException.ThrowIfNull(graphics);

        var source = Gradient(SourceSize);

        return new(graphics, EnvironmentTexture.Bake(graphics, source, Levels), SphericalHarmonics.Project(source));
    }

    /// <summary>Points the frame's lighting at the sky, and fills what the frame does not produce.</summary>
    /// <param name="lighting">The scene's lighting — <c>WorldRenderer.SceneEnvironment</c>.</param>
    /// <param name="parameters">The frame's set — <c>WorldRenderer.SceneBlock.Parameters</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Apply(SceneLighting lighting, ParameterCollection parameters) {
        ArgumentNullException.ThrowIfNull(lighting);
        ArgumentNullException.ThrowIfNull(parameters);
        ObjectDisposedException.ThrowIf(disposed, this);

        var light = lighting.Environment ?? new EnvironmentLight();

        Sky.Apply(light);
        light.Irradiance = Irradiance;
        lighting.Environment = light;

        CreateStandIns();

        parameters.Set(ForwardPlusKeys.ShadowMap, shadowStandInView);
        parameters.Set(ForwardPlusKeys.ShadowSampler, shadowSampler);
        parameters.Set(ForwardPlusKeys.Clusters, clusterStandIn);
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Sky.Dispose();

        if (shadowStandInView.IsValid) {
            device.Destroy(shadowStandInView);
        }

        if (shadowStandIn.IsValid) {
            device.Destroy(shadowStandIn);
        }

        if (shadowSampler.IsValid) {
            device.Destroy(shadowSampler);
        }

        if (clusterStandIn.IsValid) {
            device.Destroy(clusterStandIn);
        }
    }

    /// <summary>The two resources nothing in this frame produces, made once.</summary>
    /// <remarks>
    ///     <para>
    ///         The texture's content is left undefined and that is not laziness: the shader is
    ///         compiled with <c>UseShadows</c> off, so no instruction reads it, and writing a white
    ///         texel would be describing a shadow term that is never computed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its <i>layout</i> is not optional, though, and that distinction cost a run.</b> A
    ///         descriptor written against a sampled image promises the image is in
    ///         <see cref="ResourceState.ShaderRead" /> when the draw executes, and the validation
    ///         layers check that promise whether or not any instruction reads the image. A texture
    ///         created and never transitioned is in <c>UNDEFINED</c>, so every frame was an error
    ///         about a resource the shader ignores. One barrier, submitted at load, on a list made for
    ///         it — these are not graph resources, so no pass will transition them.
    ///     </para>
    ///     <para>
    ///         The froxel buffer <em>is</em> zeroed, and the asymmetry is deliberate. Zero is the
    ///         meaningful value there — a grid in which every froxel reaches no lights, which is
    ///         exactly true of a frame with no culling dispatch — so a reader that did sample it would
    ///         get the right answer rather than whatever the allocator last held.
    ///     </para>
    /// </remarks>
    void CreateStandIns() {
        if (shadowStandIn.IsValid) {
            return;
        }

        shadowStandIn = device.CreateTexture(
            new TextureDescription(PixelFormat.R8UNorm, 1, 1, TextureUsage.Sampled, Name: "ShadowMap.StandIn")
        );

        shadowStandInView = device.CreateTextureView(shadowStandIn);
        shadowSampler = device.CreateSampler(SamplerDescription.LinearClamp with { Name = "ShadowMap.StandIn" });

        clusterStandIn = device.CreateBuffer(
            new BufferDescription(
                ClusterGrid.BufferSize,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "ClusterLights.StandIn"
            )
        );

        device.Write(clusterStandIn, 0, new byte[ClusterGrid.BufferSize]);

        using var commands = device.BeginCommandList(name: "StandIns");

        commands.Barrier(
            new([], [new TextureBarrier(shadowStandIn, ResourceState.Undefined, ResourceState.ShaderRead)])
        );

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        // At load time and once, so waiting here costs a few microseconds and removes any question
        // of whether the barrier has run by the first frame.
        device.GraphicsQueue.WaitIdle();
    }

    /// <summary>An overcast sky: bright above, a dim ground bounce below, warmer toward the horizon.</summary>
    /// <remarks>
    ///     Radiance rather than colour — the values run past one, which is what the tonemap in this
    ///     project's frame is there to bring back — because everything downstream of a cube map
    ///     integrates it. A sky authored in display values gives a room lit as though the sun were a
    ///     lamp.
    /// </remarks>
    static CubeImage Gradient(int size) {
        var image = new CubeImage(size);

        var zenith = new Vector3(0.42f, 0.55f, 0.82f) * 2.4f;
        var horizon = new Vector3(0.72f, 0.70f, 0.66f) * 1.6f;
        var ground = new Vector3(0.20f, 0.18f, 0.16f) * 0.9f;

        for (var face = 0; face < 6; face++) {
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var up = Vector3.Normalize(image.DirectionOf((CubeFace)face, x, y)).Y;

                    image.At((CubeFace)face, x, y) = up >= 0f
                        ? Vector3.Lerp(horizon, zenith, MathF.Sqrt(up))
                        : Vector3.Lerp(horizon, ground, MathF.Sqrt(-up));
                }
            }
        }

        return image;
    }
}
