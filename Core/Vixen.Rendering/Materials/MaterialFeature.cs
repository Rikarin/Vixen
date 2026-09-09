// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Shaders;

namespace Vixen.Rendering.Materials;

/// <summary>
///     One choice in a material's feature tree — a base workflow, a normal map, a clear coat.
/// </summary>
/// <remarks>
///     <para>
///         Stride's composable model, resolved through Raven's <c>compose</c> rather than through a
///         mixin system: a feature names the shader that implements it, and
///         <see cref="MaterialCompiler" /> turns a list of them into the bindings that select those
///         shaders and the parameters that feed them. Adding a feature is a shader in
///         <c>Raven/Library/Material</c> and an implementation of this — no registration table, and
///         nothing in the pass changes.
///     </para>
///     <para>
///         An interface with a <c>[DataContract]</c> name per implementation, which is how the rest
///         of the engine does polymorphism in a file: the contract name is the YAML tag, so
///         <c>!MetalRoughness</c> selects the type.
///     </para>
///     <para>
///         <strong>A feature is values, not resources.</strong> Every parameter here is written into
///         the material's constant buffer; a feature that samples a texture needs a descriptor, and
///         which binding index it lands on is the compiled shader's decision — the same authoring gap
///         the compositor's nodes have. Until it closes, a textured material sets the values and a
///         host builds the descriptor set.
///     </para>
/// </remarks>
public interface IMaterialFeature {
    /// <summary>The Raven shader implementing this feature, from <c>Raven/Library/Material</c>.</summary>
    /// <remarks>
    ///     The identity the composition is written in, and the reason a feature cannot appear twice
    ///     in one material: a composed shader's parameters belong to its type, so two slots filled
    ///     with the same shader share one set of values.
    /// </remarks>
    string ShaderName { get; }

    /// <summary>Where in the chain this feature has to run for the ones after it to be right.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="MaterialFeatureStage.Surface" /> for every feature but one, which is why it
    ///         has a default: a feature that reads the coordinate and writes a channel is correct
    ///         wherever the author put it, and the whole chain is written that way on purpose.
    ///         ⚠ <b>The exception is <c>ParallaxOcclusionFeature</c></b>, the first
    ///         <see cref="MaterialFeatureStage.Coordinate" /> feature the engine has — so this
    ///         paragraph read "every feature that exists today" until one did not.
    ///     </para>
    ///     <para>
    ///         A property rather than a marker interface because a feature's shader can be
    ///         <em>data</em> — <see cref="GraphSurfaceFeature" /> takes its name from a graph — so a
    ///         graph that emits a coordinate transform has to be able to say so without being a
    ///         different C# type.
    ///     </para>
    /// </remarks>
    MaterialFeatureStage Stage => MaterialFeatureStage.Surface;

    /// <summary>Whether this feature <em>is</em> the surface, rather than contributing to one.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A base workflow decides what the surface is made of</b> — it assigns
    ///         <c>diffuseColor</c>, <c>f0</c> and <c>perceptualRoughness</c> rather than adjusting
    ///         them — so a chain may hold exactly one. Two of them compose, resolve and compile
    ///         clean, and at run time the later slot writes over the earlier one's albedo:
    ///         <see cref="MaterialDiagnosticId.TwoBaseSurfaces" /> is what says so, and
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1123">#1123</a> is where it was found.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Declared by the features rather than listed in the compiler, because a list is
    ///         the second copy a new workflow silently is not on.</b> A feature added to
    ///         <c>MaterialFeatures.cs</c> that assigns the surface says so here, next to the
    ///         assignment; a rule that read a type list in <see cref="MaterialCompiler" /> would
    ///         admit that feature beside every other base surface with nothing reported — which is
    ///         the defect this rule exists to catch, arriving through the rule itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>BlendFeature</c> is deliberately not one.</b> Its two sides are separate
    ///         surfaces — <c>BlendSurface</c> hands each a copy of the data and mixes the results —
    ///         so the base surfaces nested inside it are not in this chain at all, which is
    ///         <c>MaterialCompiler.Ordered</c>'s reason for being flat as well.
    ///     </para>
    /// </remarks>
    bool IsBaseSurface => false;

    /// <summary>Writes this feature's parameters, and fills any slots of its own.</summary>
    void Compile(MaterialCompilationContext context);
}

/// <summary>
///     When a feature has to run, relative to the features that read what it writes.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The chain has exactly one ordering rule and this is it.</strong> Every feature in
///         the library but one reads <c>d.uv</c> and writes a channel, so the order those run in is
///         the author's business and nothing here has an opinion — <c>CompositeSurface</c> calls its eight
///         slots in the order <see cref="MaterialCompiler" /> filled them, which is the order the
///         material's <see cref="MaterialDescriptor.Features" /> list happens to be in.
///     </para>
///     <para>
///         ⚠ <b>A feature that writes the coordinate inverts that, and the failure is a plausible
///         frame.</b> Parallax occlusion reads <c>d.uv</c> and writes it back displaced; placed third,
///         it displaces the coordinate the two features before it already sampled at, so half the
///         material is parallaxed and half is not — no device reports anything, and the surface merely
///         looks wrong in a way that gets blamed on the map. See
///         <a href="https://github.com/Rikarin/Vixen/issues/1065">#1065</a>, which is the feature this
///         was written ahead of.
///     </para>
///     <para>
///         <b>Written before the first feature that needs it, deliberately.</b> A rule added after the
///         feature is a rule the feature was already shipped without.
///     </para>
/// </remarks>
public enum MaterialFeatureStage {
    /// <summary>Rewrites the coordinate the rest of the chain samples at, so it runs before them.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a hint.</b> <see cref="MaterialCompiler" /> refuses a material that puts one of
    ///     these behind a <see cref="Surface" /> feature rather than quietly moving it, because a slot
    ///     the compiler reordered is a material whose file no longer says what runs.
    /// </remarks>
    Coordinate,

    /// <summary>Contributes to the surface at the coordinate it was handed. Every feature today.</summary>
    Surface
}

/// <summary>
///     What a material does with the light that reaches it: the second slot on a shading pass.
/// </summary>
/// <remarks>
///     Separate from <see cref="IMaterialFeature" /> because the two vary independently — a clear
///     coat over a metal-roughness base and over a specular-glossiness one is the same lobe — and
///     because a shading model is the one thing a surface feature cannot express: no value written
///     into <c>MaterialData</c> makes the standard BRDF evaluate a second lobe.
/// </remarks>
public interface IMaterialShading {
    /// <summary>The Raven shader implementing the model, from <c>Raven/Library/Material</c>.</summary>
    string ShaderName { get; }

    /// <summary>Writes the model's own parameters — a wrap width, a cel ramp's step count.</summary>
    void Compile(MaterialCompilationContext context);
}

/// <summary>What went wrong, or nearly went wrong, while compiling a material.</summary>
public enum MaterialDiagnosticId {
    /// <summary>Two slots want the same feature, which would make them share its parameters.</summary>
    DuplicateFeature,

    /// <summary>The material has more features than the chain has slots.</summary>
    TooManyFeatures,

    /// <summary>The material has no features, so it is the default surface.</summary>
    NoFeatures,

    /// <summary>A feature names no shader at all, so there is nothing to compose into the slot.</summary>
    /// <remarks>
    ///     ⚠ <b>Reachable only from a feature whose shader name is data</b>, which today is
    ///     <see cref="GraphSurfaceFeature" /> alone — every hand-written feature returns a constant a
    ///     compiler checked. Without this the empty name is bound into the composition, and what
    ///     comes back is Raven complaining about a shader called nothing, about a material the
    ///     message cannot name. The zeroed field whose zero looks valid, once more.
    /// </remarks>
    UnnamedShader,

    /// <summary>A layered material has more layers than its splat map has painted channels.</summary>
    /// <remarks>
    ///     ⚠ <b>A warning rather than an error, and it stands in for a picture nobody could have
    ///     read.</b> The layer is simply unpainted, which is a stack short of a layer — where the
    ///     alternative, reading a channel the map does not have, is a fourth layer weighted 1 at every
    ///     texel and therefore the whole surface after normalisation. See
    ///     <see cref="TexturedMaterialLayersFeature.PaintedChannels" />.
    /// </remarks>
    UnpaintedLayer,

    /// <summary>A feature that rewrites the coordinate is behind one that samples at it.</summary>
    /// <remarks>
    ///     ⚠ <b>An error, and the only ordering the compiler has an opinion about.</b> Every other
    ///     order is the author's — a feature reads the surface as the previous one left it, and which
    ///     contribution wins is what the list is for. A <see cref="MaterialFeatureStage.Coordinate" />
    ///     feature is the one shape that cannot be expressed that way: the features before it have
    ///     already sampled, so what it produces is half a parallaxed surface, on every device, with
    ///     nothing reported. See <see cref="MaterialFeatureStage" />.
    /// </remarks>
    CoordinateFeatureOutOfOrder,

    /// <summary>A feature's map is called something other than the one name a host pairs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An error, and the only one here whose subject is a string an author is free to
    ///         type.</b> A host joins a sampling shader's <c>uint</c> slot to a material-side texture
    ///         name, and it keys that join off the feature's <em>default</em> — one static entry per
    ///         name, built from <c>new TexturedMetalRoughnessFeature()</c> and its siblings, because
    ///         the pairing is one table for the whole frame and cannot be per material. So a material
    ///         that spells its map anything else resolves no entry, leaves the index at zero, and
    ///         samples slot zero: the fallback checker, on every device, with nothing reported. See
    ///         <a href="https://github.com/Rikarin/Vixen/issues/371">#371</a>, which asked for this at
    ///         import, where the author is at a keyboard.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reached by a rename that looks like a fix.</b> The two maps called "height" are
    ///         the case that already happened: a re-bake that copied its own texture entry's name onto
    ///         a preserved <c>ParallaxOcclusionFeature</c> bound a real texture under
    ///         <c>heightMap</c> — which is the <em>layered</em> feature's name — and marched the
    ///         checker. Eight map-name remarks in <c>MaterialFeatures</c> say the name is not the
    ///         author's; until this, nothing enforced any of them.
    ///     </para>
    ///     <para>
    ///         <b>A name whose default is empty is exempt</b>, because there is nothing to be renamed
    ///         away from: that is <see cref="GraphSurfaceFeature" />, whose slot names are its graph's
    ///         and whose pairing entries are added per material rather than statically.
    ///     </para>
    /// </remarks>
    RenamedTextureMap,

    /// <summary>Two base workflows are in one chain, so the later one writes over the earlier's albedo.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An error, and the picture it refuses is a fully lit, plausible surface of the
    ///         wrong material.</b> Every base workflow is
    ///         <see cref="MaterialFeatureStage.Surface" /> and each <em>assigns</em> the same fields
    ///         of <c>MaterialData</c>, so two of them compose into two
    ///         <c>CompositeSurface</c> slots, the composition resolves, the material compiles, and at
    ///         run time the second simply overwrites the first. Nothing in the file, the compile or
    ///         the frame said which of the two won.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reachable only by <em>assembling</em> a material</b> — a bake, an inspector, a
    ///         migration, a hand-edit — which is why it is worth a rule:
    ///         <c>MaterialBake.Material</c> was the first assembler to have the question put to it,
    ///         and the answer had to be argued from the shader source rather than read off a
    ///         diagnostic. See <a href="https://github.com/Rikarin/Vixen/issues/1123">#1123</a>, and
    ///         <see cref="IMaterialFeature.IsBaseSurface" /> for why the predicate belongs to the
    ///         features.
    ///     </para>
    /// </remarks>
    TwoBaseSurfaces
}

/// <summary>One thing the compiler has to say about a material.</summary>
/// <param name="Id">Which problem.</param>
/// <param name="Message">What to tell whoever authored the material.</param>
/// <param name="IsError">Whether the material was rejected rather than merely questioned.</param>
public readonly record struct MaterialDiagnostic(MaterialDiagnosticId Id, string Message, bool IsError) {
    /// <inheritdoc />
    public override string ToString() => $"{(IsError ? "error" : "warning")} {Id}: {Message}";
}

/// <summary>
///     What a feature writes into while a material is being compiled.
/// </summary>
/// <remarks>
///     <para>
///         It carries the two things a feature cannot know about itself: where in the composition it
///         ended up, and what has already been composed. The first decides what its parameters are
///         called — Raven qualifies a composed shader's parameters by the path of types they were
///         reached through, so a <c>baseColor</c> inside the chain is
///         <c>CompositeSurface.MetalRoughnessSurface.baseColor</c> — and the second is what makes the
///         duplicate check possible.
///     </para>
///     <para>
///         <strong>The naming rule is Raven's, implemented here without a compiler in the process.</strong>
///         That is a duplicated rule and therefore a place where two things can drift apart, which is
///         why the checked-in reflection for <c>ForwardPlus</c> exists and why
///         <c>MaterialReflectionTests</c> compares this against it. Predicting the names is what lets
///         a material be authored, edited and serialised on a machine with no shader compiler.
///     </para>
/// </remarks>
public sealed class MaterialCompilationContext {
    readonly Dictionary<string, string> composition;
    readonly ParameterCollection parameters;
    readonly List<MaterialDiagnostic> diagnostics;
    readonly HashSet<string> composed;
    readonly Stack<(string? Containing, string Prefix)> outer = new();

    /// <summary>The shader whose slot is being filled, or null at the pass.</summary>
    /// <remarks>
    ///     What a binding is qualified by. Null at the top, so a pass's own slots are bound bare —
    ///     which is what lets one composition serve the forward pass and the G-buffer pass, both of
    ///     which declare <c>surface</c>.
    /// </remarks>
    string? containing;

    /// <summary>What a parameter set here is prefixed with.</summary>
    string prefix = string.Empty;

    internal MaterialCompilationContext(
        string shaderName,
        Dictionary<string, string> composition,
        ParameterCollection parameters,
        List<MaterialDiagnostic> diagnostics,
        HashSet<string> composed,
        bool strict = true
    ) {
        ShaderName = shaderName;
        Strict = strict;
        this.composition = composition;
        this.parameters = parameters;
        this.diagnostics = diagnostics;
        this.composed = composed;

        // Every key a shader owns is qualified by it — `ForwardPlus.baseColor`, as the generated
        // bindings spell it — so a composed feature's parameter is the pass, then the path of types
        // it was reached through, then its own name. Two shaders with a `strength` stay distinct, and
        // a material's collection can be read by the same key the generator emits.
        prefix = shaderName + ".";
    }

    /// <summary>The shading pass the material is being compiled for.</summary>
    public string ShaderName { get; }

    /// <summary>Whether a defect in the authored content refuses the material or only warns.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same compiler runs at import and at load, and those two want different
    ///         answers to "this material is wrong".</b> At import there is an author to tell, the
    ///         content has not shipped yet, and a refusal is the whole point. At load the material is
    ///         already in a bundle somebody built, and a compiler that started refusing what it used
    ///         to accept takes the mesh off screen — for content the importer will not re-check,
    ///         because a compiler change moves no importer version.
    ///     </para>
    ///     <para>
    ///         So a rule about the <em>author's</em> spelling reports under this, and a rule about
    ///         something the runtime genuinely cannot do stays an error either way. A warning at load
    ///         leaves the picture exactly as it was — which for a renamed map is the fallback checker,
    ///         wrong and recognisable — rather than replacing it with nothing.
    ///     </para>
    /// </remarks>
    public bool Strict { get; }

    /// <summary>Sets one of the current feature's parameters.</summary>
    /// <param name="name">The name the shader declares, unqualified.</param>
    /// <param name="value">Its value.</param>
    public void Set<T>(string name, T value) where T : unmanaged {
        ArgumentException.ThrowIfNullOrEmpty(name);
        parameters.Set(ParameterKeys.New<T>(prefix + name), value);
    }

    /// <summary>Sets a permutation the current feature declares.</summary>
    /// <remarks>
    ///     <para>
    ///         Qualified by the pass and <em>not</em> by the composition path, because those are two
    ///         different naming systems meeting. Raven resolves a <c>[Permutation]</c> by name across
    ///         the whole compilation, the way a <c>--define</c> does, so a feature's permutation is
    ///         the compilation's; the engine's keys are qualified per shader because that is what the
    ///         generator emits from a shader's reflection. The pass is the one name both agree on.
    ///     </para>
    ///     <para>
    ///         Setting a value is not the same as it reaching the effect key: that is the host's, via
    ///         <see cref="Features.MaterialRenderFeature.PermutationKeys" />, and
    ///         <see cref="MaterialKeys" /> is where a host gets the key to register.
    ///     </para>
    /// </remarks>
    /// <param name="name">The name the shader declares, unqualified.</param>
    /// <param name="defaultValue">The default the shader declares, so the interned key carries it.</param>
    /// <param name="value">Its value.</param>
    public void SetPermutation<T>(string name, T defaultValue, T value) where T : notnull {
        ArgumentException.ThrowIfNullOrEmpty(name);
        parameters.Set(ParameterKeys.NewPermutation(defaultValue, $"{ShaderName}.{name}"), value);
    }

    /// <summary>Fills one of the current shader's <c>compose</c> slots with a feature.</summary>
    /// <remarks>
    ///     Recurses, so a feature that composes others — a blend of two layers — contributes their
    ///     parameters under its own path without knowing where it is itself.
    /// </remarks>
    public void Compose(string slot, IMaterialFeature feature) {
        ArgumentException.ThrowIfNullOrEmpty(slot);
        ArgumentNullException.ThrowIfNull(feature);

        if (feature.ShaderName.Length == 0) {
            // ⚠ Before the duplicate check, because two unnamed features would otherwise be reported
            // as one shader filling two slots — a true sentence about a fault that is not the one
            // the author has. Reachable only from a feature whose name is data; see
            // `MaterialDiagnosticId.UnnamedShader`.
            Report(
                MaterialDiagnosticId.UnnamedShader,
                $"A feature in slot '{slot}' names no shader. A graph-authored surface takes its name "
                + "from the graph it was compiled from, so this is a material pointing at a graph that "
                + "was never compiled, or one saved before it had a name."
            );

            return;
        }

        if (!composed.Add(feature.ShaderName)) {
            // Not a warning. Two slots bound to one shader compile — into a material where both read
            // the same parameters, which is a wrong image rather than a failure, and the kind that
            // gets blamed on the artist. `MaterialLayersSurface` is the answer for the case that
            // wants two of something.
            Report(
                MaterialDiagnosticId.DuplicateFeature,
                $"'{feature.ShaderName}' fills more than one slot in this material. A composed "
                + "shader's parameters belong to its type, so both slots would read the same values. "
                + "Use a layered material for repeated features."
            );

            return;
        }

        composition[Qualify(slot)] = feature.ShaderName;

        var outerContaining = containing;
        var outerPrefix = prefix;

        containing = feature.ShaderName;
        prefix = outerPrefix + feature.ShaderName + ".";

        try {
            feature.Compile(this);
        } finally {
            containing = outerContaining;
            prefix = outerPrefix;
        }
    }

    /// <summary>Fills a slot with a shader named directly, for what the compiler itself binds.</summary>
    internal void Bind(string slot, string shaderName) => composition[Qualify(slot)] = shaderName;

    /// <summary>Descends into a shader the compiler bound itself, rather than a feature.</summary>
    /// <remarks>
    ///     The chain is bound by <see cref="MaterialCompiler" /> rather than composed by a feature,
    ///     and everything below it still has to be named as though it had been — its slots qualified
    ///     by it, its features' parameters prefixed with it. This is that, and it is internal because
    ///     a feature has no business moving the path it was given.
    /// </remarks>
    internal void EnterChain(string shaderName) {
        outer.Push((containing, prefix));
        containing = shaderName;
        prefix += shaderName + ".";
    }

    /// <summary>Returns to where <see cref="EnterChain" /> was called from.</summary>
    internal void LeaveChain() => (containing, prefix) = outer.Pop();

    /// <summary>Runs <paramref name="shading" /> under its own name, as a slot on the pass.</summary>
    internal void Compose(string slot, IMaterialShading shading) {
        composition[Qualify(slot)] = shading.ShaderName;

        var outerContaining = containing;
        var outerPrefix = prefix;

        containing = shading.ShaderName;
        prefix = outerPrefix + shading.ShaderName + ".";

        try {
            shading.Compile(this);
        } finally {
            containing = outerContaining;
            prefix = outerPrefix;
        }
    }

    /// <summary>Records something the author should know.</summary>
    public void Report(MaterialDiagnosticId id, string message, bool isError = true) =>
        diagnostics.Add(new(id, message, isError));

    string Qualify(string slot) => containing is null ? slot : $"{containing}.{slot}";
}
