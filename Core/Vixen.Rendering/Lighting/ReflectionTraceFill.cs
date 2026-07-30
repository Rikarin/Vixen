// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Lighting;

/// <summary>Traces reflections per texel with a compute dispatch — doc 19 § L5's device half.</summary>
/// <remarks>
///     <para>
///         <b>The device half of <c>TracedReflections</c>, and checked against it texel by texel.</b>
///         Every composed answer comes through the slots the kernels already share: the march
///         through <c>distanceField</c>, the hit through <c>surfaceCache</c>, the rough read through
///         <c>irradiance</c>, and the miss through <c>miss</c> — the seat doc 06's reflection probes
///         take without this kernel changing a line.
///     </para>
///     <para>
///         <b>The input planes and the target are the caller's</b> — world positions with validity,
///         normals with roughness, and the storage image the answer lands in. This binds and
///         dispatches; the textures' states are whoever owns them's to arrange, with the target in
///         <see cref="SurfaceCacheTexture.PlaneIsBeingWritten" /> across the dispatch, the way every
///         written plane here is bracketed.
///     </para>
/// </remarks>
public sealed class ReflectionTraceFill : IDisposable {
    /// <summary>The shader this dispatches.</summary>
    public const string ShaderName = ReflectionTraceKeys.ShaderName;

    /// <summary>The four slots, whose fillers are the four properties below.</summary>
    const string FieldSlot = "distanceField";

    const string CacheSlot = "surfaceCache";
    const string RoughSlot = "irradiance";
    const string MissSlot = "miss";

    /// <summary>The kernel's own bindings, by the names the reflection interned.</summary>
    const string PositionsName = "reflectionPositions";

    const string NormalsName = "reflectionNormals";
    const string TargetName = "reflectionAtlas";
    const string DepthName = "depthBuffer";
    const string ColourName = "sceneColor";

    readonly List<DescriptorWrite> writes = [];
    readonly EffectConstants frameBlock;
    readonly EffectConstants materialBlock;

    bool disposed;

    /// <summary>Creates a reflection pass on a device. Nothing is allocated until the first dispatch.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException">There is no device.</exception>
    public ReflectionTraceFill(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        frameBlock = new(device, "ReflectionTrace.Frame");
        materialBlock = new(device, "ReflectionTrace.Material");
    }

    /// <summary>Where the variant is resolved from. Null dispatches nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null dispatches nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>Where the descriptor sets come from. Null dispatches nothing.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The shader behind the field slot — what the mirror rays actually march.</summary>
    public string Source { get; set; } = MaterialCompiler.EmptyFieldShader;

    /// <summary>The shader behind the cache slot — what a hit answers with.</summary>
    public string CacheSource { get; set; } = MaterialCompiler.EmptySurfaceCacheShader;

    /// <summary>The shader behind the irradiance slot — what the rough path reads.</summary>
    public string RoughSource { get; set; } = MaterialCompiler.EmptyIrradianceShader;

    /// <summary>The shader behind the miss slot — the far field, which is the probes' seat.</summary>
    /// <remarks>The sky by default rather than black, because that is what every reflection in
    ///     doc 06 sees beyond the probes today; its one colour is written under
    ///     <c>ReflectionTrace.SkyMissSource.missSkyColor</c> into <see cref="Parameters" />.</remarks>
    public string MissSource { get; set; } = MaterialCompiler.SkyReflectionMissShader;

    /// <summary>What the sets are filled from, by the names the reflection interned.</summary>
    public ParameterCollection Parameters { get; } = new();

    /// <summary>The reflecting surfaces: world position in xyz, validity in alpha.</summary>
    public TextureViewHandle Positions { get; set; }

    /// <summary>Their normals in xyz, roughness in alpha.</summary>
    public TextureViewHandle Normals { get; set; }

    /// <summary>The storage image the answer lands in.</summary>
    public TextureViewHandle Target { get; set; }

    /// <summary>How many texels the planes cover.</summary>
    public Int2 Viewport { get; set; }

    /// <summary>Where the camera stands — the view direction is toward the surface.</summary>
    public Vector3 CameraPosition { get; set; }

    /// <summary>How far a mirror ray looks before deciding it escaped.</summary>
    public float MaxDistance { get; set; } = 100f;

    /// <summary>How far off its surface a mirror ray starts.</summary>
    public float Bias { get; set; } = 0.01f;

    /// <summary>The roughness at and above which the field answers instead of the trace.</summary>
    public float RoughnessThreshold { get; set; } = 0.5f;

    /// <summary>How far below the threshold the cross-fade starts. Zero is the hard switch.</summary>
    public float RoughnessBlend { get; set; }

    /// <summary>The frame's depth, for the screen march — invalid for no screen trace at all.</summary>
    public TextureViewHandle ScreenDepth { get; set; }

    /// <summary>The frame's colour, which is what a screen hit reflects.</summary>
    public TextureViewHandle ScreenColour { get; set; }

    /// <summary>The camera that drew both — the forward matrix.</summary>
    public Matrix4x4 ViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>The viewport they cover, in pixels.</summary>
    public Int2 ScreenViewport { get; set; }

    /// <summary>How many equal steps a screen ray takes. Written as zero while either view is
    ///     invalid, so the descriptors can hold a stand-in the kernel never loads.</summary>
    public int ScreenSteps { get; set; } = 32;

    /// <summary>How deep behind a surface a sample still counts as inside it, in device depth.</summary>
    public float ScreenThickness { get; set; } = 0.02f;

    /// <summary>Whether positions come from <see cref="ScreenDepth" /> rather than a positions plane.</summary>
    /// <remarks>The production wiring — a real frame has a depth, not a positions plane. Requires
    ///     <see cref="ScreenDepth" /> and <see cref="InverseViewProjection" />; the positions
    ///     binding then takes a stand-in the kernel never loads.</remarks>
    public bool ReconstructFromDepth { get; set; }

    /// <summary>What turns a pixel's depth back into the world, when reconstructing.</summary>
    public Matrix4x4 InverseViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>How many dispatches have been recorded.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Why the last call recorded nothing, or null when it recorded something.</summary>
    public string? Skipped { get; private set; }

    /// <summary>Records a dispatch that reflects every valid texel of the planes.</summary>
    /// <param name="commands">An open command list, outside a render pass.</param>
    /// <returns>How many texels were dispatched over, which is zero when nothing could be.</returns>
    /// <exception cref="ArgumentNullException">There is no command list.</exception>
    public int Record(ICommandList commands) {
        ArgumentNullException.ThrowIfNull(commands);
        ObjectDisposedException.ThrowIf(disposed, this);

        Skipped = null;

        if (Effects is null || Pipelines is null || Descriptors is null) {
            return Skip("the pass has no effect system, pipeline cache or descriptor allocator");
        }

        if ((!Positions.IsValid && !ReconstructFromDepth) || !Normals.IsValid || !Target.IsValid) {
            return Skip("the surface planes or the target do not exist, so there is nothing to reflect");
        }

        if (ReconstructFromDepth && !ScreenDepth.IsValid) {
            return Skip("reconstruction was asked for and there is no depth to reconstruct from");
        }

        if (Viewport.X <= 0 || Viewport.Y <= 0) {
            return 0;
        }

        var key = EffectKey.Of(ShaderName)
            .With(
                MaterialCompiler.PassComposition(
                    (FieldSlot, Source),
                    (CacheSlot, CacheSource),
                    (RoughSlot, RoughSource),
                    (MissSlot, MissSource)
                )
            );

        if (Effects.Resolve(key) is not { IsPlaceholder: false } effect) {
            return Skip($"'{key}' has not compiled yet");
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid) {
            return Skip($"'{key}' has no compute stage, so there is no pipeline to dispatch");
        }

        var screen = ScreenDepth.IsValid && ScreenColour.IsValid;

        Parameters.Set(ReflectionTraceKeys.ReflectionViewport, Viewport);
        Parameters.Set(ReflectionTraceKeys.CameraPosition, CameraPosition);
        Parameters.Set(ReflectionTraceKeys.MaxDistance, MaxDistance);
        Parameters.Set(ReflectionTraceKeys.Bias, Bias);
        Parameters.Set(ReflectionTraceKeys.RoughnessThreshold, RoughnessThreshold);
        Parameters.Set(ReflectionTraceKeys.RoughnessBlend, RoughnessBlend);
        Parameters.Set(ReflectionTraceKeys.ViewProjection, ViewProjection);
        Parameters.Set(ReflectionTraceKeys.ScreenViewport, new Vector2(ScreenViewport.X, ScreenViewport.Y));

        // Zero while either view is a stand-in — "a set with a hole in it binds nothing", so the
        // descriptors always point at something in the sampled layout, and this is what guarantees
        // the kernel never loads it.
        Parameters.Set(ReflectionTraceKeys.ScreenSteps, screen ? ScreenSteps : 0);
        Parameters.Set(ReflectionTraceKeys.ScreenThickness, ScreenThickness);
        Parameters.Set(ReflectionTraceKeys.ReconstructFromDepth, ReconstructFromDepth ? 1 : 0);
        Parameters.Set(ReflectionTraceKeys.InverseViewProjection, InverseViewProjection);

        var frame = default(DescriptorSetHandle);

        if (Declares(effect, DescriptorSetSlot.PerFrame) && !TryFrameSet(effect, out frame)) {
            return Skip(
                $"set 0 of '{key}' has bindings nothing filled — the composed sources' resources are "
                + $"written under '{ShaderName}.<source>.*' and something has to put them there"
            );
        }

        if (!TryMaterialSet(effect, out var material)) {
            return Skip($"set 2 of '{key}' could not be filled, so the dispatch would write nowhere");
        }

        commands.BindPipeline(pipeline);

        if (frame.IsValid) {
            commands.BindDescriptorSet(DescriptorSetSlot.PerFrame, frame);
        }

        commands.BindDescriptorSet(DescriptorSetSlot.PerMaterial, material);
        commands.Dispatch((Viewport.X + 7) / 8, (Viewport.Y + 7) / 8, 1);

        Dispatches++;

        return Viewport.X * Viewport.Y;
    }

    /// <summary>Fills set 0 from the names the composed sources' owners wrote.</summary>
    bool TryFrameSet(Effect effect, out DescriptorSetHandle set) {
        set = default;

        var index = (int)DescriptorSetSlot.PerFrame;

        if (effect.SetLayouts.Length <= index || !effect.SetLayouts[index].IsValid) {
            return false;
        }

        var declared = effect.BlockOf(DescriptorSetSlot.PerFrame);

        var block = declared.Exists
            && frameBlock.Update(effect, declared.Size, declared.Members.AsSpan(), Parameters)
                ? frameBlock
                : null;

        if (!EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerFrame, Parameters, block, writes)) {
            return false;
        }

        set = Descriptors!.Allocate(effect.SetLayouts[index], CollectionsMarshal.AsSpan(writes));

        return set.IsValid;
    }

    /// <summary>Fills set 2 — the pass's own block, the two planes and the target.</summary>
    bool TryMaterialSet(Effect effect, out DescriptorSetHandle set) {
        const DescriptorSetSlot Slot = DescriptorSetSlot.PerMaterial;

        set = default;
        writes.Clear();

        var index = (int)Slot;

        if (effect.SetLayouts.Length <= index || !effect.SetLayouts[index].IsValid) {
            return false;
        }

        var declared = effect.BlockOf(Slot);

        if (declared.Exists) {
            if (!materialBlock.Update(effect, declared.Size, declared.Members.AsSpan(), Parameters)) {
                return false;
            }

            writes.Add(
                DescriptorWrite.Uniform(declared.Binding, materialBlock.Buffer, materialBlock.Offset, materialBlock.Size)
            );
        }

        if (effect.BindingOf(PositionsName) is not { } positions || effect.BindingOf(NormalsName) is not { } normals) {
            return false;
        }

        // Reconstructing, the positions binding takes the normals plane as its stand-in — the
        // descriptor must point at something in the sampled layout, and the mode flag is what
        // guarantees the kernel never loads it.
        writes.Add(DescriptorWrite.Texture(positions.Binding, Positions.IsValid ? Positions : Normals));
        writes.Add(DescriptorWrite.Texture(normals.Binding, Normals));

        // With no screen to trace, the positions plane stands in: the descriptor must point at
        // something in the sampled layout, and zero screenSteps means the kernel never loads it.
        if (effect.BindingOf(DepthName) is { } depth) {
            writes.Add(DescriptorWrite.Texture(depth.Binding, ScreenDepth.IsValid ? ScreenDepth : Positions));
        }

        if (effect.BindingOf(ColourName) is { } colour) {
            writes.Add(DescriptorWrite.Texture(colour.Binding, ScreenColour.IsValid ? ScreenColour : Positions));
        }

        if (effect.BindingOf(TargetName) is not { } target) {
            return false;
        }

        writes.Add(DescriptorWrite.StorageImage(target.Binding, Target));

        set = Descriptors!.Allocate(effect.SetLayouts[index], CollectionsMarshal.AsSpan(writes));

        return set.IsValid;
    }

    /// <summary>Whether a variant has anything at all in one of its sets.</summary>
    static bool Declares(Effect effect, DescriptorSetSlot slot) {
        foreach (var binding in effect.Bindings) {
            if (binding.Set == slot) {
                return true;
            }
        }

        return false;
    }

    int Skip(string reason) {
        Skipped = reason;

        return 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        frameBlock.Dispose();
        materialBlock.Dispose();
    }
}
