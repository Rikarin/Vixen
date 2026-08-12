// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>
///     What one material's resolve dispatch needs beyond the material itself.
/// </summary>
/// <param name="Material">The material, whose composition selects the variant and whose parameters fill it.</param>
/// <param name="Index">Which bin it is, as <c>Meshlet.MaterialIndex</c> numbers them.</param>
/// <remarks>
///     Two numbers for one material, and they are genuinely different things: the index is the model's
///     submesh slot, written into the cluster records at import, and the material is the asset a project
///     bound to that slot. Nothing derives one from the other — a caller that registered a mesh is the
///     only thing that knows both.
/// </remarks>
public readonly record struct ResolveMaterial(Material Material, int Index);

/// <summary>
///     Shading the visibility buffer: one indirect dispatch per material, over the tiles that material
///     is actually in.
/// </summary>
/// <remarks>
///     <para>
///         <b>The second half of phase 5 of <c>docs/plan/22-virtualized-geometry.md</c>, and the reason
///         improvement 2 is worth the bookkeeping.</b> <see cref="GpuVisibilityTiles" /> bins the screen
///         and this dispatches over one material's bin, so a material covering one per cent of the
///         screen shades one per cent of the pixels — against a full-screen material-depth pass, which
///         is what resolving into a GBuffer costs.
///     </para>
///     <para>
///         <b>A variant per material, and the composition is the material's own.</b> The key is
///         <c>VisibilityResolve</c> with the material's <see cref="Material.Composition" /> filling the
///         same two slots <c>ForwardPlus</c> composes — so a material is one surface feature chain and
///         one shading model whichever pass reaches it, which is what "a second entry contract, not a
///         second material system" has to mean. What differs is one permutation:
///         <c>UseAnalyticGradients</c>, because a compute stage has no quad to take derivatives from.
///     </para>
///     <para>
///         <b>Every binding is filled by name, through the effect's own plan.</b> The resolve's
///         resources and the material's parameters go into one collection and
///         <see cref="EffectSetWriter" /> writes the set from the variant's reflection — the same route
///         <see cref="Features.MaterialRenderFeature" /> takes, and for the same reason: what a
///         composed shader's set contains is whatever the material's features brought, so a host that
///         numbered the bindings would be numbering a layout it cannot see.
///     </para>
///     <para>
///         <b>The dispatch is indirect and the host never learns the tile count.</b> The binning pass
///         wrote it into the argument triple this reads, so a material covering the whole screen and one
///         covering none are the same command.
///     </para>
/// </remarks>
public sealed class GpuClusterResolve : IDisposable {
    readonly IGraphicsDevice device;
    readonly Dictionary<int, Bin> bins = [];
    readonly List<DescriptorWrite> writes = [];
    readonly ParameterCollection parameters = new();

    RenderView? view;
    bool disposed;

    /// <summary>Creates a resolve that dispatches on a device.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public GpuClusterResolve(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    /// <summary>Where the resolve variants are compiled. Null resolves nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipelines come from. Null resolves nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>The traversal whose clusters are being shaded. Null resolves nothing.</summary>
    public GpuClusterVisibility? Visibility { get; set; }

    /// <summary>The binning whose lists are being dispatched over. Null resolves nothing.</summary>
    public GpuVisibilityTiles? Tiles { get; set; }

    /// <summary>The page pool the geometry is read out of. Null resolves nothing.</summary>
    public MeshletPagePool? Pages { get; set; }

    /// <summary>
    ///     Which material each bin holds. Set by whoever registered the meshes.
    /// </summary>
    /// <remarks>
    ///     A list rather than an index into <see cref="Features.MaterialRenderFeature" />'s, because the
    ///     bins are numbered by <c>Meshlet.MaterialIndex</c> — the model's own submesh slots, decided at
    ///     import — and a project binds an asset to each. A bin with no material here is a bin nothing
    ///     dispatches, which is a hole rather than a wrong colour and is reported by
    ///     <see cref="Unresolved" />.
    /// </remarks>
    public IReadOnlyList<ResolveMaterial> Materials { get; set; } = [];

    /// <summary>
    ///     The lights, the shadow atlas, the environment and the irradiance field: set 0.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The forward pass's own per-frame constants, bound unchanged.</b> Since the lighting
    ///         moved into <c>ClusteredShading</c> the resolve declares exactly the bindings a forward
    ///         draw does, so what fills them is what already fills them. A resolve that built its own
    ///         would be a second opinion about where the sun is.
    ///     </para>
    ///     <para>
    ///         The object rather than a set handle, because <see cref="SceneConstants.Bind" /> writes the
    ///         set from the <em>effect's</em> reflection — and there is a different effect per material
    ///         here. A pre-built handle would be a set allocated from one variant's layout and bound to
    ///         another's.
    ///     </para>
    /// </remarks>
    public SceneConstants? Scene { get; set; }

    /// <summary>The camera's constants: set 1.</summary>
    public ViewConstants? ViewConstants { get; set; }

    /// <summary>The per-draw set: set 3, which holds the unclustered light list and the probe scalars.</summary>
    /// <remarks>
    ///     Invalid on a clustered frame, which binds none — a fragment finds its own lights in the grid,
    ///     and a resolve invocation does the same. The block's reflection probe scalars then fall back to
    ///     whatever set 3 last held, which is the behaviour <c>ClusteredShading.ProbeIndex</c> already
    ///     documents for the forward path and is why a probe is read from the object record where there
    ///     is one.
    /// </remarks>
    public DescriptorSetHandle DrawSet { get; set; }

    /// <summary>The table every material's textures live in.</summary>
    /// <remarks>
    ///     Bound as a whole set rather than written into this one, because a bindless table owns its own
    ///     descriptors — see <see cref="DescriptorSetSlot.Bindless" />. Invalid binds nothing, which is
    ///     right for a frame whose materials are all untextured.
    /// </remarks>
    public DescriptorSetHandle TextureTable { get; set; }

    /// <summary>Whether the variants compile the environment ambient term in.</summary>
    public bool ImageBasedLighting { get; set; } = true;

    /// <summary>
    ///     Whether they read indirect diffuse from a field.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Off by default, which matches the forward pass: <c>UseIrradianceField</c> is off there too
    ///         and indirect diffuse reaches a shipped frame through
    ///         <c>PostFx/IndirectDiffuse.rvn</c> instead. What this gives the resolve is the <em>same
    ///         choice</em> the forward pass has rather than a different answer to it — which is the whole
    ///         claim, and it was not true until <c>VisibilityResolve</c> could declare the slot.
    ///     </para>
    ///     <para>
    ///         Turning it on without composing a field into the material gets <c>NoIrradiance</c>, which
    ///         answers no light and an unshadowed sun. That is a slower way of leaving it off, not a
    ///         wrong picture.
    ///     </para>
    /// </remarks>
    public bool IrradianceField { get; set; }

    /// <summary>Whether the resolve splits its answer across four planes — the ambient split.</summary>
    /// <remarks>
    ///     <para>
    ///         On <c>ForwardPlus.SplitOutputs</c>'s exact terms, because the resolve shades into the
    ///         same frame: colour keeps direct light, emissive and specular ambient; the albedo plane
    ///         carries diffuse albedo with the material's occlusion in alpha; the normal plane the
    ///         shading normal with roughness in alpha; the specular plane the surface's <c>f0</c>.
    ///         Diffuse ambient becomes <c>!AmbientCombine</c>'s job. A frame that splits one path and
    ///         not the other shades the same material two ways on two sides of a tile boundary, so
    ///         whoever sets this sets the forward key too.
    ///     </para>
    ///     <para>
    ///         ⚠ The three extra planes are bindings in <em>every</em> variant — a binding is declared,
    ///         not read into existence — so <see cref="Prepare" /> always fills them: with the caller's
    ///         planes here, and with the colour target aliased when this is off, which costs three
    ///         descriptor writes and no bytes.
    ///     </para>
    /// </remarks>
    public bool SplitOutputs { get; set; }

    /// <summary>How many materials the last <see cref="Prepare" /> resolved a variant for.</summary>
    public int ResolvedMaterials { get; private set; }

    /// <summary>How many were asked for and could not be.</summary>
    /// <remarks>
    ///     A variant still compiling, a material with no composition, or a bin nothing bound a material
    ///     to. All three are a bin that dispatches nothing, which is a hole in the picture — and a hole
    ///     that fills itself a few frames later once the variant lands is worth telling apart from one
    ///     that never will.
    /// </remarks>
    public int Unresolved { get; private set; }

    /// <summary>How many dispatches the last <see cref="Record" /> issued.</summary>
    public int Dispatches { get; private set; }

    /// <summary>The permutation that swaps implicit derivatives for supplied ones.</summary>
    /// <remarks>
    ///     Unqualified, because it is one key across the compilation: the declaration is on
    ///     <c>MaterialTextures</c> and every feature that inherits it reads the same value. That is the
    ///     right shape — a pass either has a quad or it does not, and a composition where half the
    ///     features sampled implicitly is not a configuration anybody wants.
    /// </remarks>
    public const string AnalyticGradientsKey = "UseAnalyticGradients";

    /// <summary>The effect key one material's resolve variant is compiled under.</summary>
    /// <param name="material">The material, whose composition fills the shader's slots.</param>
    /// <param name="imageBasedLighting">Whether the ambient term is compiled in.</param>
    /// <param name="irradianceField">
    ///     Whether indirect diffuse is read from a field. On the same terms as the forward pass's key of
    ///     the same name, and it takes the material's own composition to say <em>which</em> field — a
    ///     variant compiled with this on and <c>NoIrradiance</c> composed reads a field that answers
    ///     nothing, which is a slower way of leaving it off rather than a wrong picture.
    /// </param>
    /// <param name="splitOutputs">
    ///     Whether the variant shades the ambient split — <c>ForwardPlus.SplitOutputs</c>'s key, on
    ///     the forward pass's exact terms. See <see cref="SplitOutputs" />.
    /// </param>
    /// <remarks>
    ///     Built literally rather than through <see cref="EffectKey.From" />, because the gradient key is
    ///     declared on a shader the reflection does not publish — it is inherited into every sampling
    ///     feature — so there is no generated <see cref="ParameterKey" /> to look it up by. Three names
    ///     and three values is a small enough surface to spell.
    /// </remarks>
    public static EffectKey Key(
        Material material,
        bool imageBasedLighting = true,
        bool irradianceField = false,
        bool splitOutputs = false
    ) {
        ArgumentNullException.ThrowIfNull(material);

        return EffectKey.Of(
            VisibilityResolveKeys.ShaderName,
            [
                new(AnalyticGradientsKey, "true"),
                new(VisibilityResolveKeys.UseImageBasedLighting.Name, imageBasedLighting ? "true" : "false"),
                new(VisibilityResolveKeys.UseIrradianceField.Name, irradianceField ? "true" : "false"),
                new(VisibilityResolveKeys.SplitOutputs.Name, splitOutputs ? "true" : "false")
            ],
            material.Composition
        );
    }

    /// <summary>
    ///     Resolves a variant per material and writes the set each dispatch will bind.
    /// </summary>
    /// <param name="view">The camera the triangles are reconstructed against.</param>
    /// <param name="target">Where the shaded colour goes.</param>
    /// <param name="identities">The visibility buffer the raster wrote.</param>
    /// <param name="size">Its size, in pixels.</param>
    /// <param name="albedo">
    ///     Where the split's albedo plane goes, when <see cref="SplitOutputs" /> is on. Invalid
    ///     aliases <paramref name="target" /> into the binding, which the off variant never writes.
    /// </param>
    /// <param name="normal">The split's normal plane, on the same terms.</param>
    /// <param name="specular">The split's f0 plane, on the same terms.</param>
    /// <returns>Whether anything is ready to dispatch.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="view" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         Outside the frame's command list, because writing a descriptor set is not a recorded
    ///         operation and the values are the host's. <see cref="Record" /> is the half that goes in
    ///         the list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A registered traversal is not a prepared one.</b>
    ///         <see cref="GpuClusterVisibility.MeshCount" /> counts registrations and survives a
    ///         <see cref="GpuClusterVisibility.Prepare" /> that returned false — most ordinarily because
    ///         the culling variant is still compiling, which is what the first frames after a
    ///         shader-cache miss look like. Seven of this set's bindings are that object's buffers and
    ///         they are made inside that <c>Prepare</c>, so a resolve that went ahead on the strength of
    ///         the registration wrote seven well-formed descriptors naming nothing —
    ///         <see cref="EffectSetWriter" /> asks whether a name is <em>set</em>, not whether what it is
    ///         set to exists, so the set completed and every counter reported a healthy pass. The frame's
    ///         virtualized geometry is simply absent for those frames, which is the same outcome a bin
    ///         whose own variant is still compiling already has, one granularity up: there is no fallback
    ///         to take, because <c>MeshExtractionSystem</c> sends an object down the virtualized path or
    ///         the ordinary one and never both.
    ///     </para>
    /// </remarks>
    public bool Prepare(
        RenderView view,
        TextureViewHandle target,
        TextureViewHandle identities,
        Int2 size,
        TextureViewHandle albedo = default,
        TextureViewHandle normal = default,
        TextureViewHandle specular = default
    ) {
        ArgumentNullException.ThrowIfNull(view);
        ObjectDisposedException.ThrowIf(disposed, this);

        // Remembered for Record, which binds the view's own set and runs inside a command list where a
        // caller has nothing left to hand it.
        this.view = view;

        ResolvedMaterials = 0;
        Unresolved = 0;

        if (Effects is null
            || Pipelines is null
            || Visibility is not { MeshCount: > 0 } visibility
            || !visibility.Visible.IsValid
            || Tiles is not { } tiles
            || Pages is not { } pages
            || !target.IsValid
            || !identities.IsValid) {
            return false;
        }

        foreach (var entry in Materials) {
            if (entry.Material is null || entry.Index < 0 || entry.Index >= GpuVisibilityTiles.MaxMaterials) {
                Unresolved++;
                continue;
            }

            if (Effects.Resolve(Key(entry.Material, ImageBasedLighting, IrradianceField, SplitOutputs))
                is not { IsPlaceholder: false } effect) {
                // A variant still compiling. The bin dispatches nothing this frame and the picture has a
                // hole in it, which is the honest outcome: shading with a placeholder would be a wrong
                // colour nobody could attribute, and the real variant arrives in a frame or two.
                Unresolved++;
                continue;
            }

            var pipeline = Pipelines.GetOrCreate(effect);

            if (!pipeline.IsValid) {
                Unresolved++;
                continue;
            }

            var bin = Bin.For(bins, entry.Index, device);

            // The colour aliased rather than left unfilled: the three split planes are bindings of
            // every variant, a set is written wholly or not at all, and the off variant never stores
            // through them — so the alias is three descriptor writes buying a set that always
            // completes.
            var albedoPlane = albedo.IsValid ? albedo : target;
            var normalPlane = normal.IsValid ? normal : target;
            var specularPlane = specular.IsValid ? specular : target;

            if (!bin.Fill(
                    device,
                    effect,
                    entry,
                    this,
                    visibility,
                    tiles,
                    pages,
                    view,
                    target,
                    identities,
                    size,
                    albedoPlane,
                    normalPlane,
                    specularPlane,
                    parameters,
                    writes
                )) {
                Unresolved++;
                continue;
            }

            bin.Pipeline = pipeline;
            ResolvedMaterials++;
        }

        return ResolvedMaterials > 0;
    }

    /// <summary>Records one indirect dispatch per prepared material.</summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>How many dispatches were recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         One command per material and no count anywhere in the host: the workgroup count is the
    ///         tile count the binning pass atomically appended into the same word the dispatch reads.
    ///     </para>
    ///     <para>
    ///         ⚠ The target must already be in <see cref="ResourceState.ShaderWrite" /> and the binning's
    ///         outputs in <see cref="ResourceState.IndirectArgument" /> and
    ///         <see cref="ResourceState.ShaderRead" />. Those transitions belong to whoever owns the two
    ///         resources across the frame, which is <see cref="Compositor.VisibilityBufferRenderer" />.
    ///     </para>
    /// </remarks>
    public int Record(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        Dispatches = 0;

        if (Tiles is not { } tiles || !tiles.Arguments.IsValid) {
            return 0;
        }

        foreach (var (index, bin) in bins) {
            if (!bin.Ready) {
                continue;
            }

            list.BindPipeline(bin.Pipeline);

            // Written per variant rather than bound from a handle, for the reason `Scene` gives: the
            // layout is the effect's, and there is an effect per material.
            if (bin.AllocatedFor is { } effect) {
                Scene?.Bind(list, effect);
            }

            if (ViewConstants is not null && view is not null) {
                ViewConstants.Bind(list, view);
            }

            if (DrawSet.IsValid) {
                list.BindDescriptorSet(DescriptorSetSlot.PerDraw, DrawSet);
            }

            if (TextureTable.IsValid) {
                list.BindDescriptorSet(DescriptorSetSlot.Bindless, TextureTable);
            }

            list.BindDescriptorSet(DescriptorSetSlot.PerMaterial, bin.Set);
            list.DispatchIndirect(tiles.Arguments, GpuVisibilityTiles.ArgumentOffset(index));

            bin.Ready = false;
            Dispatches++;
        }

        return Dispatches;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var bin in bins.Values) {
            bin.Dispose(device);
        }

        bins.Clear();
    }

    /// <summary>One material's variant, its constants and the set it dispatches with.</summary>
    /// <remarks>
    ///     Per material rather than per frame, because a variant is compiled per material and a set
    ///     points at a block whose contents differ per material — the tile base and the bin index are in
    ///     it. The constants carry their own ring, so a frame in flight is not reading what this frame
    ///     is writing.
    /// </remarks>
    sealed class Bin {
        public EffectConstants? Constants;
        public DescriptorSetHandle Set;
        public PipelineHandle Pipeline;
        public Effect? AllocatedFor;
        public bool Ready;

        public static Bin For(Dictionary<int, Bin> bins, int index, IGraphicsDevice device) {
            _ = device;

            if (!bins.TryGetValue(index, out var bin)) {
                bins[index] = bin = new();
            }

            return bin;
        }

        public bool Fill(
            IGraphicsDevice device,
            Effect effect,
            in ResolveMaterial entry,
            GpuClusterResolve owner,
            GpuClusterVisibility visibility,
            GpuVisibilityTiles tiles,
            MeshletPagePool pages,
            RenderView view,
            TextureViewHandle target,
            TextureViewHandle identities,
            Int2 size,
            TextureViewHandle albedo,
            TextureViewHandle normal,
            TextureViewHandle specular,
            ParameterCollection parameters,
            List<DescriptorWrite> writes
        ) {
            // The material's own parameters first, then the pass's — so a pass value wins where the two
            // share a name, which they should not and which would otherwise be silent.
            parameters.Clear();
            parameters.Apply(entry.Material.Parameters);

            parameters.Set(VisibilityResolveKeys.Identities, identities);
            parameters.Set(VisibilityResolveKeys.Target, target);
            parameters.Set(VisibilityResolveKeys.AlbedoTarget, albedo);
            parameters.Set(VisibilityResolveKeys.NormalTarget, normal);
            parameters.Set(VisibilityResolveKeys.SpecularTarget, specular);
            parameters.Set(VisibilityResolveKeys.Visible, visibility.Visible);
            parameters.Set(VisibilityResolveKeys.Instances, visibility.Instances);
            parameters.Set(VisibilityResolveKeys.Geometry, visibility.Geometry);
            parameters.Set(VisibilityResolveKeys.Meshes, visibility.Grids);
            parameters.Set(VisibilityResolveKeys.Residency, visibility.Slots);
            parameters.Set(VisibilityResolveKeys.Pages, pages.Pages);
            parameters.Set(VisibilityResolveKeys.Bones, visibility.Bones);
            parameters.Set(VisibilityResolveKeys.ClusterMaterials, visibility.Materials);
            parameters.Set(VisibilityResolveKeys.Tiles, tiles.Tiles);
            parameters.Set(VisibilityResolveKeys.PageSize, (uint)visibility.PageSize);

            // The three ringed buffers, reached by base index rather than by descriptor offset — which is
            // not a preference: this set is filled by EffectSetWriter, and a writer that binds a buffer
            // whole has nowhere to put an offset. Before these existed the resolve read instance records
            // and the slot table from region zero while the raster read this frame's, so the two passes
            // agreed on every frame the ring happened to be at zero.
            parameters.Set(VisibilityResolveKeys.InstanceBase, (uint)visibility.InstanceBase);
            parameters.Set(VisibilityResolveKeys.BoneBase, (uint)visibility.BoneBase);
            parameters.Set(VisibilityResolveKeys.ResidencyBase, (uint)visibility.SlotBase);
            parameters.Set(VisibilityResolveKeys.TileBase, (uint)tiles.TileBase(entry.Index));
            parameters.Set(VisibilityResolveKeys.Material, (uint)entry.Index);
            parameters.Set(VisibilityResolveKeys.TileCount, GpuVisibilityTiles.TilesFor(size));
            parameters.Set(VisibilityResolveKeys.ViewProjection, view.ViewProjection);

            // Everything else the shading needs — the sun, the cascades, the environment, the clustered
            // light list — is in sets 0, 1 and 3, and those are the frame's own. See FrameSet.

            Constants ??= new(device, $"VisibilityResolve.{entry.Index}");
            Constants.Update(effect, parameters);

            if (!EffectSetWriter.TryWrite(effect, DescriptorSetSlot.PerMaterial, parameters, Constants, writes)) {
                // A binding with nothing to fill it. Binding a partly-written set is a validation error on
                // one backend and a black sample on another, and neither says which name was missing — so
                // the bin dispatches nothing and the counter says so.
                return false;
            }

            if (!ReferenceEquals(AllocatedFor, effect) || !Set.IsValid) {
                if (Set.IsValid) {
                    device.Destroy(Set);
                }

                Set = device.CreateDescriptorSet(
                    effect.SetLayouts[(int)DescriptorSetSlot.PerMaterial],
                    $"VisibilityResolve.{entry.Index}"
                );

                AllocatedFor = effect;
            }

            if (!Set.IsValid) {
                return false;
            }

            device.UpdateDescriptorSet(Set, writes.ToArray());
            Ready = true;

            return true;
        }

        public void Dispose(IGraphicsDevice device) {
            if (Set.IsValid) {
                device.Destroy(Set);
                Set = default;
            }

            Constants?.Dispose();
            Constants = null;
        }
    }
}
