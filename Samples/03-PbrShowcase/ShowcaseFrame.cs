// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Engine.Renderer;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Samples.PbrShowcase;

/// <summary>What this project contributes to two of the frame's sets: a baked sky, and stand-ins.</summary>
/// <remarks>
///     <para>
///         <b>Sample 13's <c>ArenaFrame</c>, carried over with its reasons</b> — this is the host
///         half the standard frame's remarks call the material's and the extraction's, in its
///         smallest complete form. It is what "the ambient split is the material's" costs in
///         practice: a project that turns the shading permutations on owes the set-0 bindings the
///         shader declares for them.
///     </para>
///     <para>
///         <b>The rule that makes this type necessary.</b> <c>ForwardPlus</c> declares its
///         per-frame bindings <i>whatever the permutations say</i> — a variant with image-based
///         lighting, reflection probes and clustered lights all switched off still has an
///         <c>environment</c>, a <c>probes</c> array and a <c>clusters</c> buffer in its plan.
///         <c>EffectSetWriter</c> writes every binding of a set or none, so one unfilled resource is
///         not one missing effect: it is a set that never binds and a driver that refuses every draw
///         in the pass. A black window, from permutations somebody turned off to simplify things.
///     </para>
///     <para>
///         <b>So each binding is filled by whoever owns it.</b> The block and the scene buffers are
///         the renderer's. The shadow atlas and its sampler come from the <c>!ShadowMap</c> node the
///         `shadows:` knob emits and the pass that reads it. Three come from <b>one baked sky</b> —
///         the cube, its sampler, and the probe array, whose empty slots fall back to that same cube
///         because a surface with no probe reflects the sky. (The irradiance volumes sample 13 also
///         fills are absent here: `gi: Ambient` emits no probe field, so the forward irradiance slot
///         composes the neutral filler and declares none.) That leaves one, and this project renders
///         it nowhere.
///     </para>
///     <para>
///         ⚠ <b>The one is the cluster list</b>, which needs a culling dispatch and a buffer for it
///         to write. What is bound instead is an empty froxel buffer, with the clustered permutation
///         off so nothing samples it — a valid descriptor rather than a convincing one, and the
///         distinction is the point.
///     </para>
///     <para>
///         <b>And the same rule again, one set along.</b> <c>ShadowCaster</c> declares an opacity map,
///         a sampler and a bone palette whatever its <c>AlphaTested</c> and <c>Skinned</c>
///         permutations say — and a material has no name for any of them, because they belong to a
///         pass no material has heard of. <see cref="ApplyCaster" /> fills them on the stage that
///         imposed the shader, which is the thing that owes them: see
///         <see cref="RenderStage.Parameters" />.
///     </para>
///     <para>
///         <b>A stand-in works for these and would not work generically.</b>
///         <c>EffectBinding</c> carries no texture <i>dimension</i>, so nothing can pick a neutral
///         resource by kind alone: <c>environment</c> and <c>probes</c> are cubes and the irradiance
///         volumes are 3D, and a 2D view bound to any of them is a different validation error rather
///         than a fallback. Knowing that <c>opacityMap</c> is 2D and that a froxel grid is
///         <see cref="ClusterGrid.BufferSize" /> bytes is knowing something about <i>these</i>
///         shaders, which is why this lives in the project and not in the engine.
///     </para>
/// </remarks>
public sealed class ShowcaseFrame : IDisposable {
    /// <summary>One side of the source cube, before prefiltering.</summary>
    /// <remarks>
    ///     Small deliberately. The convolution is on the CPU at sixty-four importance samples per
    ///     texel per face per level, so the source size is the load time — and a daylight sky has no
    ///     detail below the sun's aureole that a larger cube would preserve.
    /// </remarks>
    const int SourceSize = 48;

    /// <summary>How many roughness levels the chain holds.</summary>
    const int Levels = 5;

    readonly IGraphicsDevice device;

    BufferHandle clusterStandIn;
    TextureHandle opaque;
    TextureViewHandle opaqueView;
    SamplerHandle opaqueSampler;
    BufferHandle bindPose;
    bool disposed;

    ShowcaseFrame(IGraphicsDevice graphics, EnvironmentTexture texture, ShCoefficients irradiance, SkyParameters sky) {
        device = graphics;
        Sky = texture;
        Irradiance = irradiance;
        Atmosphere = sky;
    }

    /// <summary>The three numbers the whole frame's light comes out of.</summary>
    public SkyParameters Atmosphere { get; }

    /// <summary>How much light the sun delivers to a surface facing it, in lux.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off the atmosphere rather than typed into the scene, and that is the point of
    ///     the model being here.</b> A scene file naming both a sun direction and a sun brightness is
    ///     a scene that can be a sunset sky over a noon sun, and nothing reports it — so the level
    ///     says where the sun <em>is</em> and this says what that means. At eight degrees of
    ///     elevation it is about a tenth of what it is overhead.
    /// </remarks>
    public float SunIlluminance => PhysicalSky.SunIlluminance(Atmosphere);

    /// <summary>What colour the sun is at that elevation, as a tint of unit luminance.</summary>
    /// <remarks>
    ///     From Rayleigh and Mie transmittance along the same air mass the sky above was computed
    ///     for, so the disc and the horizon redden together. Golden hour is a position, not a palette.
    /// </remarks>
    public Color3 SunTint => PhysicalSky.SunTint(Atmosphere);

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

    /// <summary>Bakes the sky the sun's own direction implies, and the stand-ins beside it.</summary>
    /// <param name="graphics">The device the resources live on.</param>
    /// <param name="sunDirection">Which way the sun's light travels — the level's own sun.</param>
    /// <returns>The frame's contribution.</returns>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    /// <remarks>
    ///     <para>
    ///         Computed rather than a loaded HDR, for the reason every other asset in this project is
    ///         generated: the sample commits its content, and a committed sky is either a model or a
    ///         megabyte of binary nobody can review. Unlike the gradient this replaced, it is also
    ///         <em>right</em> — Preetham's daylight model in cd/m², so the horizon reddens because the
    ///         sun is low and not because somebody typed an orange.
    ///     </para>
    ///     <para>
    ///         A turbidity of 2.6 is a clear evening, and clear is what this level wants rather than
    ///         atmospheric: haze scatters the sun <em>into</em> the sky, so raising it brightens the
    ///         ambient and dims the key at the same time — which is a scene with no shadows in it. The
    ///         ground albedo is the dry concrete the floor is made of; without it the lower hemisphere
    ///         is black and every surface's ambient stops dead at its equator.
    ///     </para>
    /// </remarks>
    public static ShowcaseFrame Bake(IGraphicsDevice graphics, Vector3 sunDirection) {
        ArgumentNullException.ThrowIfNull(graphics);

        var sky = new SkyParameters(sunDirection, Turbidity: 2.6f, GroundAlbedo: 0.15f);
        var source = PhysicalSky.Bake(SourceSize, sky);

        return new(
            graphics,
            EnvironmentTexture.Bake(graphics, source, Levels),
            SphericalHarmonics.Project(source),
            sky
        );
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

        parameters.Set(ForwardPlusKeys.Clusters, clusterStandIn);
    }

    /// <summary>Fills what a caster stage owes the shader it imposes.</summary>
    /// <param name="stage">The stage whose <c>shader:</c> is <c>ShadowCaster</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stage" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         Three bindings, none of which any material in this project has an opinion about. The
    ///         texture is opaque white rather than undefined and the distinction matters here in a way
    ///         it did not for the old shadow stand-in: the alpha-tested variant would sample it, and
    ///         white is "this texel is solid" — the answer that makes a caster with no cut-out map cast
    ///         its whole silhouette. Undefined would be a level whose shadows have holes in them on one
    ///         driver.
    ///     </para>
    ///     <para>
    ///         The palette is one identity matrix. A skinned caster reading it would draw its bind
    ///         pose, which is exactly what a palette nobody animated means; this project has no
    ///         skinned meshes at all — its character is segmented, parts parented into a skeleton —
    ///         so nothing reads it and the variant folds the read away.
    ///     </para>
    /// </remarks>
    public void ApplyCaster(RenderStage stage) {
        ArgumentNullException.ThrowIfNull(stage);
        ObjectDisposedException.ThrowIf(disposed, this);

        CreateStandIns();

        // Named rather than taken from a generated `ShadowCasterKeys`, because there is no such
        // class: the key generator reads a shader's committed `.reflect.json` and `ShadowCaster` has
        // none. `ParameterKeys.New` interns exactly the string `EffectSetWriter` looks the binding
        // up under, which is the shader's name and the binding's.
        stage.Parameters.Set(ParameterKeys.New<TextureViewHandle>("ShadowCaster.opacityMap"), opaqueView);
        stage.Parameters.Set(ParameterKeys.New<SamplerHandle>("ShadowCaster.opacitySampler"), opaqueSampler);
        stage.Parameters.Set(ParameterKeys.New<BufferHandle>("ShadowCaster.bones"), bindPose);
    }

    /// <summary>Hands the frame's sky node the cube a document cannot name.</summary>
    /// <param name="host">The renderer's host, whose builder holds the nodes the document made.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>After every rebuild, not once.</b> A document's nodes are made by
    ///         <c>CompositorBuilder</c>, so the object this reaches is a different one each time the
    ///         frame is loaded — and a sky node that never got its cube draws black, which is exactly
    ///         what a missing background looks like.
    ///     </para>
    ///     <para>
    ///         The <em>upload</em> is the renderer's, once per frame before the first pass, and it is
    ///         also what leaves the texture in <c>ShaderRead</c>. This node declares no read on it, so
    ///         nothing in the frame graph would transition it.
    ///     </para>
    /// </remarks>
    public void ApplySky(SceneRenderHost host) {
        ArgumentNullException.ThrowIfNull(host);
        ObjectDisposedException.ThrowIf(disposed, this);

        foreach (var node in host.Builder.Nodes.Values) {
            if (node is SkyRenderer sky) {
                sky.Environment = Sky.View;
                sky.EnvironmentSampler = Sky.Sampler;
                sky.MipCount = Sky.MipCount;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Sky.Dispose();

        if (clusterStandIn.IsValid) {
            device.Destroy(clusterStandIn);
        }

        if (opaqueView.IsValid) {
            device.Destroy(opaqueView);
        }

        if (opaque.IsValid) {
            device.Destroy(opaque);
        }

        if (opaqueSampler.IsValid) {
            device.Destroy(opaqueSampler);
        }

        if (bindPose.IsValid) {
            device.Destroy(bindPose);
        }
    }

    /// <summary>The one resource nothing in this frame produces, made once.</summary>
    /// <remarks>
    ///     <para>
    ///         It is zeroed, and that is the meaningful value rather than a tidy one: a grid in which
    ///         every froxel reaches no lights is exactly true of a frame with no culling dispatch, so
    ///         a reader that did sample it gets the right answer instead of whatever the allocator
    ///         last held. The shader is compiled with the clustered permutation off, so none does.
    ///     </para>
    ///     <para>
    ///         There used to be a shadow atlas and a sampler here too, and there is a reason they are
    ///         gone rather than unused: the frame renders both now. A stand-in for a resource a pass
    ///         also publishes is a value that is right until the ordering changes.
    ///     </para>
    /// </remarks>
    void CreateStandIns() {
        if (clusterStandIn.IsValid) {
            return;
        }

        clusterStandIn = device.CreateBuffer(
            new BufferDescription(
                ClusterGrid.BufferSize,
                BufferUsage.Storage,
                MemoryAccess.HostUpload,
                "ClusterLights.StandIn"
            )
        );

        device.Write(clusterStandIn, 0, new byte[ClusterGrid.BufferSize]);

        opaque = device.CreateTexture(
            new TextureDescription(
                PixelFormat.Rgba8UNorm,
                1,
                1,
                TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name: "Opacity.Opaque"
            )
        );

        opaqueView = device.CreateTextureView(opaque);
        opaqueSampler = device.CreateSampler(SamplerDescription.LinearClamp with { Name = "Opacity.Opaque" });

        bindPose = device.CreateBuffer(
            new BufferDescription(64, BufferUsage.Storage, MemoryAccess.HostUpload, "Bones.BindPose")
        );

        var identity = Matrix4x4.Identity;

        device.Write(bindPose, 0, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref identity, 1)));

        // The four opaque bytes, staged, because a texture is device memory and only a copy reaches it.
        var staging = device.CreateBuffer(
            new BufferDescription(4, BufferUsage.CopySource, MemoryAccess.HostUpload, "Opacity.Staging")
        );

        device.Write(staging, 0, [byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue]);

        // ⚠ The texture's *content* is written, not just its layout, and both are needed. A
        // descriptor written against a sampled image promises the image is in ShaderRead when the
        // draw executes and the validation layers check that promise whether or not any instruction
        // reads it — a texture created and never transitioned is UNDEFINED, which was an error every
        // frame about a resource the shader ignores. These are not graph resources, so no pass will
        // transition them: one list, submitted at load, is the whole of it.
        using var commands = device.BeginCommandList(name: "StandIns");

        commands.Barrier(
            new([], [new TextureBarrier(opaque, ResourceState.Undefined, ResourceState.CopyDestination)])
        );

        commands.CopyBufferToTexture(staging, 0, new TextureRegion(opaque), new Int3(1, 1, 1));

        commands.Barrier(
            new([], [new TextureBarrier(opaque, ResourceState.CopyDestination, ResourceState.ShaderRead)])
        );

        commands.Finish();
        device.GraphicsQueue.Submit([commands]);

        // At load time and once, so waiting here costs a few microseconds and removes any question
        // of whether the upload has run by the first frame — and lets the staging buffer go now
        // rather than being tracked for a frame.
        device.GraphicsQueue.WaitIdle();
        device.Destroy(staging);
    }
}
