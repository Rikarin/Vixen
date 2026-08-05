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
public sealed class VolumetricFogRenderer : SceneRenderer {
    readonly List<ComputeRenderer> steps = [];

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

        // The document's declaration wins, and the extent it declared is what everything downstream
        // uses. Declaring here only fills in for a document that named no volumes at all.
        Declare(frame, Media, Resolution);
        Declare(frame, Scattered, Resolution);
        Declare(frame, Volume, Resolution);

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
        node.Parameters.Set(VolumetricFogInjectKeys.Density, Density);
        node.Parameters.Set(VolumetricFogInjectKeys.ScatteringAlbedo, ScatteringAlbedo);
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

        if (Camera() is { } camera) {
            node.Parameters.Set(VolumetricFogKeys.TanHalfFov, camera.TanHalfFov);
        }

        node.Parameters.Set(VolumetricFogKeys.FogNear, Near);
        node.Parameters.Set(VolumetricFogKeys.FogFar, Far);
        node.Parameters.Set(VolumetricFogKeys.SunDirection, SunDirection);
        node.Parameters.Set(VolumetricFogKeys.SunColour, SunColour);
        node.Parameters.Set(VolumetricFogKeys.PhaseG, PhaseG);
        node.Parameters.Set(VolumetricFogKeys.AmbientColour, AmbientColour);

        node.Groups = groups;

        node.Reads.Clear();
        node.Writes.Clear();
        node.Descriptors.Bindings.Clear();

        node.Writes.Add(target);
        node.Reads.Add(source);

        node.Descriptors.Bindings.Add(new() {
            Binding = VolumetricFogKeys.TargetBinding, Kind = DescriptorKind.StorageTexture, Resource = target
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
    }

    /// <summary>
    ///     The view-to-world matrix and the half-angle tangents, derived exactly once.
    /// </summary>
    /// <remarks>
    ///     All the grid needs: the tangents turn a grid UV into a view ray whose view depth is one,
    ///     and the matrix puts the result in the world. ⚠ Both passes take them from here rather than
    ///     deriving their own, because two derivations of one frustum is a scatter pass lighting
    ///     froxels the injection put somewhere else.
    /// </remarks>
    (Matrix4x4 InverseView, Vector2 TanHalfFov)? Camera() {
        if (View?.Camera is not { } camera) {
            return null;
        }

        var view = Matrix4x4.LookAt(camera.Position, camera.Position + camera.Forward, camera.Up);

        if (!Matrix4x4.Invert(view, out var inverse)) {
            return null;
        }

        var vertical = MathF.Tan(camera.FieldOfView * 0.5f);

        return (inverse, new Vector2(vertical * camera.AspectRatio, vertical));
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
