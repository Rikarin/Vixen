// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>
///     A sub-feature that decides part of which shader variant its objects need.
/// </summary>
/// <remarks>
///     <para>
///         Skinning and instancing are not material settings — a mesh is skinned because it has a
///         skeleton, not because an artist ticked a box — yet both change which shader has to be
///         compiled. This is how a sub-feature says so without <see cref="MaterialRenderFeature" />
///         knowing that either exists.
///     </para>
///     <para>
///         Booleans only, and that is a real limit rather than an oversight: a permutation with more
///         than two states multiplies the cache by more than two, and every case that has come up so
///         far — skinned, instanced, shadow-receiving — is a flag. An integer permutation like a
///         light-count bucket belongs on the material, where a human chose it.
///     </para>
/// </remarks>
public interface IPermutationSubFeature {
    /// <summary>The permutation keys this sub-feature decides.</summary>
    /// <remarks>
    ///     The name is the <em>renderer's</em> — <c>Vixen.Skinned</c>, not <c>ForwardPlus.Skinned</c>
    ///     — because one feature drives the same flag across every shader that has it, and a key per
    ///     shader would mean a feature that had to enumerate them. A host maps it onto a shader by
    ///     listing it in <see cref="MaterialRenderFeature.PermutationKeys" /> for that shader, which
    ///     is the same place the shader's own permutations are declared.
    /// </remarks>
    IReadOnlyList<PermutationKey<bool>> PermutationKeys { get; }

    /// <summary>This sub-feature's value for one object and one of its keys.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="index">Which of <see cref="PermutationKeys" />.</param>
    bool ValueOf(RenderSystem system, RenderObjectId id, int index);
}

/// <summary>
///     Which material each object uses, and which shader variant that resolves to.
/// </summary>
/// <remarks>
///     <para>
///         This is where the shader half of the engine meets the renderer half. Preparation turns a
///         material's <see cref="ParameterCollection" /> — plus whatever the object's other
///         sub-features contribute through <see cref="IPermutationSubFeature" /> — into an
///         <see cref="EffectKey" />, resolves it through the <see cref="EffectSystem" />, and
///         remembers the answer per object, so by the time anything is recorded "which shader" is an
///         array lookup.
///     </para>
///     <para>
///         <strong>Resolution happens in preparation, not in the draw call, and not in
///         extraction.</strong> Not in the draw call because resolving can compile, and compiling
///         inside a command list is the stall that a frame budget cannot absorb. Not in extraction
///         because the answer is only needed for objects that survived culling, which in a
///         well-culled scene is far fewer — the same reason the phase exists at all.
///     </para>
///     <para>
///         <strong>Per distinct variant, not per object.</strong> The per-object step is building a
///         small flag mask and one dictionary lookup; building the key and asking the effect system
///         happens once per <em>(material, flags)</em> pair that has ever been seen. Ten thousand
///         objects over twenty materials, half of them skinned, is forty resolutions.
///     </para>
///     <para>
///         <strong>The sort group is derived from the resolved effect.</strong> That is what turns
///         the renderer's depth sort into a state-change-minimising one: objects that will bind the
///         same pipeline get the same group, land adjacent in the stage's list, and reach the mesh
///         feature as one run it can bind once for. A sort group taken from anything else — a
///         material id, a mesh id — would group things that do not share a pipeline and separate
///         things that do.
///     </para>
/// </remarks>
public sealed class MaterialRenderFeature : SubRenderFeature, IDisposable {
    readonly List<Material> materials = [];
    readonly Dictionary<Material, int> indices = [];
    readonly Dictionary<EffectKey, uint> groups = new();
    readonly List<Variant> variants = [];
    readonly Dictionary<(int Material, uint Flags, string Shader), int> variantIndices = [];
    readonly List<IPermutationSubFeature> contributors = [];
    readonly ParameterCollection scratch = new();
    readonly List<EffectConstants?> blocks = [];
    readonly List<DescriptorWrite> writes = [];

    /// <summary>Variant × stage → the variant that stage's shader override resolved to, or 0.</summary>
    /// <remarks>
    ///     A flat array rather than a dictionary because it is read once per draw. Variants are tens
    ///     and stages are at most sixty-four, so the whole table is a few kilobytes — where a
    ///     dictionary probe in the draw loop would be the one lookup added to every object in the
    ///     frame for the benefit of the two stages that override anything.
    /// </remarks>
    int[] overrides = [];
    int stageCount;

    /// <inheritdoc />
    public override string Name => "Material";

    /// <summary>Each object's index into <see cref="Materials" />, or 0 for none.</summary>
    public RenderDataKey<int> MaterialIndex { get; private set; }

    /// <summary>Each object's resolved variant — its effect and its sort group.</summary>
    /// <remarks>
    ///     Separate from <see cref="MaterialIndex" /> because one material is more than one variant
    ///     as soon as a sub-feature contributes a permutation: a skinned and an unskinned object can
    ///     share a material and must not share a pipeline.
    /// </remarks>
    public RenderDataKey<int> VariantIndex { get; private set; }

    /// <summary>The materials this feature knows about.</summary>
    public IReadOnlyList<Material> Materials => materials;

    /// <summary>How many distinct variants preparation has resolved.</summary>
    /// <remarks>Includes the "no material" sentinel at index 0.</remarks>
    public int VariantCount => variants.Count;

    /// <summary>Which permutation keys the shader's variants are selected by, per shader name.</summary>
    /// <remarks>
    ///     <para>
    ///         Supplied rather than discovered, because it is a property of the compiled shader and
    ///         this cannot compile one. The generated <c>…Keys.UsedPermutationKeys</c> is exactly
    ///         this list, so a host registers it once per shader and the key is built from the same
    ///         set the compiler reported.
    ///     </para>
    ///     <para>
    ///         A sub-feature's contributed keys are <em>not</em> added automatically. A shader that
    ///         does not branch on <c>Skinned</c> must not have it in its key, or the cache splits in
    ///         two for variants that would compile to the same bytes — which is the difference
    ///         between a tractable cache and 2ⁿ entries where a handful are distinct.
    ///     </para>
    ///     <para>
    ///         A shader with no entry gets an empty set and therefore one variant, which is right for
    ///         a shader that declares no permutations and wrong-but-harmless for one that does — it
    ///         resolves to the default variant rather than to nothing.
    ///     </para>
    /// </remarks>
    public Dictionary<string, IReadOnlyList<ParameterKey>> PermutationKeys { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Where effects are resolved from. Set before the first frame that prepares.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>
    ///     The device a material's own descriptor set is written on, or null to leave that to a host.
    /// </summary>
    /// <remarks>
    ///     Set this and <see cref="Descriptors" /> together and a material binds itself: its uniform
    ///     block, its textures and its samplers, all resolved from the effect's binding plan. Leave
    ///     either null and nothing changes — <see cref="DescriptorsOf" /> falls back to
    ///     <see cref="Material.Descriptors" />, which is what every host did before this existed and
    ///     what one that owns an unusual set still wants.
    /// </remarks>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where a material's descriptor set comes from.</summary>
    /// <remarks>
    ///     The frame allocator rather than a set created once and kept, because a material's values
    ///     are not constant: an artist moves a slider, a script swaps a texture, and a set rewritten
    ///     in place is one rewritten while an unfinished frame may still be reading it. The allocator
    ///     hands back the <em>same</em> set for the same writes, so a material nobody touched costs a
    ///     hash rather than a write — which is the whole reason it compares them.
    /// </remarks>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>How many variants have a set this feature wrote.</summary>
    public int BoundCount { get; private set; }

    /// <summary>How many times a material's uniform block has actually gone to the GPU.</summary>
    /// <remarks>
    ///     The set is rewritten every frame — the frame allocator's cache is a frame long, which is
    ///     what makes it safe for values that change. The <em>bytes</em> are the part worth not
    ///     repeating, and this is how a test says they are not: a material nobody touched uploads
    ///     once and stays uploaded.
    /// </remarks>
    public int UploadCount {
        get {
            var total = 0;

            foreach (var block in blocks) {
                total += block?.UploadCount ?? 0;
            }

            return total;
        }
    }

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        MaterialIndex = system.Objects.Data.Register<int>();
        VariantIndex = system.Objects.Data.Register<int>();

        // Zero is a valid index and the arrays start zeroed, so an object nobody assigned a material
        // to would silently claim the first one. Registering a null sentinel at 0 makes the default
        // mean "none" without every caller having to write -1.
        materials.Add(null!);
        variants.Add(new(null, uint.MaxValue));
        blocks.Add(null);
    }

    /// <summary>Registers a material and returns the index objects refer to it by.</summary>
    public int Add(Material material) {
        ArgumentNullException.ThrowIfNull(material);

        if (indices.TryGetValue(material, out var existing)) {
            return existing;
        }

        var index = materials.Count;
        materials.Add(material);
        indices[material] = index;
        return index;
    }

    /// <summary>Points an object at a material.</summary>
    public void Assign(RenderSystem system, RenderObjectId id, Material material) {
        ArgumentNullException.ThrowIfNull(system);
        system.Objects.Data.Data(MaterialIndex)[id.Index] = Add(material);
    }

    /// <inheritdoc />
    protected internal override void Prepare(RenderSystem system) {
        ArgumentNullException.ThrowIfNull(system);

        if (Effects is null || Parent is null) {
            return;
        }

        // Gathered here rather than at Initialize, because a sub-feature may be attached after this
        // one — the mesh feature's own order of Add calls should not decide whether skinning works.
        contributors.Clear();

        foreach (var subFeature in Parent.SubFeatures) {
            if (subFeature is IPermutationSubFeature contributor && contributor.PermutationKeys.Count > 0) {
                contributors.Add(contributor);
            }
        }

        var materialIndices = system.Objects.Data.Data(MaterialIndex);
        var variantIndex = system.Objects.Data.Data(VariantIndex);
        var objects = system.Objects.All;

        // The table is indexed by stage count, so a stage added after the first frame moves every
        // entry in it. Dropping it is cheap and the alternative is an override read at the wrong
        // slot — which draws one stage's shader in another's pass.
        if (stageCount != system.Stages.Count) {
            stageCount = system.Stages.Count;
            overrides = [];
        }

        for (var index = 0; index < objects.Length; index++) {
            ref readonly var candidate = ref objects[index];

            if (!candidate.IsAlive || candidate.FeatureIndex != Parent.Index) {
                continue;
            }

            if (!IsVisibleAnywhere(system, index)) {
                continue;
            }

            var material = materialIndices[index];

            if (material <= 0 || material >= materials.Count) {
                variantIndex[index] = 0;
                continue;
            }

            var id = new RenderObjectId(index);
            var resolved = VariantOf(system, id, material, materials[material].ShaderName, composes: true);
            variantIndex[index] = resolved;

            // Resolved here, in preparation, for the same reason the base variant is: a stage
            // override resolves through the effect system and resolving can compile. Only the stages
            // this object actually appears in, so a prepass costs nothing for an object that is not
            // in one.
            foreach (var stage in candidate.Stages.Indices()) {
                if (stage < stageCount && system.Stages[stage] is { ShaderName: { Length: > 0 } shader } overriding) {
                    Override(system, id, material, resolved, stage, shader, overriding.ShaderComposes);
                }
            }
        }

        // After the object loop, so it runs once per variant rather than once per object — which is
        // the same economy the variant table itself exists for. Ten thousand objects over twenty
        // materials is twenty descriptor sets.
        Bind();
    }

    /// <summary>
    ///     Writes each variant's per-material descriptor set from its own effect's binding plan.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>What used to be the host's job, and could not be until the plan reached the
    ///         runtime.</strong> A material knows it has a texture called <c>albedo</c>; which binding
    ///         index that is belongs to the compiled shader, and adding a texture above it in the
    ///         <c>.rvn</c> renumbers it. So the name is what a material carries and
    ///         <see cref="Effect.Bindings" /> is what turns it into an index — the same fix, and the
    ///         same argument, as the one that made a compositor node's bindings authorable.
    ///     </para>
    ///     <para>
    ///         The uniform block is written too, and it has to be: a set with a hole in it is a
    ///         validation error rather than an unused slot, and every material shader has a block
    ///         even if the material set none of its values. <see cref="EffectConstants" /> fills it
    ///         from the same parameters, defaults included, and re-uploads only when they change.
    ///     </para>
    /// </remarks>
    void Bind() {
        if (Device is null || Descriptors is null) {
            return;
        }

        BoundCount = 0;

        for (var index = 1; index < variants.Count; index++) {
            var variant = variants[index];

            if (variant.Effect is not { } effect || variant.Material <= 0 || variant.Material >= materials.Count) {
                continue;
            }

            const int slot = (int)DescriptorSetSlot.PerMaterial;

            if (effect.SetLayouts.Length <= slot || !effect.SetLayouts[slot].IsValid) {
                continue;
            }

            var material = materials[variant.Material];
            var block = Constants(index, effect, material);

            writes.Clear();
            var wanted = 0;

            foreach (var binding in effect.Bindings) {
                if (binding.Set != DescriptorSetSlot.PerMaterial) {
                    continue;
                }

                wanted++;

                if (Write(binding, effect, material, block) is { } write) {
                    writes.Add(write);
                }
            }

            // Every binding or none. A set short of an entry is a validation error on one backend and
            // a sampled black texture on another, and neither says which material forgot which
            // texture — where an object that does not draw at all is unmistakable, and the material
            // that owns it is the one being looked at.
            if (wanted == 0 || writes.Count != wanted) {
                continue;
            }

            variants[index] = variant with {
                Set = Descriptors.Allocate(effect.SetLayouts[slot], System.Runtime.InteropServices.CollectionsMarshal.AsSpan(writes))
            };

            BoundCount++;
        }
    }

    /// <summary>One binding, as the write that fills it — or null when nothing can.</summary>
    /// <remarks>
    ///     A resource the material never set leaves the binding out rather than writing a null handle.
    ///     The set is then short of an entry and this feature declines to allocate it at all, which is
    ///     the right failure: a partly-written set is a validation error on one backend and a sampled
    ///     black texture on another, and neither says which material forgot which texture.
    /// </remarks>
    static DescriptorWrite? Write(EffectBinding binding, Effect effect, Material material, EffectConstants? block) {
        // The uniform block, which is the effect's own and not a value the material carries.
        if (binding.Kind is DescriptorKind.UniformBuffer or DescriptorKind.DynamicUniformBuffer) {
            return block is { Size: > 0 } filled
                ? DescriptorWrite.Uniform(binding.Binding, filled.Buffer, filled.Offset, filled.Size)
                : null;
        }

        // Qualified by the shader, because that is how the generator interns it: a material sets
        // `LightingKeys.Albedo`, whose name is "Lighting.albedo", and the shader calls it "albedo".
        if (!ParameterKeys.TryGet($"{effect.Key.ShaderName}.{binding.Name}", out var key)) {
            return null;
        }

        return binding.Kind switch {
            DescriptorKind.SampledTexture or DescriptorKind.StorageTexture =>
                material.Parameters.Has(key) && key is ParameterKey<TextureViewHandle> texture
                    ? DescriptorWrite.Texture(binding.Binding, material.Parameters.Get(texture))
                    : null,
            DescriptorKind.Sampler =>
                material.Parameters.Has(key) && key is ParameterKey<SamplerHandle> sampler
                    ? DescriptorWrite.SamplerAt(binding.Binding, material.Parameters.Get(sampler))
                    : null,
            DescriptorKind.StorageBuffer or DescriptorKind.DynamicStorageBuffer =>
                material.Parameters.Has(key) && key is ParameterKey<BufferHandle> buffer
                    ? DescriptorWrite.Storage(binding.Binding, material.Parameters.Get(buffer))
                    : null,
            _ => null
        };
    }

    /// <summary>The variant's uniform block, created on first use and refilled when values change.</summary>
    EffectConstants? Constants(int variant, Effect effect, Material material) {
        if (effect.ConstantBufferSize == 0) {
            return null;
        }

        var block = blocks[variant] ??= new(Device!, material.ShaderName);
        return block.Update(effect, material.Parameters) ? block : null;
    }

    /// <summary>The effect an object resolved to for a stage, or null when it has none.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="stage">
    ///     The stage drawing it. A stage with a <see cref="RenderStage.ShaderName" /> gets the variant
    ///     that override resolved to; null and every other stage get the material's own.
    /// </param>
    public Effect? EffectOf(RenderSystem system, RenderObjectId id, RenderStage? stage = null) {
        ArgumentNullException.ThrowIfNull(system);
        return variants[IndexFor(system, id, stage)].Effect;
    }

    /// <summary>The descriptor set an object's material binds, invalid when it has none.</summary>
    /// <param name="system">The render system.</param>
    /// <param name="id">The object.</param>
    /// <param name="stage">The stage drawing it, for the same reason <see cref="EffectOf" /> takes one.</param>
    /// <remarks>
    ///     The set this feature wrote when it has a <see cref="Device" /> and an allocator, and the
    ///     material's own otherwise. In that order rather than the reverse: a host that set
    ///     <see cref="Material.Descriptors" /> by hand and then turned this on would otherwise get the
    ///     hand-written one forever and no sign that the new path was doing nothing.
    /// </remarks>
    public DescriptorSetHandle DescriptorsOf(RenderSystem system, RenderObjectId id, RenderStage? stage = null) {
        ArgumentNullException.ThrowIfNull(system);

        if (variants[IndexFor(system, id, stage)].Set is { IsValid: true } written) {
            return written;
        }

        var index = system.Objects.Data.Data(MaterialIndex)[id.Index];
        return index > 0 && index < materials.Count ? materials[index].Descriptors : default;
    }

    /// <summary>The sort group for an object: its variant's, so equal pipelines sort together.</summary>
    /// <remarks>
    ///     Read from the array preparation filled rather than recomputed. Sorting asks this once per
    ///     visible object per stage, and building an <see cref="EffectKey" /> there would put string
    ///     work inside the frame's hottest loop.
    /// </remarks>
    /// <remarks>
    ///     Takes the stage because a stage that overrides the shader also changes what groups
    ///     together — and in a prepass every object resolves to the same override, so they all share a
    ///     group and the sort collapses to pure front-to-back, which is exactly what a prepass wants.
    /// </remarks>
    public uint SortGroupOf(RenderSystem system, RenderObjectId id, RenderStage? stage = null) {
        ArgumentNullException.ThrowIfNull(system);
        return variants[IndexFor(system, id, stage)].Group;
    }

    /// <summary>Which entry in <see cref="variants" /> an object uses in a stage.</summary>
    int IndexFor(RenderSystem system, RenderObjectId id, RenderStage? stage) {
        var index = system.Objects.Data.Data(VariantIndex)[id.Index];

        if (index <= 0 || index >= variants.Count) {
            return 0;
        }

        if (stage is not { ShaderName.Length: > 0 } || stage.Index < 0 || stage.Index >= stageCount) {
            return index;
        }

        var slot = (index * stageCount) + stage.Index;
        return slot < overrides.Length && overrides[slot] > 0 ? overrides[slot] : index;
    }

    int VariantOf(RenderSystem system, RenderObjectId id, int material, string shaderName, bool composes) {
        var flags = FlagsOf(system, id);

        if (variantIndices.TryGetValue((material, flags, shaderName), out var existing)) {
            // A variant resolved while its shader was still being compiled holds a placeholder, and
            // the real one arrives some frames later with nothing to announce it. Asking again is how
            // that is noticed. It costs one dictionary lookup, and only for the variants still
            // waiting — which is none of them for the whole of a shipping run.
            if (variants[existing].Effect is { IsPlaceholder: true }) {
                var waiting = variants[existing];
                variants[existing] = waiting with { Effect = Effects!.Resolve(waiting.Key) };
            }

            return existing;
        }

        var source = materials[material];

        // A scratch collection layered material-then-contributions, so a sub-feature's flag wins over
        // a material that happened to set the same key. The material describes a surface; whether
        // that surface is on a skeleton is not the material's to claim.
        scratch.Clear();
        scratch.Apply(source.Parameters);
        Contribute(system, id);

        // The material's own shader was authored against its features, so it always takes them. A
        // stage override is a different shader and says for itself — see RenderStage.ShaderComposes,
        // which is what keeps a depth prepass to one variant rather than one per material.
        var composition = composes ? source.Composition : ShaderComposition.Empty;
        var key = EffectKey.From(shaderName, scratch, KeysFor(shaderName), composition);

        if (!groups.TryGetValue(key, out var group)) {
            // Dense and assigned in first-seen order. The value means nothing on its own — only that
            // two objects sharing an effect share a group, which is what puts them adjacent in the
            // sorted list.
            group = (uint)groups.Count;
            groups[key] = group;
        }

        var index = variants.Count;
        variants.Add(new(Effects!.Resolve(key), group, key, material));
        blocks.Add(null);
        variantIndices[(material, flags, shaderName)] = index;
        return index;
    }

    /// <summary>Resolves and records what one variant becomes in a stage that overrides the shader.</summary>
    void Override(
        RenderSystem system,
        RenderObjectId id,
        int material,
        int variant,
        int stage,
        string shader,
        bool composes
    ) {
        var slot = (variant * stageCount) + stage;

        if (slot < overrides.Length && overrides[slot] > 0) {
            return;
        }

        var resolved = VariantOf(system, id, material, shader, composes);

        // Sized after resolving rather than before: VariantOf may have added the override itself, and
        // the table has to be long enough for whichever of the two indices is larger.
        var required = variants.Count * stageCount;

        if (overrides.Length < required) {
            Array.Resize(ref overrides, required);
        }

        overrides[slot] = resolved;
    }

    void Contribute(RenderSystem system, RenderObjectId id) {
        foreach (var contributor in contributors) {
            for (var i = 0; i < contributor.PermutationKeys.Count; i++) {
                scratch.Set(contributor.PermutationKeys[i], contributor.ValueOf(system, id, i));
            }
        }
    }

    /// <summary>One object's contributed permutations, packed into a bit per key.</summary>
    /// <remarks>
    ///     A mask rather than a list, because it is a dictionary key looked up once per visible
    ///     object per frame and a list would allocate for every one of them. Thirty-two contributed
    ///     flags is the ceiling — far past the handful that will ever exist, and past it two variants
    ///     that differ only in a high flag would share a cache entry.
    /// </remarks>
    uint FlagsOf(RenderSystem system, RenderObjectId id) {
        var flags = 0u;
        var bit = 0;

        foreach (var contributor in contributors) {
            for (var i = 0; i < contributor.PermutationKeys.Count && bit < 32; i++, bit++) {
                if (contributor.ValueOf(system, id, i)) {
                    flags |= 1u << bit;
                }
            }
        }

        return flags;
    }

    static bool IsVisibleAnywhere(RenderSystem system, int index) {
        foreach (var view in system.Views) {
            if (system.Visibility.IsVisible(view.Index, new(index))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Releases the uniform blocks this feature created.</summary>
    /// <remarks>
    ///     One buffer per variant that has constants, so a scene with forty variants holds forty —
    ///     small each and worth returning. Called by <see cref="RenderSystem.Dispose" />, which
    ///     disposes whatever it holds that can be.
    /// </remarks>
    public void Dispose() {
        foreach (var block in blocks) {
            block?.Dispose();
        }

        blocks.Clear();
    }

    IReadOnlyList<ParameterKey> KeysFor(string shaderName) =>
        PermutationKeys.TryGetValue(shaderName, out var keys) ? keys : [];

    /// <summary>What one (material, flags, shader) resolved to.</summary>
    /// <param name="Effect">The variant, null when nothing could supply it.</param>
    /// <param name="Group">Its sort group, so equal pipelines sort together.</param>
    /// <param name="Key">
    ///     What it was resolved from, kept so a placeholder can be asked again. Building it costs a
    ///     sort and a hash and would otherwise be done twice for every variant still compiling.
    /// </param>
    /// <param name="Material">
    ///     Which material's values fill it. A variant is a (material, flags, shader) triple, so this
    ///     is the one of the three the descriptor set is written from.
    /// </param>
    /// <param name="Set">
    ///     The per-material descriptor set, when this feature wrote one. Per variant rather than per
    ///     material because a permutation can fold a texture out of the shader entirely, and a set
    ///     written for the variant that has it does not fit the layout of the variant that does not.
    /// </param>
    readonly record struct Variant(
        Effect? Effect,
        uint Group,
        EffectKey Key = default,
        int Material = 0,
        DescriptorSetHandle Set = default
    );
}
