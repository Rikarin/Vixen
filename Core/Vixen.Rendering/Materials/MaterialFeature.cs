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

    /// <summary>Writes this feature's parameters, and fills any slots of its own.</summary>
    void Compile(MaterialCompilationContext context);
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
    UnnamedShader
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
        HashSet<string> composed
    ) {
        ShaderName = shaderName;
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
