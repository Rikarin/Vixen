// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Shaders;

namespace Vixen.Rendering.Materials;

/// <summary>What compiling a material produced.</summary>
/// <param name="Material">The material, or null when it was rejected.</param>
/// <param name="Diagnostics">What the compiler has to say, errors and warnings alike.</param>
public readonly record struct MaterialCompilation(
    Material? Material,
    ImmutableArray<MaterialDiagnostic> Diagnostics
) {
    /// <summary>Whether the material was rejected.</summary>
    public bool Failed => Material is null;

    /// <summary>The errors only, for a caller that shows warnings elsewhere.</summary>
    public IEnumerable<MaterialDiagnostic> Errors => Diagnostics.Where(diagnostic => diagnostic.IsError);
}

/// <summary>
///     Turns an authored <see cref="MaterialDescriptor" /> into the <see cref="Material" /> a render
///     feature draws with.
/// </summary>
/// <remarks>
///     <para>
///         The whole of what a material system does, in one function: a list of features becomes a
///         <see cref="ShaderComposition" /> that selects the shaders implementing them, and their
///         values become a <see cref="ParameterCollection" /> keyed by the names those shaders will
///         have once composed. From there it is the existing machinery — the composition goes into
///         the effect key, the key resolves to an effect, the effect's layout writes the parameters.
///     </para>
///     <para>
///         <strong>No compiler runs here, and that is the design.</strong> The names are predicted
///         from Raven's qualification rule rather than read out of a compiled shader, so a material
///         can be authored and serialised on a machine that has never compiled one — and a shipping
///         build, which must not link the compiler at all, can still build the key that finds the
///         baked effect. What keeps the prediction honest is the checked-in reflection: see
///         <c>MaterialReflectionTests</c>, which holds this against what Raven actually emits.
///     </para>
///     <para>
///         <strong>Every slot the library declares is bound, whether the material uses it or not.</strong>
///         Raven rejects a compilation with an unfilled slot wherever it is declared (<c>RVN2073</c>),
///         which is right — a slot with no implementation is a shader that cannot be emitted — and it
///         means a complete composition is one that answers for the whole library rather than only
///         for the shaders this material reaches. The unused ones take <c>IdentitySurface</c>, whose
///         contribution is nothing.
///     </para>
/// </remarks>
public static class MaterialCompiler {
    /// <summary>The chain's slots, in the order <c>CompositeSurface</c> calls them.</summary>
    /// <remarks>
    ///     The list is the shader's, and the ceiling on how many features a material can have. Eight
    ///     because that is a base workflow plus every optional feature the library has, and because a
    ///     ninth would be a slot every material pays a binding for.
    /// </remarks>
    internal static readonly string[] ChainSlots = [
        "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth"
    ];

    /// <summary>Slots the library declares that a material does not necessarily fill.</summary>
    /// <remarks>
    ///     Written down here rather than discovered, for the reason
    ///     <see cref="Features.MaterialRenderFeature.PermutationKeys" /> gives about permutation
    ///     keys: it is a property of the shipped shaders, and this cannot compile one to ask. A slot
    ///     added to the library and not added here shows up as <c>RVN2073</c> the first time
    ///     something compiles a material, which is a loud failure rather than a quiet one.
    /// </remarks>
    internal static readonly (string Shader, string Slot, string Filler)[] OptionalSlots = [
        ("BlendSurface", "under", IdentityShader),
        ("BlendSurface", "over", IdentityShader),

        // The traced pass's field. Its filler is not the identity surface — a slot is typed, and this
        // one wants an IDistanceFieldSource rather than an IMaterialSurface. A material compiled
        // beside a pass that can trace has to name something for it even though it never reaches it,
        // and what it names answers "nothing is near".
        ("DistanceFieldAo", "distanceField", EmptyFieldShader),

        // And the indirect pass's field, for the same reason and with the same shape. Its filler
        // answers "no indirect light and nothing shadowing the sun", which are two different right
        // answers rather than one convenient zero.
        ("IndirectDiffuse", "irradiance", EmptyIrradianceShader),

        // And the forward pass's, which is the one with reach: every material in every project is
        // compiled against ForwardPlus, so this is the entry that decides whether the whole material
        // tree compiles at all. It is also why the slot's filler is a project's decision rather than a
        // material's — a material has nothing to say about whether the scene has a field.
        ("ForwardPlus", "irradiance", EmptyIrradianceShader),

        // And the fill shader's, which traces the same clipmap the traced pass does. A material never
        // reaches a compute shader, and that is exactly why this is here: a slot has to be bound
        // wherever it is *declared*, not wherever it is used.
        ("IrradianceFill", "distanceField", EmptyFieldShader)
    ];

    /// <summary>The typed slots a pass has to name whether or not it reaches them, and their fillers.</summary>
    /// <remarks>
    ///     <para>
    ///         Bare slot names rather than qualified ones, because a pass is compiled against a source
    ///         set rather than against a material: a bare binding fills the slot wherever it is
    ///         declared, which is what a compilation holding two shaders that each declare one needs.
    ///     </para>
    ///     <para>
    ///         The material path uses <see cref="OptionalSlots" /> for the same job and cannot share
    ///         this list, because there the qualification is what keeps one shader's slot from
    ///         accidentally filling another's. The two answer different questions about the same
    ///         fillers, and <c>ComposeSlotInventoryTests</c> holds them against each other.
    ///     </para>
    /// </remarks>
    internal static readonly (string Slot, string Filler)[] PassSlots = [
        ("distanceField", EmptyFieldShader),
        ("irradiance", EmptyIrradianceShader)
    ];

    /// <summary>A pass's composition, with every typed slot it did not name filled by its default.</summary>
    /// <param name="slot">The slot this pass actually cares about.</param>
    /// <param name="filler">What to put behind it.</param>
    /// <returns>The composition.</returns>
    /// <exception cref="ArgumentException">The slot or the filler is empty.</exception>
    /// <remarks>
    ///     <b>Every slot a compilation's sources declare has to be bound, whether or not the shader
    ///     being compiled reaches it.</b> A post pass composing one of them therefore cannot compile
    ///     beside a shader declaring another unless it names a filler for that one too — which is the
    ///     same job <see cref="OptionalSlots" /> does for a material, and the same failure when it is
    ///     not done: the compiler refuses the variant, the effect system records a miss, and the node
    ///     draws nothing while looking exactly like a pass nobody scheduled.
    /// </remarks>
    public static ShaderComposition PassComposition(string slot, string filler) {
        ArgumentException.ThrowIfNullOrEmpty(slot);
        ArgumentException.ThrowIfNullOrEmpty(filler);

        Dictionary<string, string> bindings = Defaults();

        bindings[slot] = filler;

        return ShaderComposition.Of(bindings);
    }

    /// <summary>Every typed slot filled by its default, for a pass that composes none of them.</summary>
    /// <returns>The composition.</returns>
    /// <remarks>
    ///     <b>A shader that composes nothing still needs one, and that is the part that surprises.</b>
    ///     The rule is about the <i>compilation</i>, not the shader: every slot the sources declare has
    ///     to be bound. So a compute pass sharing a package with a shader that declares
    ///     <c>distanceField</c> — <c>IrradianceRepair</c> beside <c>IrradianceFill</c>, exactly — is
    ///     refused unless it names a filler for a slot it has never heard of. This is what it names.
    /// </remarks>
    public static ShaderComposition PassComposition() => ShaderComposition.Of(Defaults());

    /// <summary>Every typed slot mapped to the shader that fills it when nothing else does.</summary>
    static Dictionary<string, string> Defaults() {
        Dictionary<string, string> bindings = new(StringComparer.Ordinal);

        foreach (var (name, fallback) in PassSlots) {
            bindings[name] = fallback;
        }

        return bindings;
    }

    /// <summary>The shader that fills a slot nothing else does.</summary>
    public const string IdentityShader = "IdentitySurface";

    /// <summary>The shader that fills a distance-field slot for a project that traces nothing.</summary>
    public const string EmptyFieldShader = "NoDistanceField";

    /// <summary>The shader that fills an irradiance slot for a project with no field.</summary>
    public const string EmptyIrradianceShader = "NoIrradiance";

    /// <summary>The shader that fills an irradiance slot by reading doc 19 § L2's field.</summary>
    /// <remarks>
    ///     <b>It is also half of a binding name, which is why it is a constant.</b> A composed slot's
    ///     bindings are named for the shader filling it — <c>ForwardPlus.IrradianceFieldProbes.irradianceL0</c>
    ///     — so the host that composes it and the host that binds its volumes have to spell it the same
    ///     way. Two spellings resolve to nothing, silently, and a frame lit by nothing looks like a
    ///     field that found no light.
    /// </remarks>
    public const string IrradianceFieldShader = "IrradianceFieldProbes";

    /// <summary>The chain a material's features are composed through.</summary>
    public const string ChainShader = "CompositeSurface";

    /// <summary>The forward pass's irradiance slot, qualified — what a project overrides to turn GI on.</summary>
    /// <remarks>
    ///     Named because it is the one entry in <see cref="OptionalSlots" /> a project genuinely varies.
    ///     The others are answers no material has an opinion about — a material cannot know whether the
    ///     scene has a distance field, and never reaches the pass that would use one. This one it does
    ///     reach, so whether the ambient term comes from a field is a decision somebody makes, and this
    ///     is the key they make it under.
    /// </remarks>
    public const string ForwardIrradianceSlot = "ForwardPlus.irradiance";

    /// <summary>Compiles a descriptor, or reports why it cannot be.</summary>
    /// <param name="descriptor">The material.</param>
    /// <param name="slots">
    ///     Slot fillers the project decides rather than the material, by their qualified names —
    ///     <see cref="ForwardIrradianceSlot" /> is the one that exists. Null takes every default.
    /// </param>
    /// <remarks>
    ///     <b>A parameter rather than a field on the descriptor, because it is not a property of the
    ///     material.</b> Whether the scene has an irradiance field is true of every material in it at
    ///     once; putting it on each one invites two materials to disagree, and two compositions is two
    ///     effects where the project meant one.
    /// </remarks>
    public static MaterialCompilation Compile(
        MaterialDescriptor descriptor,
        IReadOnlyDictionary<string, string>? slots = null
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);

        Dictionary<string, string> composition = new(StringComparer.Ordinal);
        List<MaterialDiagnostic> diagnostics = [];
        HashSet<string> composed = new(StringComparer.Ordinal);

        var parameters = new ParameterCollection();
        var context = new MaterialCompilationContext(
            descriptor.ShaderName,
            composition,
            parameters,
            diagnostics,
            composed
        );

        if (descriptor.Features.Count > ChainSlots.Length) {
            diagnostics.Add(
                new(
                    MaterialDiagnosticId.TooManyFeatures,
                    $"A material has at most {ChainSlots.Length} features, and this one has "
                    + $"{descriptor.Features.Count}.",
                    IsError: true
                )
            );
        }

        if (descriptor.Features.Count == 0) {
            diagnostics.Add(
                new(
                    MaterialDiagnosticId.NoFeatures,
                    "This material has no features, so it is a white dielectric. That is a valid "
                    + "material and rarely an intended one.",
                    IsError: false
                )
            );
        }

        // Always through the chain, even for one feature. The alternative — binding a lone feature
        // straight into `surface` — would name its parameters one way for a material with one
        // feature and another for the same material with two, so adding a normal map would rename
        // `baseColor`. One rule, one set of names, and an unused slot costs an empty call.
        context.Bind("surface", ChainShader);
        context.Compose("shading", descriptor.Shading);

        for (var i = 0; i < descriptor.Features.Count && i < ChainSlots.Length; i++) {
            if (descriptor.Features[i] is { } feature) {
                Chain(context, ChainSlots[i], feature);
            }
        }

        // The project's answers before the defaults, because `Fill` only writes a slot nothing has
        // claimed — which is what makes an override an override rather than a race.
        if (slots is not null) {
            foreach (var (slot, filler) in slots) {
                composition[slot] = filler;
            }
        }

        Fill(context, composition);

        var errors = diagnostics.Count(diagnostic => diagnostic.IsError);

        if (errors > 0) {
            return new(null, [.. diagnostics]);
        }

        var material = new Material(descriptor.ShaderName) {
            Composition = ShaderComposition.Of(composition)
        };

        material.Parameters.Apply(parameters);
        return new(material, [.. diagnostics]);
    }

    /// <summary>Composes one feature into the chain, under the chain's own name.</summary>
    /// <remarks>
    ///     The chain is a shader like any other, so its slots are qualified by it —
    ///     <c>CompositeSurface.first</c> — and the parameters of what fills them are prefixed the same
    ///     way. Nothing here knows the chain's name is special, because it is not.
    /// </remarks>
    static void Chain(MaterialCompilationContext context, string slot, IMaterialFeature feature) {
        // Entered as though the chain were composing, so the binding is qualified and the prefix
        // carries the chain — which is exactly what Raven does when it resolves the slot.
        context.EnterChain(ChainShader);

        try {
            context.Compose(slot, feature);
        } finally {
            context.LeaveChain();
        }
    }

    /// <summary>Fills every slot the library declares and this material left empty.</summary>
    static void Fill(MaterialCompilationContext context, Dictionary<string, string> composition) {
        foreach (var slot in ChainSlots) {
            var qualified = $"{ChainShader}.{slot}";

            if (!composition.ContainsKey(qualified)) {
                composition[qualified] = IdentityShader;
            }
        }

        foreach (var (shader, slot, filler) in OptionalSlots) {
            var qualified = $"{shader}.{slot}";

            if (!composition.ContainsKey(qualified)) {
                composition[qualified] = filler;
            }
        }
    }
}
