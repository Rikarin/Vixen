// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Syntax;
using Vixen.Raven.Binding;
using Vixen.Raven.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>Reads modifiers and attributes off declaration syntax.</summary>
internal static class DeclarationFacts {
    /// <summary>Attribute names that mark a method as a pipeline entry point.</summary>
    static readonly Dictionary<string, ShaderStage> StageAttributes = new(StringComparer.Ordinal) {
        ["VertexShader"] = ShaderStage.Vertex,
        ["FragmentShader"] = ShaderStage.Fragment,
        ["GeometryShader"] = ShaderStage.Geometry,
        ["ComputeShader"] = ShaderStage.Compute
    };

    /// <summary>
    ///     Attribute names that place a binding in a descriptor set. One name per set, so the
    ///     convention is spelled rather than numbered — see <see cref="ResourceSet" />.
    /// </summary>
    /// <remarks>
    ///     <c>[Bindless]</c> is the odd one, and named for what it holds rather than for how often it
    ///     changes: the other four say when a frame rebinds them and that one is never rebound.
    /// </remarks>
    static readonly Dictionary<string, ResourceSet> SetAttributes = new(StringComparer.Ordinal) {
        ["PerFrame"] = ResourceSet.PerFrame,
        ["PerView"] = ResourceSet.PerView,
        ["PerMaterial"] = ResourceSet.PerMaterial,
        ["PerDraw"] = ResourceSet.PerDraw,
        ["Bindless"] = ResourceSet.Bindless
    };

    /// <summary>
    ///     Attribute names that carry no set and no stage, read one at a time by the predicates
    ///     below.
    /// </summary>
    /// <remarks>
    ///     Here rather than only at each <c>== "Permutation"</c> site, because the set of names the
    ///     compiler reads is a fact about the language that nothing could previously ask for. An
    ///     unrecognised attribute was dropped in silence — see <c>RVN2138</c> — and the check that
    ///     ended that had to be able to enumerate what "recognised" means.
    /// </remarks>
    static readonly string[] MarkerAttributes = [
        "Permutation", "PushConstant", "Shared", "MaterialIndex", "Format", "Semantic", "DynamicOffset",
        "Interpolation"
    ];

    /// <summary>The word each <see cref="InterpolationMode" /> is written as.</summary>
    /// <remarks>
    ///     The GLSL qualifier rather than a name of our own, because that is the word an author of
    ///     shaders already knows and the one every reference they will reach for uses.
    /// </remarks>
    static readonly Dictionary<string, InterpolationMode> InterpolationModes = new(StringComparer.Ordinal) {
        ["smooth"] = InterpolationMode.Smooth,
        ["flat"] = InterpolationMode.Flat,
        ["noperspective"] = InterpolationMode.NoPerspective,
        ["centroid"] = InterpolationMode.Centroid
    };

    /// <summary>The interpolation words, in declaration order, for a diagnostic.</summary>
    public static string InterpolationModeNamesText { get; } = string.Join(", ", InterpolationModes.Keys);

    /// <summary>Every attribute name Raven reads, in the order the message lists them.</summary>
    public static IReadOnlyList<string> KnownAttributeNames { get; } =
        [.. StageAttributes.Keys, .. SetAttributes.Keys, .. MarkerAttributes];

    /// <summary>The recognised names as one comma-separated string, for a diagnostic.</summary>
    public static string KnownAttributeNamesText { get; } = string.Join(", ", KnownAttributeNames);

    /// <summary>Whether the compiler reads an attribute of this name anywhere.</summary>
    /// <remarks>
    ///     ⚠ Name only, and deliberately not <em>where it stands</em>. A <c>[Semantic]</c> on a
    ///     shader means nothing, but saying so is a different diagnostic from this one and several
    ///     of the existing checks already say it — <c>RVN2091</c> for a misplaced set marker,
    ///     <c>RVN2125</c> for a <c>[Format]</c> off an image, <c>RVN2052</c> for a stage attribute
    ///     outside a shader. This one answers the question none of those could: whether the name is
    ///     a name at all.
    /// </remarks>
    public static bool IsKnownAttributeName(string name) =>
        StageAttributes.ContainsKey(name) || SetAttributes.ContainsKey(name) || MarkerAttributes.Contains(name);

    public static bool Has(SyntaxList<SyntaxToken> modifiers, SyntaxKind kind) {
        foreach (var modifier in modifiers) {
            if (modifier.Kind == kind) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bare name of an attribute, with any <c>Attribute</c> suffix removed.</summary>
    public static string GetAttributeName(AttributeSyntax attribute) {
        var name = attribute.Name switch {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => attribute.Name.ToString().Trim()
        };

        return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
            ? name[..^"Attribute".Length]
            : name;
    }

    /// <summary>Every attribute across the declaration's attribute lists.</summary>
    public static IEnumerable<AttributeSyntax> GetAttributes(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var list in attributeLists) {
            foreach (var attribute in list.Attributes) {
                yield return attribute;
            }
        }
    }

    /// <summary>
    ///     The pipeline stage named by a stage attribute on this declaration, or
    ///     <see cref="ShaderStage.None" />.
    /// </summary>
    public static ShaderStage GetShaderStage(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (StageAttributes.TryGetValue(GetAttributeName(attribute), out var stage)) {
                return stage;
            }
        }

        return ShaderStage.None;
    }

    /// <summary>True when the name is one of the recognised stage attributes.</summary>
    public static bool IsStageAttributeName(string name) => StageAttributes.ContainsKey(name);

    /// <summary>
    ///     The workgroup size written on a stage attribute — <c>[ComputeShader(8, 8, 1)]</c> —
    ///     or null when the attribute carries no arguments.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         On the stage attribute rather than an attribute of its own, so a size cannot be
    ///         separated from the stage it sizes, cannot be written twice with two answers, and
    ///         cannot be left on a declaration whose stage attribute was removed.
    ///     </para>
    ///     <para>
    ///         One to three arguments; a dimension not written is 1, which is what both targets
    ///         default to and what a 1-D dispatch means. Anything that is not a positive integer
    ///         constant returns as <see cref="WorkgroupSize.Invalid" /> rather than being silently
    ///         rounded into range — the binder reports it, because a wrong workgroup size is a
    ///         correctness bug in every invocation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A dimension may be a folded constant and not only a literal</b>, which is what
    ///         <paramref name="fold" /> is for. The number a reduction is built around is usually
    ///         already spelled as a <c>const</c> — a lane count, a tile size — and refusing it here
    ///         made the shader spell it twice, in two places that could disagree <em>in the
    ///         safe-looking direction</em>: a workgroup narrower than the array it fills simply never
    ///         loads the texels the missing lanes would have, so the answer is a fraction of the
    ///         truth with no diagnostic and no validation error. A literal still takes the fast path
    ///         and binds nothing, so a size written as a number costs exactly what it did.
    ///     </para>
    /// </remarks>
    /// <param name="attributeLists">The declaration's attributes.</param>
    /// <param name="fold">
    ///     Folds a non-literal argument to its compile-time value, or null when it has none. Absent
    ///     for a caller with no binder to fold in, which then reads literals only.
    /// </param>
    public static WorkgroupSize? GetWorkgroupSize(
        SyntaxList<AttributeListSyntax> attributeLists,
        Func<ExpressionSyntax, int?>? fold = null
    ) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (!StageAttributes.ContainsKey(GetAttributeName(attribute))) {
                continue;
            }

            if (attribute.ArgumentList is not { } arguments || arguments.Arguments.Count == 0) {
                return null;
            }

            if (arguments.Arguments.Count > 3) {
                return WorkgroupSize.Invalid;
            }

            var dimensions = new int[] { 1, 1, 1 };
            var index = 0;

            foreach (var argument in arguments.Arguments) {
                if (Dimension(argument, fold) is not { } value) {
                    return WorkgroupSize.Invalid;
                }

                dimensions[index++] = value;
            }

            return new(dimensions[0], dimensions[1], dimensions[2]);
        }

        return null;
    }

    /// <summary>One workgroup dimension, or null when it is not a positive integer constant.</summary>
    static int? Dimension(AttributeArgumentSyntax argument, Func<ExpressionSyntax, int?>? fold) {
        // A named argument is refused rather than matched by name: `[ComputeShader(y: 8)]`
        // would have to mean "x is 1", which reads as a size of 8 to everyone who writes it.
        if (argument.NameColon is not null) {
            return null;
        }

        // The positivity check is here rather than in `fold`, so a literal and a folded constant are
        // held to one rule: `[ComputeShader(0)]` and a `const val Size = 0` are the same mistake.
        object? value = argument.Expression is LiteralExpressionSyntax {
            Kind: SyntaxKind.NumericLiteralExpression
        } literal
            ? LiteralParser.Parse(literal).Value
            : fold?.Invoke(argument.Expression);

        return value switch {
            int folded and > 0 => folded,
            uint folded and > 0 and <= int.MaxValue => (int)folded,
            _ => null
        };
    }

    /// <summary>
    ///     Whether the declaration is marked <c>[Permutation]</c>, making it a compile-time
    ///     key whose value the caller supplies per effect variant.
    /// </summary>
    public static bool IsPermutation(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) == "Permutation") {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Every descriptor-set marker on the declaration, in source order, with the name it
    ///     was written as.
    /// </summary>
    /// <remarks>
    ///     All of them rather than the first, because two markers on one field is a mistake
    ///     worth naming both halves of. <see cref="GetResourceSet" /> is what callers that
    ///     only need the answer should use.
    /// </remarks>
    public static IEnumerable<(string Name, ResourceSet Set)> GetResourceSets(
        SyntaxList<AttributeListSyntax> attributeLists
    ) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            var name = GetAttributeName(attribute);
            if (SetAttributes.TryGetValue(name, out var set)) {
                yield return (name, set);
            }
        }
    }

    /// <summary>
    ///     The descriptor set the declaration is marked with, or null when it carries no
    ///     marker and the default applies.
    /// </summary>
    public static ResourceSet? GetResourceSet(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var (_, set) in GetResourceSets(attributeLists)) {
            return set;
        }

        return null;
    }

    /// <summary>True when the name is one of the recognised descriptor-set markers.</summary>
    public static bool IsResourceSetAttributeName(string name) => SetAttributes.ContainsKey(name);

    /// <summary>Whether the declaration is marked <c>[PushConstant]</c>.</summary>
    public static bool IsPushConstant(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) == "PushConstant") {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the declaration is marked <c>[Shared]</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         One resource for the whole compilation rather than a contribution from each feature
    ///         that mentions it. A composed feature's bindings are qualified by the path they were
    ///         reached through — which is what stops three features that each declare a
    ///         <c>strength</c> from colliding — and that makes a binding declared by two features two
    ///         bindings. For a value that is right. For the frame's texture table it is the opposite
    ///         of what the table is: one unbounded array bound once, which two of is two descriptor
    ///         arrays and two pools.
    ///     </para>
    ///     <para>
    ///         Marked rather than inferred from the declarations matching, because the inference is
    ///         the wrong default: two features that happened to name a texture <c>noise</c> would
    ///         silently share one descriptor, and neither author would have said anything to that
    ///         effect. Saying it is the whole point.
    ///     </para>
    /// </remarks>
    public static bool IsShared(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) == "Shared") {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the declaration is marked <c>[DynamicOffset]</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Where a block's bytes are, said by the shader instead of read off a set index.</b>
    ///         A uniform block marked this way is bound through a dynamic descriptor: one buffer, one
    ///         descriptor, and an offset moved per draw — the arrangement that keeps a Vulkan
    ///         renderer from being slower than the D3D11 one it replaced, since the alternative is a
    ///         descriptor set per object.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This existed as an inference, and the inference was two claims wearing one
    ///         number.</b> <c>EffectLoader.KindOf</c> read "a uniform block in set 3, used by a
    ///         graphics stage" as "dynamic", so the set index carried both <i>where a binding lives</i>
    ///         and <i>whether its contents change between draws</i> — and a shader wanting the first
    ///         without the second had no way to say so. It needed a carve-out for compute (a dispatch
    ///         has no draws, so <c>BindlessProbe</c>'s <c>[PerDraw]</c> block is storage rather than a
    ///         claim about draws), and the next shader wanting a plain per-draw block would have got a
    ///         refusal it could not fix in the shader. Both disappear into this.
    ///     </para>
    /// </remarks>
    public static bool IsDynamicOffset(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) == "DynamicOffset") {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the declaration is marked <c>[MaterialIndex]</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         One field carries it, and its presence changes the shape of a whole set: the
    ///         per-material uniform block becomes one <em>record</em> of a buffer holding every
    ///         material in the frame, and this is the index of the record a draw reads. Every
    ///         reference to a per-material value is then <c>materials[index].value</c> rather than a
    ///         member of a block bound per draw.
    ///     </para>
    ///     <para>
    ///         Which is the point: a draw that binds a descriptor set per material cannot be merged
    ///         with a draw that binds a different one. One buffer bound once for the frame and an
    ///         index in the per-draw data is what makes two materials' draws identical in everything
    ///         but their data — see <c>docs/plan/23-bindless-materials.md</c>.
    ///     </para>
    /// </remarks>
    public static bool IsMaterialIndex(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) == "MaterialIndex") {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The permutation a <c>[MaterialIndex("Key")]</c> is conditional on, or null when it is
    ///     unconditional.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>What makes one pass able to be both.</strong> Records are what a device with
    ///         bindless wants and a descriptor set per material is what GL, WebGL2 and MoltenVK below
    ///         argument-buffer tier 2 need (ADR-011) — so a pass that could only be one of the two
    ///         would have to be written twice, and the shipped forward pass is four hundred lines.
    ///     </para>
    ///     <para>
    ///         A permutation is the right conditional and not merely the available one: the two forms
    ///         are different <em>compilations</em> with different descriptor layouts, which is exactly
    ///         what a permutation already means everywhere else in this language. Folding a branch is
    ///         the usual consequence; changing the shape of a set is a larger one, and it is the same
    ///         mechanism.
    ///     </para>
    ///     <para>
    ///         Gating on the marked field being <em>used</em> would have been the tempting alternative
    ///         and does not work: a binding is a declared field, so it survives its last reader
    ///         folding away — which a probe confirmed before this existed.
    ///     </para>
    /// </remarks>
    public static string? GetMaterialIndexCondition(SyntaxList<AttributeListSyntax> attributeLists) =>
        StringArgumentOf(attributeLists, "MaterialIndex");

    /// <summary>
    ///     The texel format a declaration is tagged with — <c>[Format("rgba16f")]</c> — or null.
    /// </summary>
    /// <remarks>
    ///     The string as written, not a resolved <see cref="ImageFormat" />: an unrecognised name
    ///     has to reach the diagnostic that lists the recognised ones, and returning null for it
    ///     would report "no format" for a declaration that plainly has one.
    /// </remarks>
    public static string? GetImageFormat(SyntaxList<AttributeListSyntax> attributeLists) =>
        StringArgumentOf(attributeLists, "Format");

    /// <summary>Whether the declaration carries a <c>[Format]</c> at all, well-formed or not.</summary>
    public static bool HasFormat(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) == "Format") {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The pipeline semantic a declaration is tagged with —
    ///     <c>[Semantic("POSITION")]</c>, <c>[Semantic("SV_Target")]</c> — or null.
    ///     This is what the backends key stage inputs and outputs off.
    /// </summary>
    public static string? GetSemanticName(SyntaxList<AttributeListSyntax> attributeLists) =>
        StringArgumentOf(attributeLists, "Semantic");

    /// <summary>
    ///     The interpolation the declaration asks for — <c>[Interpolation("noperspective")]</c> —
    ///     or null when it carries no such attribute.
    /// </summary>
    /// <remarks>
    ///     ⚠ Three answers rather than two, and the third is what makes the diagnostic possible: a
    ///     declaration with the attribute and an unrecognised word inside it reports
    ///     <see cref="InterpolationMode" />-less rather than "no interpolation". Returning
    ///     <c>Smooth</c> for it would silently give the author the default they were writing the
    ///     attribute to decline.
    /// </remarks>
    /// <param name="attributeLists">The declaration's attributes.</param>
    /// <param name="written">The word as written, when there is an attribute at all.</param>
    /// <returns>The mode, or null when the attribute is absent or its word is not one of the four.</returns>
    public static InterpolationMode? GetInterpolation(
        SyntaxList<AttributeListSyntax> attributeLists,
        out string? written
    ) {
        written = null;

        if (!HasInterpolation(attributeLists)) {
            return null;
        }

        written = StringArgumentOf(attributeLists, "Interpolation") ?? string.Empty;

        return InterpolationModes.TryGetValue(written, out var mode) ? mode : null;
    }

    /// <summary>Whether the declaration carries an <c>[Interpolation]</c> at all, well-formed or not.</summary>
    public static bool HasInterpolation(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) == "Interpolation") {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first string literal passed to the named attribute, or null.</summary>
    static string? StringArgumentOf(SyntaxList<AttributeListSyntax> attributeLists, string name) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) != name || attribute.ArgumentList is not { } arguments) {
                continue;
            }

            foreach (var argument in arguments.Arguments) {
                if (argument.Expression is LiteralExpressionSyntax {
                        Kind: SyntaxKind.StringLiteralExpression
                    } literal) {
                    return LiteralParser.Parse(literal).Value as string;
                }
            }
        }

        return null;
    }
}
