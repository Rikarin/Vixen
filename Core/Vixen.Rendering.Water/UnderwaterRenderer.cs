// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>
///     The waterline: what a camera straddling the surface sees, divided by a curve.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D9](../../docs/plan/35-water.md#d9-underwater-is-a-post-process-volume-and-the-waterline-is-named-as-the-hard-part)'s
///         second half, and the document warns about it twice.</b> "Designing the volume path first
///         and discovering the waterline second is how you get a system where the transition is a hard
///         cut and the fix is architectural." The volume path is <see cref="UnderwaterShape" /> over
///         doc 32's fold and it grades the <em>whole frame</em>; this is the other feature, which a
///         fold cannot be.
///     </para>
///     <para>
///         ⚠ <b>A fold produces one weight and a waterline is a curve.</b> A camera half in the water
///         needs two treatments in one frame divided by the intersection of the wave surface with the
///         near plane — which is a per-pixel question, and no number the volume system can produce is
///         an answer to it.
///     </para>
///     <para>
///         ⚠ <b>The local surface plane rather than the wave sum, and the approximation is stated.</b>
///         The exact answer needs the info texture and the Gerstner sum bound into a post-process
///         node, which is a second place for § D2's seam test to have to hold. Over the few
///         centimetres a near plane spans, a wave is its own tangent plane to well under a millimetre.
///         What it costs is a crest smaller than the near plane passing the camera, which is spray
///         rather than a waterline.
///     </para>
///     <para>
///         ⚠ <b>The plane comes from the same <see cref="WaterQuery" /> the volume fold and the
///         buoyancy solver read, at the same water time</b> — § D2 applied to one more consumer. A
///         waterline drawn against the rest height would sit at mean sea level while the drawn surface
///         moved around it, which reads as the camera being wrong rather than the line being wrong.
///     </para>
///     <para>
///         ⚠ <b>The distortion and the caustics are the <em>volume's</em> and not the lens's</b>,
///         which § D9 says outright: they are driven by the submersion depth the fold computes, so a
///         head just under the surface wobbles and one at ten metres does not. A lens property would
///         wobble a cutscene camera flying over the lake.
///     </para>
/// </remarks>
public sealed class UnderwaterRenderer : SceneRenderer, IDisposable {
    readonly FullScreenRenderer pass;
    readonly List<ResourceBinding> bindings = [];

    bool disposed;

    /// <summary>Creates the node.</summary>
    public UnderwaterRenderer() {
        pass = new() {
            Name = UnderwaterKeys.ShaderName,
            ShaderName = UnderwaterKeys.ShaderName,
            PermutationKeys = UnderwaterKeys.UsedPermutationKeys,
            ConstantBinding = UnderwaterKeys.ConstantBufferBinding
        };
    }

    /// <summary>The target it writes — the frame's scene colour.</summary>
    public required string Output { get; init; }

    /// <summary>The copy of the frame so far, which is what it grades.</summary>
    /// <remarks>
    ///     ⚠ <b>A copy, on <see cref="WaterRenderer.Behind" />'s terms and for the same reason.</b>
    ///     Sampling a target this pass is also writing is undefined — not slow, not approximate. And
    ///     this pass <em>displaces</em> its reads, so it is the one where a driver that seemed to
    ///     tolerate it would show the tolerance up as a smear.
    /// </remarks>
    public required string Behind { get; init; }

    /// <summary>The opaque scene's depth, which says how far the fog has to reach.</summary>
    public required string SceneDepth { get; init; }

    /// <summary>The surface plane: device depth in <c>r</c>, coverage in <c>g</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Read for where the ray <em>leaves</em> the water, which is not what it is read for in
    ///     <see cref="WaterRenderer" />.</b> Under the surface, a coverage of one means the surface is
    ///     between the eye and the scene — so the fog path stops there. A diver looking up sees the
    ///     sky through a metre of water; taking the distance to the sky instead makes looking up
    ///     exactly as dark as looking down at the bed, which is the failure that reads as "underwater
    ///     is just a blue filter".
    /// </remarks>
    public required string Surface { get; init; }

    /// <summary>The view the camera matrices are derived from, or null to set them by hand.</summary>
    public RenderView? View { get; set; }

    /// <summary>Where the zones — and therefore the local surface plane — come from.</summary>
    /// <remarks>
    ///     ⚠ <b>Without one the node grades nothing at all, rather than grading everything.</b> A
    ///     waterline with no water is a frame with the whole world fogged blue, which is worse than a
    ///     frame with no effect — so the submersion stays at its "above" value and every pixel passes
    ///     through. <c>!ScreenProbeGather</c>'s terms.
    /// </remarks>
    public WaterZoneSystem? Zones { get; set; }

    /// <summary>Clip back to world, when no <see cref="View" /> supplies it.</summary>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>Where the camera is, which the path lengths are measured from.</summary>
    public Vector3 CameraPosition { get; set; }

    /// <summary>A point on the surface near the camera. Set from <see cref="Zones" /> when there is one.</summary>
    /// <remarks>
    ///     ⚠ <b>Its default is a kilometre-scale sentinel below the world, and that is the "no water"
    ///     answer written as a number.</b> A plane at the origin with an upward normal grades
    ///     everything below <c>y = 0</c>, so a node whose zone system was never wired would fog the
    ///     bottom half of every frame in a project whose ground happens to sit below zero — which
    ///     reads as the effect working. Far below, the plane test saturates to zero in one subtraction
    ///     with no branch and no infinity.
    /// </remarks>
    public Vector3 SurfacePoint { get; set; } = new(0f, -1e9f, 0f);

    /// <summary>The surface's normal there, which is what tilts the waterline with the swell.</summary>
    public Vector3 SurfaceNormal { get; set; } = Vector3.UnitY;

    /// <summary>How far below the surface the camera is, in metres. Negative is above.</summary>
    public float Submersion { get; set; } = -1f;

    /// <summary>How wide the waterline's fade is, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Narrow rather than soft, and never zero.</b> A hard step across a curve that moves
    ///     with the swell is an aliased crawling edge that sends people to their antialiasing settings
    ///     to fix a geometry problem. Four centimetres is a line.
    /// </remarks>
    public float WaterlineFeather { get; set; } = 0.04f;

    /// <summary>How much light the medium scatters out of a ray per metre, per channel.</summary>
    /// <remarks>
    ///     ⚠ <b>The same triple <c>!Water</c> integrates with, or the lake changes colour when you put
    ///     your head under</b> — which reads as a bug in whichever pass you look at second. The
    ///     document's own defaults match on both nodes for that reason.
    /// </remarks>
    public Vector3 Scattering { get; set; } = new(0.03f, 0.06f, 0.09f);

    /// <summary>How much it absorbs per metre, per channel.</summary>
    public Vector3 Absorption { get; set; } = new(0.35f, 0.06f, 0.02f);

    /// <summary>Henyey–Greenstein anisotropy, carried so the medium is one description.</summary>
    public float PhaseG { get; set; } = 0.7f;

    /// <summary>
    ///     What arrives from the whole sky, <b>as a radiance in cd/m²</b>, when <see cref="Frame" />
    ///     has no sky in it. ⚠ Without it everything below the surface is black.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The same quantity <c>WaterRenderer.SkyColour</c> is, and it has to hold the same
    ///     number for the same reason <see cref="Scattering" /> does</b> — a sky that changed value at
    ///     the waterline is a lake that changes colour when you put your head under. Both defaults are
    ///     a clear daylight sky, and both prefer the frame's own environment over them.
    /// </remarks>
    public Vector3 SkyColour { get; set; } = new(1400f, 1680f, 2200f);

    /// <summary>The frame's own lighting, which is where the sky's mean radiance comes from.</summary>
    /// <remarks>
    ///     <c>WaterRenderer.Frame</c>'s property, from the same <c>CompositorBuilder.SceneConstants</c>,
    ///     read per frame for the same two reasons: the environment is filled by the frame's sky node
    ///     after the compositor is built, and the mean radiance is <c>L00·Y₀·Intensity</c> rather than
    ///     the coefficient itself. Null leaves <see cref="SkyColour" /> alone.
    /// </remarks>
    public SceneConstants? Frame { get; set; }

    /// <summary>§ D8's scale on what shows through.</summary>
    public Vector3 BehindScale { get; set; } = Vector3.One;

    /// <summary>Whether what is seen is refracted at all.</summary>
    public bool Distortion { get; set; } = true;

    /// <summary>How far the refraction wobble displaces what is seen, in UV.</summary>
    public float DistortionAmount { get; set; } = 0.004f;

    /// <summary>How many wobbles across the screen, and how fast they travel.</summary>
    public float DistortionScale { get; set; } = 12f;

    /// <summary>How fast the wobble travels.</summary>
    public float DistortionSpeed { get; set; } = 0.6f;

    /// <summary>Whether the moving caustic bands are added.</summary>
    public bool Caustics { get; set; } = true;

    /// <summary>How bright they are.</summary>
    public float CausticAmount { get; set; } = 0.12f;

    /// <summary>How large a caustic cell is.</summary>
    public float CausticScale { get; set; } = 1.5f;

    /// <summary>How fast the pattern drifts.</summary>
    public float CausticSpeed { get; set; } = 0.35f;

    /// <summary>How deep the caustics fade out over, in metres.</summary>
    /// <remarks>⚠ They are a surface phenomenon; a diver at thirty metres sees none.</remarks>
    public float CausticDepth { get; set; } = 12f;

    /// <summary>The simulation's water time, in seconds.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off <see cref="Zones" /> rather than settable, on
    ///     <see cref="WaterMeshRenderer.WaterTime" />'s terms.</b> There is exactly one water clock in
    ///     a running game, and a wobble running on a second one is a refraction that drifts out of
    ///     step with the surface it is supposed to be refracting through.
    /// </remarks>
    public float WaterTime => Zones?.WaterTime ?? 0f;

    /// <summary>Where shader modules come from. Set before the first frame that builds.</summary>
    public EffectPipelineDescriber? Modules {
        get => pass.Modules;
        set => pass.Modules = value;
    }

    /// <summary>Where samplers come from, shared so a frame does not exhaust the device's supply.</summary>
    public SamplerCache? Samplers { get; set; }

    /// <summary>Where the pass's descriptor sets come from.</summary>
    public DescriptorAllocator? Allocator {
        get => pass.Descriptors.Allocator;
        set => pass.Descriptors.Allocator = value;
    }

    /// <summary>The device its pipelines and its uniform block are created on.</summary>
    public IGraphicsDevice? Device {
        get => pass.Device;
        set => pass.Device = value;
    }

    /// <summary>The pass this node is, for a host that wants to look at what it did.</summary>
    public FullScreenRenderer Pass => pass;

    /// <summary>How many frames have declared the pass.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Whether the last build found the camera under water at all.</summary>
    /// <remarks>
    ///     The reading behind "the underwater grade is not coming on". It is false for a camera above
    ///     the surface, a camera outside every zone's window, and a node with no zone system — three
    ///     causes with three different fixes, which is why <c>stat water</c> counts the last two.
    /// </remarks>
    public bool IsSubmerged { get; private set; }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);

        if (string.Equals(Behind, Output, StringComparison.Ordinal)) {
            throw new CompositorBindingException(
                ToString(),
                "target",
                Behind,
                "is both what the underwater composite reads and what it writes. Sampling a target a "
                + "pass is also writing is undefined — and this pass displaces its reads, so a driver "
                + "that seemed to tolerate it would show the tolerance up as a smear. Put a !Copy node "
                + "ahead of this one and name its destination here. See docs/plan/35 § B1"
            );
        }

        Degrade(Declare(compositor, frame));
    }

    /// <summary>Declares the composite, or says why it did not.</summary>
    /// <remarks>
    ///     ⚠ The pass is this node's own rather than a node a document named, so it is not in
    ///     <see cref="SceneRenderer.Nested" /> and its answer is carried out here — see
    ///     <see cref="WaterRenderer" /> for the same shape.
    /// </remarks>
    string? Declare(GraphicsCompositor compositor, CompositorFrame frame) {
        if (Samplers is null || pass.Device is null) {
            // A document naming !Underwater in a host that has not wired the renderer up should cost
            // a frame with no grade, not an exception — !Water's terms exactly.
            return Samplers is null
                ? "no Samplers, so the underwater composite was never declared — a submerged camera "
                + "sees the surface frame ungraded, which reads as being above the water"
                : "no Device on the composite pass, so it was never declared — a submerged camera "
                + "sees the surface frame ungraded, which reads as being above the water";
        }

        pass.Samplers = Samplers;
        pass.ColourTargets.Clear();
        pass.Reads.Clear();
        pass.BufferReads.Clear();
        pass.Descriptors.Bindings.Clear();
        bindings.Clear();

        pass.ColourTargets.Add(Output);

        if (View is { } view) {
            CameraPosition = view.Position;

            if (Matrix4x4.Invert(view.ViewProjection, out var inverse)) {
                InverseViewProjection = inverse;
            }
        }

        Locate();

        pass.Parameters.Set(UnderwaterKeys.Distortion, Distortion);
        pass.Parameters.Set(UnderwaterKeys.Caustics, Caustics);
        pass.Parameters.Set(UnderwaterKeys.InverseViewProjection, InverseViewProjection);
        pass.Parameters.Set(UnderwaterKeys.CameraPosition, CameraPosition);
        pass.Parameters.Set(UnderwaterKeys.SurfacePoint, SurfacePoint);
        pass.Parameters.Set(UnderwaterKeys.SurfaceNormal, SurfaceNormal);
        pass.Parameters.Set(UnderwaterKeys.WaterlineFeather, WaterlineFeather);
        pass.Parameters.Set(UnderwaterKeys.Submersion, Submersion);
        pass.Parameters.Set(UnderwaterKeys.Scattering, Scattering);
        pass.Parameters.Set(UnderwaterKeys.Absorption, Absorption);
        pass.Parameters.Set(UnderwaterKeys.PhaseG, PhaseG);
        // ⚠ The frame's own sky where there is one, on WaterRenderer.Lighting's terms — the two nodes
        // integrate the same medium and a sky read differently either side of the waterline is a lake
        // that changes colour when the camera crosses it.
        pass.Parameters.Set(
            UnderwaterKeys.SkyColour,
            Frame?.Lighting?.Environment is { } environment
                ? environment.Irradiance.L00 * (0.282095f * environment.Intensity)
                : SkyColour
        );
        pass.Parameters.Set(UnderwaterKeys.BehindScale, BehindScale);
        pass.Parameters.Set(UnderwaterKeys.DistortionAmount, DistortionAmount);
        pass.Parameters.Set(UnderwaterKeys.DistortionScale, DistortionScale);
        pass.Parameters.Set(UnderwaterKeys.DistortionSpeed, DistortionSpeed);
        pass.Parameters.Set(UnderwaterKeys.CausticAmount, CausticAmount);
        pass.Parameters.Set(UnderwaterKeys.CausticScale, CausticScale);
        pass.Parameters.Set(UnderwaterKeys.CausticSpeed, CausticSpeed);
        pass.Parameters.Set(UnderwaterKeys.CausticDepth, CausticDepth);
        pass.Parameters.Set(UnderwaterKeys.WaterTime, WaterTime);

        Read(UnderwaterKeys.SceneColourCopyBinding, Behind);
        Read(UnderwaterKeys.SceneDepthBinding, SceneDepth);
        Read(UnderwaterKeys.WaterSurfaceBinding, Surface);

        bindings.Add(
            new() {
                Binding = UnderwaterKeys.PointSamplerBinding,
                Kind = DescriptorKind.Sampler,
                Sampler = Samplers.PointClamp
            }
        );

        bindings.Add(
            new() {
                Binding = UnderwaterKeys.LinearSamplerBinding,
                Kind = DescriptorKind.Sampler,
                Sampler = Samplers.LinearClamp
            }
        );

        foreach (var binding in bindings) {
            pass.Descriptors.Bindings.Add(binding);
        }

        BuildCount++;
        BuildChild(pass, compositor, frame);

        return pass.Degraded;
    }

    /// <summary>Finds the surface plane under the camera, out of the zone the camera is in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same <see cref="WaterQuery" /> the buoyancy solver and the volume fold read,
    ///         at the same water time.</b> § D2's whole claim is that there is one definition of where
    ///         the surface is; a waterline evaluated from anything else would be a line that is
    ///         somewhere the water is not, on a frame where nothing looks obviously wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A camera outside every window is treated as above water rather than as an error.</b>
    ///         It is what a camera flying away from a lake is, and the whole-frame blue that the other
    ///         answer produces would be a much louder bug than the one it prevents.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>With no <see cref="Zones" /> at all it changes nothing, and the difference between
    ///         that and "no zone reaches" is the point.</b> A host that has not wired the system up has
    ///         not made a claim about where the water is, so whatever a document or a fixture set
    ///         stands; a host that <em>has</em> wired it up and got no answer has made the claim "there
    ///         is no water here", and this overwrites with it. Collapsing the two would make the node
    ///         impossible to drive by hand — which is how an image fixture reaches it, and how the
    ///         collapse was found.
    ///     </para>
    /// </remarks>
    void Locate() {
        if (Zones is null) {
            IsSubmerged = Submersion > 0f;

            return;
        }

        IsSubmerged = false;

        if (Zones.QueryAt(new(CameraPosition.X, CameraPosition.Z)) is not { } query) {
            Dry();

            return;
        }

        var sample = query.Sample(new(CameraPosition.X, CameraPosition.Z), WaterTime);

        if (!sample.IsWet) {
            Dry();

            return;
        }

        SurfacePoint = new(CameraPosition.X, sample.Height, CameraPosition.Z);
        SurfaceNormal = sample.Normal;
        Submersion = sample.Height - CameraPosition.Y;
        IsSubmerged = Submersion > 0f;
    }

    /// <summary>The plane for a camera the zones say has no water under it.</summary>
    /// <remarks>
    ///     The same sentinel <see cref="SurfacePoint" />'s default is, for the same reason: a plane
    ///     under the world grades nothing, in one subtraction with no branch.
    /// </remarks>
    void Dry() {
        Submersion = -1f;
        SurfacePoint = new(CameraPosition.X, -1e9f, CameraPosition.Z);
        SurfaceNormal = Vector3.UnitY;
    }

    /// <summary>Adds a sampled texture to the pass, and records that it is read.</summary>
    void Read(uint binding, string resource) {
        if (string.IsNullOrEmpty(resource)) {
            return;
        }

        pass.Reads.Add(resource);
        bindings.Add(new() { Binding = binding, Kind = DescriptorKind.SampledTexture, Resource = resource });
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        pass.Dispose();
    }
}
