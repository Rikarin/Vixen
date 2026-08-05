// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.PostFx;

/// <summary>
///     Froxel volumetric fog: three compute dispatches that fill a volume, which <c>!Fog</c> reads.
/// </summary>
/// <remarks>
///     <para>
///         <b>Fog that a shadow can fall through.</b> The analytic falloff <see cref="FogRenderer" />
///         applies is a function of distance and altitude and nothing else, so it cannot know that a
///         wall is between this pixel and the sun — which is why a valley lit by an analytic fog has
///         no beams in it. Marching a volume is what buys that, and the volume is what the marching
///         needs somewhere to live in.
///     </para>
///     <para>
///         <b>Where it runs, and why there.</b> The three dispatches go between the shadow passes and
///         the main pass: they need shadows and lights, not scene colour, and declaring that is what
///         puts the barriers in. The <i>composite</i> is a permutation on <c>!Fog</c> in the
///         <c>"Air"</c> seat, which is after the temporal accumulation — the documented
///         TAA-before-fog invariant, and it is safe here for a reason that is not obvious: the volume
///         does its own temporal work in its own space, so it must not be inside a screen-space
///         accumulator that would reproject it as though it were a surface.
///     </para>
///     <para>
///         ⚠ <b>The volumes are declared only if the document did not.</b> A document that names
///         <c>FogMedia</c> and <c>FogScattered</c> re-points them — at a different resolution, or at
///         an import a host owns — and this stands aside, which is
///         <see cref="GraphicsCompositor" />'s own rule about a declaration an import already covers.
///         The extent then comes back out of the frame rather than from this node's own copy of the
///         numbers, so a dispatch cannot cover part of a volume the document sized differently.
///     </para>
///     <para>
///         <b>Its far plane is its own and not the camera's</b>, which is the number to reach for
///         first when tuning. Sixty-four metres of grid at sixty-four slices puts the nearest slice
///         about a centimetre deep and the furthest about four metres — spending the resolution
///         where a beam is actually visible. A grid stretched over a kilometre spends almost all of
///         it on distance the analytic fallback describes perfectly well.
///     </para>
/// </remarks>
public sealed class VolumetricFogRenderer : SceneRenderer, IDisposable, IPostProcessTarget {
    /// <summary>How big the stand-in buffer is, in bytes.</summary>
    /// <remarks>
    ///     Past one element of either structure it stands in for — a <c>PunctualLight</c> is 80 bytes
    ///     and a <c>FrameClusterLights</c> is 132 — so that <c>lightBuffer.Length</c> and
    ///     <c>clusters.Length</c> are both at least one in a variant that never asks.
    /// </remarks>
    const int StandInSize = 256;

    readonly List<ComputeRenderer> steps = [];

    PostProcessOverlay applied;

    BufferHandle lightStandIn;
    TextureHandle standIn;
    TextureViewHandle standInView;
    IGraphicsDevice? owner;

    /// <summary>What the shadow stand-in is, for the import and for the frame's record of it.</summary>
    TextureDescription StandInDescription =>
        new(PixelFormat.R32Float, 1, 1, TextureUsage.Sampled, Name: ShadowStandIn);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Recorded rather than applied, so the authored properties stay what the document set and
    ///     the overlay is laid over them each frame. See <c>IPostProcessTarget</c>.
    /// </remarks>
    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    /// <summary>The name the medium volume is published under.</summary>
    public string Media { get; init; } = "FogMedia";

    /// <summary>The name the lit volume is published under.</summary>
    public string Scattered { get; init; } = "FogScattered";

    /// <summary>The name the marched volume is published under. <c>!Fog</c> reads this one.</summary>
    public string Volume { get; init; } = "FogVolume";

    /// <summary>The view whose camera the froxels are laid out in front of.</summary>
    /// <remarks>
    ///     ⚠ <b>Not optional.</b> Without it the grid stands in front of a camera at the origin
    ///     looking down −Z with a 90° field of view, which is fog for a view nobody is looking
    ///     through — <see cref="FogRenderer.View" />'s failure, in three dimensions.
    /// </remarks>
    public RenderView? View { get; set; }

    /// <summary>How many froxels across, down and deep.</summary>
    /// <remarks>
    ///     The two screen axes are a resolution and the third is a decision about how finely to cut
    ///     the frustum — which is why the depth takes no scale from the frame. Doubling the slices
    ///     doubles the cost of the march and is what removes the banding a shallow grid shows where a
    ///     shadow edge crosses it.
    /// </remarks>
    public Int3 Resolution { get; set; } = new(160, 90, 64);

    /// <summary>Where the grid's first slice begins, in metres of view depth.</summary>
    /// <remarks>
    ///     ⚠ Not zero, and not the camera's near plane by accident. The slice boundaries are a ratio
    ///     raised to a power, so a near of zero collapses every slice onto it and a near of a
    ///     millimetre spends a third of the grid inside the camera.
    /// </remarks>
    public float Near { get; set; } = 0.5f;

    /// <summary>Where its last slice ends. Beyond this <c>!Fog</c>'s analytic falloff carries on.</summary>
    public float Far { get; set; } = 64f;

    /// <summary>How much light a metre of the medium takes out of a ray, at the reference altitude.</summary>
    public float Density { get; set; } = 0.02f;

    /// <summary>What fraction of that is scattered rather than absorbed, per channel.</summary>
    public Vector3 ScatteringAlbedo { get; set; } = new(0.9f, 0.92f, 0.95f);

    /// <summary>Whether density thins with altitude.</summary>
    public bool HeightFalloff { get; set; } = true;

    /// <summary>The altitude the authored density holds at.</summary>
    public float Height { get; set; }

    /// <summary>How fast it thins above that, per world unit.</summary>
    public float HeightFalloffRate { get; set; } = 0.05f;

    /// <summary>Which way the light travels.</summary>
    public Vector3 SunDirection { get; set; } = new(0f, -1f, 0f);

    /// <summary>What it carries.</summary>
    public Vector3 SunColour { get; set; } = new(1f, 0.9f, 0.7f);

    /// <summary>Henyey–Greenstein anisotropy. Air's forward peak is what makes a beam a beam.</summary>
    public float PhaseG { get; set; } = 0.7f;

    /// <summary>What arrives from the whole sky.</summary>
    /// <remarks>
    ///     ⚠ Zero here is not "no ambient", it is a valley that is black whenever the sun is behind
    ///     the viewer — a phase function is normalised over the sphere, so one directional light
    ///     contributes almost nothing outside its forward peak.
    /// </remarks>
    public Vector3 AmbientColour { get; set; } = new(0.35f, 0.42f, 0.55f);

    /// <summary>The cascade atlas the frame's shadow node rendered.</summary>
    /// <remarks>
    ///     ⚠ Naming it is not what turns shadowing on — <see cref="Shadowed" /> is, and it is a fact
    ///     about the frame rather than a setting. A name here that the frame never declared leaves
    ///     the fog unshadowed, which is the honest outcome for a document whose volumetrics run
    ///     without a sun.
    /// </remarks>
    public string ShadowAtlas { get; init; } = "ShadowAtlas";

    /// <summary>The 1×1 texture bound into the shadow slot on a frame that has no atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>Not decoration, and not optional.</b> A descriptor set is written whole or not at
    ///     all, and a permutation does not fold an unused binding away — the layers report
    ///     <c>shadowMap</c> as statically used in the variant that never taps it, which is the same
    ///     finding that made the injection a shader of its own. So the slot is written on every
    ///     frame, and this is what goes into it when there is no atlas to write. <c>TerrainRenderer</c>
    ///     spends a device texture on the identical problem; a declared 1×1 costs the graph nothing
    ///     and needs no upload, because nothing ever reads it.
    /// </remarks>
    public string ShadowStandIn { get; init; } = "FogShadowStandIn";

    /// <summary>The name of the buffer bound into both light slots on a frame that culled nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ShadowStandIn" />'s argument for the other resource kind, and the answer has
    ///         to be a different shape: the light list and the cluster lists are not graph resources
    ///         at all. They arrive as handles the shading pass published, so the slots cannot be
    ///         filled with a declared texture and there is nothing to declare when the frame published
    ///         neither. One device buffer, made once, goes into both.
    ///     </para>
    ///     <para>
    ///         ⚠ Two bindings, one buffer, and that is not a corner cut. The variant that would read
    ///         them is the one this frame is not running, so their <em>contents</em> are unreachable —
    ///         what has to exist is a bindable buffer of each declared kind, and one storage buffer is
    ///         both. Sized past a single element of either stride so that a driver validating the
    ///         range against the shader's declared minimum finds one.
    ///     </para>
    /// </remarks>
    public string LightStandIn { get; init; } = "FogLightStandIn";

    /// <summary>Where the shading pass published its cascades — <c>ShadowMapRenderer.ShaderName</c>.</summary>
    public string ScenePass { get; set; } = "ForwardPlus";

    /// <summary>The frame's constants, which is where the cascades and the biases were published.</summary>
    /// <remarks>
    ///     ⚠ Without it the fog is unshadowed however many shadow nodes the document has. The
    ///     cascades do not travel on the graph — <c>ShadowMapRenderer.Publish</c> writes them into
    ///     this collection under <see cref="ScenePass" />'s name, and this is the only way to them.
    /// </remarks>
    public SceneConstants? Frame { get; set; }

    /// <summary>Whether the froxels may be shadowed at all — the tier's ceiling.</summary>
    /// <remarks>
    ///     ⚠ A ceiling under <see cref="Shadowed" />'s detection and not a second answer to it: false
    ///     gives a glow where a shaft would have been, and true cannot conjure an atlas a frame never
    ///     declared. It exists because the taps are the feature's whole cost — see
    ///     <see cref="PostFidelityQuality.VolumetricShadows" />.
    /// </remarks>
    public bool Shadows { get; init; } = true;

    /// <summary>Whether the last build found everything a shadowed march needs.</summary>
    /// <remarks>
    ///     Availability and not preference, on <c>TerrainSceneRenderer.DetectMode</c>'s rule: the
    ///     atlas is a declared resource and the cascades are published parameters, so the frame has
    ///     already stated the answer and a toggle could only contradict it.
    /// </remarks>
    public bool Shadowed { get; private set; }

    /// <summary>Whether the last build found a light list and cluster lists to index it by.</summary>
    /// <remarks>
    ///     <para>
    ///         The same rule as <see cref="Shadowed" /> and with no ceiling above it, because there is
    ///         nothing to tier: a frame that runs a cluster cull has already paid for the lists, and
    ///         reading thirty-two indices per froxel is what the grid was cut for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>False is the ordinary answer, not the exception.</b> The Standard Frame emits no
    ///         cluster node by default — the lamps are lit per object — so an unmodified document gets
    ///         fog the sun and the sky light, and a document that added a cull gets its lamps in the
    ///         air. Both halves are required: the light list comes from the lighting feature and the
    ///         lists from the shading pass's own published buffers, and either alone indexes nothing.
    ///     </para>
    /// </remarks>
    public bool Clustered { get; private set; }

    /// <summary>The device this node's own resources are created on. Falls back to the frame's.</summary>
    /// <remarks>
    ///     ⚠ <b>Required, and a build without one dispatches nothing.</b> Two of the bindings are
    ///     buffers the graph does not own — see <see cref="LightStandIn" /> — and a set is written
    ///     whole or not at all, so there is no variant that runs without something to put in them.
    /// </remarks>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where descriptor sets come from.</summary>
    public DescriptorAllocator? Allocator { get; set; }

    /// <summary>Where samplers come from.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Where compute pipelines come from.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>What fills the library's compose slots. See <see cref="ComputeRenderer.Composition" />.</summary>
    public ShaderComposition Composition { get; set; } = Materials.MaterialCompiler.PassComposition();

    /// <summary>The three dispatches, in the order they run.</summary>
    public IReadOnlyList<ComputeRenderer> Steps => steps;

    /// <summary>The volume's shape as the last build actually dispatched it.</summary>
    /// <remarks>
    ///     What the document declared, when it declared anything, and <see cref="Resolution" />
    ///     otherwise — so a test can check that the two are not being derived twice.
    /// </remarks>
    public Int3 Dispatched { get; private set; }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        if (Samplers is null || Pipelines is null || Allocator is null) {
            return;
        }

        if ((Device ?? frame.Device) is not { } device) {
            return;
        }

        Acquire(device);

        // The document's declaration wins, and the extent it declared is what everything downstream
        // uses. Declaring here only fills in for a document that named no volumes at all.
        Declare(frame, Media, Resolution);
        Declare(frame, Scattered, Resolution);
        Declare(frame, Volume, Resolution);

        // Both halves, because they publish separately and either alone is useless: the atlas is a
        // graph resource the shadow node declared, and the matrices that index into it are
        // parameters it published. An atlas with no cascades cannot be projected into, and cascades
        // with no atlas have nothing to sample.
        Shadowed = Shadows
            && frame.Has(ShadowAtlas)
            && Frame is { } constants
            && constants.Parameters.Has(ParameterKeys.New<Matrix4x4>($"{ScenePass}.cascades[0].viewProjection"));

        if (!Shadowed) {
            DeclareStandIn(frame);
        }

        // Both halves again, and for the same reason one more time: the light list comes from the
        // lighting feature and the lists from the shading pass's own published buffers, so a frame
        // with one and not the other would index a list nothing filled.
        //
        // ⚠ The handles are last frame's — they are published while a pass *executes*, and this node
        // builds before the pass that publishes them runs. So a frame that has only just started
        // culling lights its fog without lamps for exactly one frame, which is the same one frame
        // `TerrainSceneRenderer` records about the same two buffers.
        Clustered = PublishedBuffer("lightBuffer").IsValid && PublishedBuffer("clusters").IsValid;

        Dispatched = ExtentOf(frame, Volume);

        // The three grid dimensions are permutations, so a variant is compiled per shape — which is
        // what lets Integrate's march be a counted loop rather than a dynamic one.
        Inject(Groups(Dispatched.X, Dispatched.Y, Dispatched.Z));
        Step(1, "Scatter", 0, Scattered, Media, Groups(Dispatched.X, Dispatched.Y, Dispatched.Z));

        // ⚠ Flat in z, and it has to be: the march is a prefix sum along the ray, so one invocation
        // owns a whole column. A dispatch shaped like the volume would have every slice's invocation
        // racing to write every slice.
        Step(2, "Integrate", 1, Volume, Scattered, Groups(Dispatched.X, Dispatched.Y, 1));

        foreach (var step in steps) {
            BuildChild(step, compositor, frame);
        }
    }

    /// <summary>How many 8×8×1 groups cover an extent.</summary>
    static Int3 Groups(int x, int y, int z) => new((x + 7) / 8, (y + 7) / 8, z);

    /// <summary>The extent a name resolves to, or this node's own when nothing recorded one.</summary>
    Int3 ExtentOf(CompositorFrame frame, string name) =>
        frame.DescriptionOf(ToString(), name) is { } description
            ? new(description.Width, description.Height, description.Depth)
            : Resolution;

    /// <summary>The 1×1 that keeps the shadow slot written on a frame with no atlas.</summary>
    /// <remarks>
    ///     <para>
    ///         Single-channel depth-shaped, because that is what the binding is typed as — a tap reads
    ///         <c>.r</c> and compares it. Its contents are never read: the variant that would read them
    ///         is the one this frame is not running.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Imported and not declared, and a declared one did not work.</b> The march declares
    ///         a read of whatever fills the shadow slot — that read is what orders it after the shadow
    ///         passes when there <em>is</em> an atlas — and the graph refuses a read of a transient
    ///         resource no earlier pass writes, by name, at compile: "the contents it would read are
    ///         whatever was in that memory last frame". Which is exactly right about a placeholder and
    ///         exactly the wrong thing to allow in general. An import is memory somebody else owns and
    ///         is answerable without a producer, so this node owns the texture and hands it over.
    ///     </para>
    /// </remarks>
    void DeclareStandIn(CompositorFrame frame) {
        if (frame.Has(ShadowStandIn) || !standIn.IsValid) {
            return;
        }

        frame.Add(ShadowStandIn, frame.Graph.ImportTexture(standIn, standInView, StandInDescription), StandInDescription);
    }

    static void Declare(CompositorFrame frame, string name, Int3 size) {
        if (frame.Has(name)) {
            return;
        }

        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            Math.Max(size.X, 1),
            Math.Max(size.Y, 1),
            TextureUsage.Storage | TextureUsage.Sampled,
            Math.Max(size.Z, 1),
            Dimension: TextureDimension.Texture3D,
            Name: name
        );

        frame.Add(name, frame.Graph.CreateTexture(description), description);
    }

    /// <summary>The medium, which reads nothing and so is a shader of its own.</summary>
    /// <remarks>
    ///     ⚠ A separate shader rather than a third <c>Pass</c>, and a device is what decided it. A set
    ///     is written whole or not at all, so a variant sharing a set with the two that sample a
    ///     volume would have to bind <em>something</em> into that sampled slot — and the only 3D
    ///     textures in the frame at this point are the ones these passes write. Pointing a sampled
    ///     binding at the storage image the same dispatch writes asks the driver for one image in two
    ///     layouts at once, which the validation layers refuse per dispatch
    ///     (<c>VUID-vkCmdDispatch-imageLayout-00344</c>) and a release driver does not. Leaving it
    ///     unbound is not the fix either: the layers then report <c>source</c> as statically used in a
    ///     variant that never calls it, so the permutation is not folding the binding away.
    /// </remarks>
    void Inject(Int3 groups) {
        var node = At(
            0,
            "Inject",
            VolumetricFogInjectKeys.ShaderName,
            VolumetricFogInjectKeys.UsedPermutationKeys,
            VolumetricFogInjectKeys.ConstantBufferBinding
        );

        node.Parameters.Set(VolumetricFogInjectKeys.GridX, Dispatched.X);
        node.Parameters.Set(VolumetricFogInjectKeys.GridY, Dispatched.Y);
        node.Parameters.Set(VolumetricFogInjectKeys.GridZ, Dispatched.Z);
        node.Parameters.Set(VolumetricFogInjectKeys.HeightFalloff, HeightFalloff);

        if (Camera() is { } camera) {
            node.Parameters.Set(VolumetricFogInjectKeys.InverseView, camera.InverseView);
            node.Parameters.Set(VolumetricFogInjectKeys.TanHalfFov, camera.TanHalfFov);
        }

        node.Parameters.Set(VolumetricFogInjectKeys.FogNear, Near);
        node.Parameters.Set(VolumetricFogInjectKeys.FogFar, Far);
        // ⚠ Through the overlay, where a *zero* density is a real answer: a cellar volume that says
        // "no fog here" is how the mist a level-wide volume filled gets cleared out of an interior.
        // Null is the absence of an opinion and leaves the authored value; the two are not the same.
        node.Parameters.Set(VolumetricFogInjectKeys.Density, applied.VolumetricDensity?.Over(Density) ?? Density);

        node.Parameters.Set(
            VolumetricFogInjectKeys.ScatteringAlbedo,
            applied.VolumetricAlbedo?.Over(ScatteringAlbedo) ?? ScatteringAlbedo
        );
        node.Parameters.Set(VolumetricFogInjectKeys.FogHeight, Height);
        node.Parameters.Set(VolumetricFogInjectKeys.HeightFalloffRate, HeightFalloffRate);

        node.Groups = groups;

        node.Reads.Clear();
        node.Writes.Clear();
        node.Descriptors.Bindings.Clear();

        node.Writes.Add(Media);

        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogInjectKeys.TargetBinding,
            Kind = DescriptorKind.StorageTexture,
            Resource = Media
        });
    }

    void Step(int index, string name, int pass, string target, string source, Int3 groups) {
        var node = At(
            index,
            name,
            VolumetricFogKeys.ShaderName,
            VolumetricFogKeys.UsedPermutationKeys,
            VolumetricFogKeys.ConstantBufferBinding
        );

        node.Parameters.Set(VolumetricFogKeys.Pass, pass);
        node.Parameters.Set(VolumetricFogKeys.GridX, Dispatched.X);
        node.Parameters.Set(VolumetricFogKeys.GridY, Dispatched.Y);
        node.Parameters.Set(VolumetricFogKeys.GridZ, Dispatched.Z);

        node.Parameters.Set(VolumetricFogKeys.Shadowed, Shadowed);
        node.Parameters.Set(VolumetricFogKeys.UseClusteredLights, Clustered);

        if (Camera() is { } camera) {
            node.Parameters.Set(VolumetricFogKeys.TanHalfFov, camera.TanHalfFov);

            // ⚠ The same matrix the injection was given, from the same derivation. Two inversions of
            // one camera is a lighting pass shadowing froxels the injection put somewhere else.
            node.Parameters.Set(VolumetricFogKeys.InverseView, camera.InverseView);

            // ⚠ The camera's planes, not this node's Near and Far. They index the *cluster* grid,
            // which the culling pass cut from the camera's near plane to its far one — the fog's own
            // range would read a plausible list culled for somewhere else entirely.
            node.Parameters.Set(VolumetricFogKeys.ClusterNear, camera.Near);
            node.Parameters.Set(VolumetricFogKeys.ClusterFar, camera.Far);
        }

        Shadow(node);

        node.Parameters.Set(VolumetricFogKeys.FogNear, Near);
        node.Parameters.Set(VolumetricFogKeys.FogFar, Far);
        node.Parameters.Set(VolumetricFogKeys.SunDirection, SunDirection);
        node.Parameters.Set(VolumetricFogKeys.SunColour, SunColour);
        node.Parameters.Set(VolumetricFogKeys.PhaseG, applied.VolumetricPhaseG?.Over(PhaseG) ?? PhaseG);
        node.Parameters.Set(VolumetricFogKeys.AmbientColour, AmbientColour);

        node.Groups = groups;

        node.Reads.Clear();
        node.Writes.Clear();
        node.Descriptors.Bindings.Clear();

        node.Writes.Add(target);
        node.Reads.Add(source);

        // ⚠ On both dispatches, not just the one that taps it. Declaring the read is what orders
        // this after the shadow passes and puts the barrier in — and the march's variant carries the
        // binding whether or not it samples, because a permutation does not fold one away.
        node.Reads.Add(Shadowed ? ShadowAtlas : ShadowStandIn);

        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.TargetBinding, Kind = DescriptorKind.StorageTexture, Resource = target
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.ShadowMapBinding,
            Kind = DescriptorKind.SampledTexture,
            Resource = Shadowed ? ShadowAtlas : ShadowStandIn
        });

        // ⚠ Point, where the volumes above are linear, and the difference is load-bearing: the tap
        // compares after sampling, so a filtered read hands the compare the average of four casters
        // — a depth no surface is at, which reads as a shadow edge that shimmers rather than one
        // that steps. `ShadowMapRenderer.Publish` states the same rule for the scene pass.
        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.ShadowSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampled = SamplerDescription.PointClamp
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.SourceBinding, Kind = DescriptorKind.SampledTexture, Resource = source
        });

        // ⚠ Linear, and every read it serves here lands on a texel centre — where linear and point
        // agree exactly. It is linear because the *composite* filters between slices, and one filter
        // for both keeps the volume from meaning one thing written and another read.
        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.VolumeSamplerBinding,
            Kind = DescriptorKind.Sampler,
            Sampled = SamplerDescription.LinearClamp
        });

        // ⚠ Handed over rather than named, and no read is declared for either. They are not graph
        // resources: the shading pass published handles into the scene's parameters, so there is no
        // name to resolve and — because the handles are last frame's — nothing in this frame to order
        // against. Importing them under names of this node's own would hand the graph a second
        // resource over memory it already tracks. See `ResourceBinding.Buffer`.
        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.LightBufferBinding,
            Kind = DescriptorKind.StorageBuffer,
            Buffer = Clustered ? PublishedBuffer("lightBuffer") : lightStandIn
        });

        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.ClustersBinding,
            Kind = DescriptorKind.StorageBuffer,
            Buffer = Clustered ? PublishedBuffer("clusters") : lightStandIn
        });
    }

    /// <summary>The handle a buffer the shading pass published under its own name resolved to.</summary>
    /// <remarks>
    ///     <c>TerrainSceneRenderer.PublishedBuffer</c> verbatim, and the only route to these two: the
    ///     cluster lists and the light list do not travel on a graph edge. Invalid where the frame
    ///     published nothing, which is what <see cref="Clustered" /> is detecting.
    /// </remarks>
    BufferHandle PublishedBuffer(string binding) {
        if (Frame is not { } constants) {
            return default;
        }

        var key = ParameterKeys.New<BufferHandle>($"{ScenePass}.{binding}");

        return constants.Parameters.Has(key) ? constants.Parameters.Get(key) : default;
    }

    /// <summary>Creates the two stand-ins, once, on the device the frame is running on.</summary>
    /// <remarks>
    ///     Their contents are never read and are never written — the variants that would read them are
    ///     the ones a frame without an atlas and without cluster lists is not running. What they have
    ///     to be is bindable, and — for the texture — answerable to the graph without a producer.
    /// </remarks>
    void Acquire(IGraphicsDevice device) {
        if (lightStandIn.IsValid && standIn.IsValid && ReferenceEquals(owner, device)) {
            return;
        }

        Release();

        owner = device;

        lightStandIn = device.CreateBuffer(new(
            StandInSize,
            BufferUsage.Storage,
            MemoryAccess.DeviceLocal,
            LightStandIn
        ));

        standIn = device.CreateTexture(StandInDescription);
        standInView = device.CreateTextureView(standIn);
    }

    void Release() {
        if (owner is { } device) {
            if (lightStandIn.IsValid) {
                device.Destroy(lightStandIn);
            }

            if (standInView.IsValid) {
                device.Destroy(standInView);
            }

            if (standIn.IsValid) {
                device.Destroy(standIn);
            }
        }

        lightStandIn = default;
        standInView = default;
        standIn = default;
        owner = null;
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (var step in steps) {
            step.Dispose();
        }

        steps.Clear();
        Release();
    }

    /// <summary>Copies the frame's published cascades and biases into one dispatch's parameters.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>TerrainSceneRenderer.FillLighting</c>'s shape, for its reason: the cascades are not
    ///         graph resources and do not travel on an edge. <c>ShadowMapRenderer.Publish</c> writes
    ///         them into the scene's parameters under its own shader name, and every consumer that is
    ///         not the scene pass reads them back out under the same name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A missing cascade is padded degenerate rather than skipped.</b> A zero matrix
    ///         fails <c>FrameShadow.Containing</c>'s <c>clip.w > 0</c> test, so the slot can never be
    ///         selected — where a stale matrix left in place from a frame that fitted more would be
    ///         selected, and would shadow the air from somewhere the sun no longer is.
    ///     </para>
    /// </remarks>
    void Shadow(ComputeRenderer node) {
        if (!Shadowed || Frame is not { } constants) {
            return;
        }

        var parameters = constants.Parameters;
        var lastSplit = 0f;

        for (var cascade = 0; cascade < VolumetricFogCascadesElement.Count; cascade++) {
            var index = cascade.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var published = ParameterKeys.New<Matrix4x4>($"{ScenePass}.cascades[{index}].viewProjection");
            var fitted = parameters.Has(published);

            if (fitted) {
                lastSplit = Value(parameters, $"cascades[{index}].split", lastSplit);
            }

            node.Parameters.Set(Slot<Matrix4x4>(index, "viewProjection"), fitted ? parameters.Get(published) : default);
            node.Parameters.Set(Slot<float>(index, "split"), lastSplit);

            node.Parameters.Set(
                Slot<float>(index, "depthScale"),
                fitted ? Value(parameters, $"cascades[{index}].depthScale", 0f) : 0f
            );
        }

        node.Parameters.Set(
            VolumetricFogKeys.ShadowTexelSize,
            Value(parameters, "shadowTexelSize", new Vector2(1f / 1024f, 1f / 1024f))
        );

        // ⚠ The scene pass's own biases, not this node's. A froxel and a wall are shadowed by the
        // same atlas, and two constants would put the air's beam and the ground's shadow at
        // different depths — visible as a shaft that does not meet the shadow it belongs to.
        node.Parameters.Set(VolumetricFogKeys.ShadowConstantBias, Value(parameters, "shadowConstantBias", 0.008f));
        node.Parameters.Set(VolumetricFogKeys.ShadowSlopeBias, Value(parameters, "shadowSlopeBias", 0.01f));
        node.Parameters.Set(VolumetricFogKeys.ShadowFadeRange, Value(parameters, "shadowFadeRange", 10f));
    }

    /// <summary>One element of this shader's cascade array, by the name its plan expanded it to.</summary>
    /// <remarks>
    ///     ⚠ Built here rather than taken from <c>VolumetricFogKeys</c>, which has none: the
    ///     generator emits <see cref="VolumetricFogCascadesElement" /> for writing a block directly
    ///     and no keys for the elements. A compute node fills its block from parameters, so the keys
    ///     have to exist — and the names are not a guess. <see cref="Effect.Parameters" /> holds one
    ///     entry per array <em>element</em>, expanded from the reflection's single
    ///     <c>cascades[].viewProjection</c> to <c>cascades[0]…[3]</c> at offset plus index times
    ///     stride, which is exactly what these spell.
    /// </remarks>
    static ParameterKey<T> Slot<T>(string index, string member) =>
        ParameterKeys.New<T>($"VolumetricFog.cascades[{index}].{member}");

    /// <summary>One published scalar, under the shading pass's qualified name.</summary>
    float Value(ParameterCollection parameters, string name, float fallback) {
        var key = ParameterKeys.New<float>($"{ScenePass}.{name}");

        return parameters.Has(key) ? parameters.Get(key) : fallback;
    }

    /// <summary>And one published vector, on the same terms.</summary>
    Vector2 Value(ParameterCollection parameters, string name, Vector2 fallback) {
        var key = ParameterKeys.New<Vector2>($"{ScenePass}.{name}");

        return parameters.Has(key) ? parameters.Get(key) : fallback;
    }

    /// <summary>
    ///     The view-to-world matrix, the half-angle tangents and the camera's own two planes, derived
    ///     exactly once.
    /// </summary>
    /// <remarks>
    ///     All the grid needs: the tangents turn a grid UV into a view ray whose view depth is one,
    ///     and the matrix puts the result in the world. ⚠ Both passes take them from here rather than
    ///     deriving their own, because two derivations of one frustum is a scatter pass lighting
    ///     froxels the injection put somewhere else. The planes are the <em>cluster</em> grid's, which
    ///     is why they are the camera's and not <see cref="Near" /> and <see cref="Far" />.
    /// </remarks>
    (Matrix4x4 InverseView, Vector2 TanHalfFov, float Near, float Far)? Camera() {
        if (View?.Camera is not { } camera) {
            return null;
        }

        var view = Matrix4x4.LookAt(camera.Position, camera.Position + camera.Forward, camera.Up);

        if (!Matrix4x4.Invert(view, out var inverse)) {
            return null;
        }

        var vertical = MathF.Tan(camera.FieldOfView * 0.5f);

        return (inverse, new Vector2(vertical * camera.AspectRatio, vertical), camera.NearPlane, camera.FarPlane);
    }

    ComputeRenderer At(int index, string name, string shader, IReadOnlyList<ParameterKey> keys, uint constants) {
        while (steps.Count <= index) {
            steps.Add(new() {
                Name = $"{this}.{name}",
                ShaderName = shader,
                PermutationKeys = keys,
                ConstantBinding = constants
            });
        }

        var node = steps[index];

        node.Samplers = Samplers;
        node.Pipelines = Pipelines;
        node.Composition = Composition;
        node.Descriptors.Allocator = Allocator;

        return node;
    }
}
