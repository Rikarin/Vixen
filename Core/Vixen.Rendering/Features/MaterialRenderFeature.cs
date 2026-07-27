// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Features;

/// <summary>
///     Which material each object uses, and which shader variant that resolves to.
/// </summary>
/// <remarks>
///     <para>
///         This is where the shader half of the engine meets the renderer half. Preparation turns a
///         material's <see cref="ParameterCollection" /> into an <see cref="EffectKey" />, resolves
///         it through the <see cref="EffectSystem" />, and remembers the answer per object — so by
///         the time anything is recorded, "which shader" is an array lookup.
///     </para>
///     <para>
///         <strong>Resolution happens in preparation, not in the draw call, and not in
///         extraction.</strong> Not in the draw call because resolving can compile, and compiling
///         inside a command list is the stall that a frame budget cannot absorb. Not in extraction
///         because the answer is only needed for objects that survived culling, which in a
///         well-culled scene is far fewer — the same reason the phase exists at all.
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
public sealed class MaterialRenderFeature : SubRenderFeature {
    readonly List<Material> materials = [];
    readonly Dictionary<Material, int> indices = [];
    readonly Dictionary<EffectKey, uint> groups = new();
    readonly List<Effect?> resolved = [];

    /// <inheritdoc />
    public override string Name => "Material";

    /// <summary>Each object's index into <see cref="Materials" />, or -1 for none.</summary>
    public RenderDataKey<int> MaterialIndex { get; private set; }

    /// <summary>The materials this feature knows about.</summary>
    public IReadOnlyList<Material> Materials => materials;

    /// <summary>Which permutation keys the shader's variants are selected by, per shader name.</summary>
    /// <remarks>
    ///     <para>
    ///         Supplied rather than discovered, because it is a property of the compiled shader and
    ///         this cannot compile one. The generated <c>…Keys.UsedPermutationKeys</c> is exactly
    ///         this list, so a host registers it once per shader and the key is built from the same
    ///         set the compiler reported.
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

    /// <inheritdoc />
    protected internal override void Initialize(RenderSystem system) {
        MaterialIndex = system.Objects.Data.Register<int>();

        // Zero is a valid index and the arrays start zeroed, so an object nobody assigned a material
        // to would silently claim the first one. Registering a null sentinel at 0 makes the default
        // mean "none" without every caller having to write -1.
        materials.Add(null!);
        resolved.Add(null);
    }

    /// <summary>Registers a material and returns the index objects refer to it by.</summary>
    public int Add(Material material) {
        ArgumentNullException.ThrowIfNull(material);

        if (indices.TryGetValue(material, out var existing)) {
            return existing;
        }

        var index = materials.Count;
        materials.Add(material);
        resolved.Add(null);
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
        if (Effects is null) {
            return;
        }

        // Per material, not per object: a scene of ten thousand objects sharing twenty materials
        // resolves twenty times. The per-object step is the array read in `EffectOf`.
        for (var i = 1; i < materials.Count; i++) {
            var material = materials[i];
            var key = EffectKey.From(material.ShaderName, material.Parameters, KeysFor(material.ShaderName));

            resolved[i] = Effects.Resolve(key);

            if (!groups.ContainsKey(key)) {
                // Dense and assigned in first-seen order. The value means nothing on its own — only
                // that two objects sharing an effect share a group, which is what puts them adjacent
                // in the sorted list.
                groups[key] = (uint)groups.Count;
            }
        }
    }

    /// <summary>The effect an object resolved to, or null when it has none.</summary>
    public Effect? EffectOf(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);

        var index = system.Objects.Data.Data(MaterialIndex)[id.Index];
        return index > 0 && index < resolved.Count ? resolved[index] : null;
    }

    /// <summary>The descriptor set an object's material binds, invalid when it has none.</summary>
    public DescriptorSetHandle DescriptorsOf(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);

        var index = system.Objects.Data.Data(MaterialIndex)[id.Index];
        return index > 0 && index < materials.Count ? materials[index].Descriptors : default;
    }

    /// <summary>The sort group for an object: its effect's, so equal effects sort together.</summary>
    public uint SortGroupOf(RenderSystem system, RenderObjectId id) {
        ArgumentNullException.ThrowIfNull(system);

        var index = system.Objects.Data.Data(MaterialIndex)[id.Index];

        if (index <= 0 || index >= materials.Count) {
            return uint.MaxValue;
        }

        var material = materials[index];
        var key = EffectKey.From(material.ShaderName, material.Parameters, KeysFor(material.ShaderName));

        // Objects whose effect nothing has resolved sort last rather than first. They draw nothing,
        // and putting them at the front would break the run of everything that does.
        return groups.TryGetValue(key, out var group) ? group : uint.MaxValue;
    }

    IReadOnlyList<ParameterKey> KeysFor(string shaderName) =>
        PermutationKeys.TryGetValue(shaderName, out var keys) ? keys : [];
}
