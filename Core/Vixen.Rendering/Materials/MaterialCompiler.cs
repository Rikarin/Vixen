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
    ///     <para>
    ///         Written down here rather than discovered, for the reason
    ///         <see cref="Features.MaterialRenderFeature.PermutationKeys" /> gives about permutation
    ///         keys: it is a property of the shipped shaders, and this cannot compile one to ask.
    ///     </para>
    ///     <para>
    ///         <b>No longer load-bearing, and worth saying because it was.</b> Every one of these slots
    ///         now names the same filler as its own default in the library — <c>compose val irradiance:
    ///         IIrradianceSource = NoIrradiance</c> — so a compilation that says nothing about them gets
    ///         exactly what this names. Before that, a slot added to the library and not added here
    ///         showed up as <c>RVN2073</c> the first time anything compiled a material, which broke
    ///         every material in the project rather than the shader that declared the slot; it happened
    ///         twice.
    ///     </para>
    ///     <para>
    ///         What the list is <em>for</em> now is the other direction: naming a real implementation
    ///         where the library's default is the neutral one. A project with a field binds
    ///         <c>IrradianceFieldProbes</c> here, and that is a project's decision rather than a
    ///         material's — a material has nothing to say about whether the scene has a field.
    ///     </para>
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

        // And the forward pass's *field*, which is how ambient occlusion reaches a shaded pixel at
        // all. It is the same slot, the same type and the same filler as the traced pass's above —
        // one clipmap, marched by whoever needs it — and putting it on ForwardPlus is what lets the
        // occlusion be multiplied into the ambient term where it belongs, rather than into a
        // screen-space buffer that a forward pass has no way to apply after the fact.
        ("ForwardPlus", "distanceField", EmptyFieldShader),

        // And the forward pass's shadow atlas, which is the third slot on the same pass and the one
        // that decides whether a lamp is stopped by a wall. Neutral here for the reason the two above
        // are: a material has nothing to say about whether the frame renders a shadow atlas, and a
        // project that renders one names `PunctualShadowShader` in its own composition.
        ("ForwardPlus", "punctualShadow", EmptyPunctualShadowShader),

        // And the resolve's, which composes the same slot so that a virtualized surface and a
        // classic one are shadowed by the same atlas. Two passes shading one scene under different
        // shadow terms is exactly the divergence `ClusteredShading` was extracted to prevent.
        ("VisibilityResolve", "punctualShadow", EmptyPunctualShadowShader),

        // And the fill shader's, which traces the same clipmap the traced pass does. A material never
        // reaches a compute shader, and that is exactly why this is here: a slot has to be bound
        // wherever it is *declared*, not wherever it is used.
        ("IrradianceFill", "distanceField", EmptyFieldShader),

        // And the screen-probe trace's, the third compute shader to declare the same slot for the
        // same march. Forgetting this line is doc 19 § L2's RVN2073 finding replaying verbatim — and
        // it did replay, once, when the ScreenProbes package landed without it and every whole-library
        // compilation in the golden suite refused.
        ("ScreenProbeTrace", "distanceField", EmptyFieldShader),

        // The trace also terminates long rays in the irradiance field — the same slot name and type
        // the shading passes compose, because it is the same field and a pass composition binds a
        // slot once, wherever it is declared.
        ("ScreenProbeTrace", "irradiance", EmptyIrradianceShader)
    ];

    /// <summary>A pass's composition, with every slot it did not name filled by its default.</summary>
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

    /// <summary>The same composition, for a pass that cares about several slots.</summary>
    /// <param name="overrides">The slots and what to put behind each.</param>
    /// <returns>The composition.</returns>
    /// <exception cref="ArgumentNullException">There are no overrides.</exception>
    /// <exception cref="ArgumentException">A slot or a filler is empty.</exception>
    /// <remarks>
    ///     The screen-probe trace is what forced the plural: it marches a distance field <i>and</i>
    ///     terminates its long rays in the irradiance field, and a pass with two slots and one
    ///     override would quietly leave the other at its default — which for a slot whose default
    ///     answers "nothing" is a feature switched off with no error anywhere.
    /// </remarks>
    public static ShaderComposition PassComposition(params ReadOnlySpan<(string Slot, string Filler)> overrides) {
        Dictionary<string, string> bindings = Defaults();

        foreach (var (slot, filler) in overrides) {
            ArgumentException.ThrowIfNullOrEmpty(slot);
            ArgumentException.ThrowIfNullOrEmpty(filler);

            bindings[slot] = filler;
        }

        return ShaderComposition.Of(bindings);
    }

    /// <summary>Every slot filled by its default, for a pass that composes none of them.</summary>
    /// <returns>The composition.</returns>
    /// <remarks>
    ///     <b>A shader that composes nothing still needs one, and that is the part that surprises.</b>
    ///     The rule is about the <i>compilation</i>, not the shader: every slot the sources declare has
    ///     to be bound. So a compute pass sharing a package with a shader that declares
    ///     <c>distanceField</c> — <c>IrradianceRepair</c> beside <c>IrradianceFill</c>, exactly — is
    ///     refused unless it names a filler for a slot it has never heard of. This is what it names.
    /// </remarks>
    public static ShaderComposition PassComposition() => ShaderComposition.Of(Defaults());

    /// <summary>Every slot the library declares, mapped to the shader that fills it by default.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Every slot, not every <i>typed</i> slot, and the difference is the whole
    ///         configuration a game ships in.</b> This used to name <c>distanceField</c> and
    ///         <c>irradiance</c> alone, on the reasoning that a pass has nothing to say about a
    ///         material's surface — which is true about the shader and false about the compilation.
    ///         <c>RVN2073</c> asks the source set, so a compute shader compiled beside
    ///         <c>ForwardPlus</c> is refused for <c>surface</c>, <c>shading</c> and all ten of
    ///         <c>CompositeSurface</c>'s links, none of which it has heard of.
    ///     </para>
    ///     <para>
    ///         ⚠ That refusal was invisible for as long as it existed, because every test compiling a
    ///         pass narrowed its source set to the packages the pass belongs to. An application has one
    ///         effect system and it serves the whole library, so the narrow set is the configuration
    ///         nothing ships in — and the first frame that asked for a device-filled field against the
    ///         whole library is what found it.
    ///     </para>
    ///     <para>
    ///         <b>Derived from the material path rather than written beside it.</b> The two lists have
    ///         to name the same fillers for the same slots, and two lists that have to agree are two
    ///         lists that drift; a test can only notice afterwards. Reading
    ///         <see cref="OptionalSlots" /> and <see cref="ChainSlots" /> here makes the agreement a
    ///         property of the code, and leaves only the two <c>Fill</c> binds by hand —
    ///         <c>surface</c> and <c>shading</c> — to be named twice.
    ///     </para>
    /// </remarks>
    static Dictionary<string, string> Defaults() {
        Dictionary<string, string> bindings = new(StringComparer.Ordinal) {
            // What a material's own composition binds these to: the chain, and the model the
            // descriptor chose. A pass reaches neither, and has to answer for both anyway.
            ["surface"] = IdentityShader,
            ["shading"] = DefaultShadingShader
        };

        foreach (var slot in ChainSlots) {
            bindings[slot] = IdentityShader;
        }

        // Bare rather than qualified, which is the one place the two paths genuinely differ: a
        // material qualifies a slot by the shader declaring it so that one shader's slot cannot fill
        // another's, and a pass wants exactly the opposite — `distanceField` bound once, wherever it
        // is declared, because it is declared in two shaders the pass does not distinguish.
        foreach (var (_, slot, filler) in OptionalSlots) {
            bindings[slot] = filler;
        }

        return bindings;
    }

    /// <summary>The shading model a compilation gets where nothing chose one.</summary>
    /// <remarks>
    ///     <see cref="MaterialDescriptor.Shading" />'s default, and it has to stay that way: a pass
    ///     binding a different one would compile <c>ForwardPlus</c> a second time under a second key
    ///     for a slot the pass never reaches, which is a pipeline nobody draws with and a stutter
    ///     nobody could attribute.
    /// </remarks>
    public const string DefaultShadingShader = "StandardShading";

    /// <summary>The shader that fills a slot nothing else does.</summary>
    public const string IdentityShader = "IdentitySurface";

    /// <summary>The shader that fills a distance-field slot for a project that traces nothing.</summary>
    public const string EmptyFieldShader = "NoDistanceField";

    /// <summary>The shader that fills an irradiance slot for a project with no field.</summary>
    public const string EmptyIrradianceShader = "NoIrradiance";

    /// <summary>The shader that fills a punctual-shadow slot for a frame with no atlas.</summary>
    /// <remarks>
    ///     Answers "fully lit", which is what every punctual light in this engine was until there was
    ///     an atlas to read — so a project that says nothing gets exactly the frame it had.
    /// </remarks>
    public const string EmptyPunctualShadowShader = "NoPunctualShadows";

    /// <summary>The shader that fills it by sampling <c>PunctualShadowRenderer</c>'s atlas.</summary>
    /// <remarks>
    ///     <b>It is also half of a binding name</b>, on exactly
    ///     <see cref="IrradianceFieldShader" />'s terms: the atlas, its sampler and its tile buffer are
    ///     written into set 0 under <c>ForwardPlus.PunctualShadowAtlas.*</c>, so the host that composes
    ///     the slot and the node that fills the bindings have to spell it the same way.
    /// </remarks>
    public const string PunctualShadowShader = "PunctualShadowAtlas";

    /// <summary>The shader that fills a surface-cache slot for a project with no cache — every hit
    ///     answers black, which is every tracer's answer before doc 19 § L4 existed.</summary>
    public const string EmptySurfaceCacheShader = "NoSurfaceCache";

    /// <summary>The shader that fills a surface-cache slot by sampling § L4's card atlas.</summary>
    public const string SurfaceCacheShader = "SurfaceCacheSource";

    /// <summary>The shader that fills a reflection-miss slot for a project with no far field at
    ///     all — black, the answer of a scene with neither probes nor sky.</summary>
    public const string EmptyReflectionMissShader = "NoReflectionMiss";

    /// <summary>The shader that fills a reflection-miss slot with one colour of sky — what every
    ///     reflection in doc 06 sees beyond the probes today. The probe-backed implementation
    ///     replaces it without the kernel changing, which is the slot's point.</summary>
    public const string SkyReflectionMissShader = "SkyMissSource";

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

    /// <summary>The forward pass's distance-field slot, qualified — what turns ambient occlusion on.</summary>
    /// <remarks>
    ///     <para>
    ///         The second entry a project varies, and for the same reason as
    ///         <see cref="ForwardIrradianceSlot" />: the forward pass is the one a material actually
    ///         reaches, so whether its ambient term is occluded is somebody's decision rather than a
    ///         material's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Filling this is only half of it.</b> The clipmap's volumes, textures and sampler
    ///         are written into set 0 under <c>ForwardPlus.GlobalDistanceField.*</c>, and the node
    ///         that writes them is <c>GlobalDistanceFieldRenderer</c> — whose <c>Passes</c> has to
    ///         name <c>ForwardPlus</c> too. A composition without the bindings is a set that is
    ///         declared and never filled, which is every draw in the pass refused rather than a
    ///         feature that does nothing.
    ///     </para>
    /// </remarks>
    public const string ForwardDistanceFieldSlot = "ForwardPlus.distanceField";

    /// <summary>The forward pass's punctual-shadow slot, qualified — what stops a lamp at a wall.</summary>
    /// <remarks>
    ///     <para>
    ///         The third entry a project varies, and the shortest to describe: filled with
    ///         <see cref="PunctualShadowShader" /> every spot and point light is occluded by whatever
    ///         the frame's atlas holds, and left alone every one of them lights through walls — which
    ///         is what this engine did everywhere until there was an atlas to read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And filling it is only half of it</b>, on exactly
    ///         <see cref="ForwardDistanceFieldSlot" />'s terms. The atlas, its sampler and its tile
    ///         buffer land in set 0 under <c>ForwardPlus.PunctualShadowAtlas.*</c>, and what writes
    ///         them is <c>PunctualShadowRenderer</c> — whose <c>Passes</c> has to name
    ///         <c>ForwardPlus</c>. Composition without bindings is a set written short, which is every
    ///         draw in the pass refused rather than a shadow that does not appear.
    ///     </para>
    /// </remarks>
    public const string ForwardPunctualShadowSlot = "ForwardPlus.punctualShadow";

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
