// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven.Binding;
using Vixen.Raven.Syntax;
using Vixen.Core.Syntax;

namespace Vixen.Raven.Symbols.Source;

/// <summary>Reads modifiers and attributes off declaration syntax.</summary>
public static class DeclarationFacts {
    /// <summary>Attribute names that mark a method as a pipeline entry point.</summary>
    static readonly Dictionary<string, ShaderStage> StageAttributes = new(StringComparer.Ordinal) {
        ["VertexShader"] = ShaderStage.Vertex,
        ["PixelShader"] = ShaderStage.Pixel,
        ["FragmentShader"] = ShaderStage.Pixel,
        ["GeometryShader"] = ShaderStage.Geometry,
        ["ComputeShader"] = ShaderStage.Compute
    };

    /// <summary>
    ///     Attribute names that place a binding in a descriptor set. One name per set, so the
    ///     four-set convention is spelled rather than numbered — see <see cref="ResourceSet" />.
    /// </summary>
    static readonly Dictionary<string, ResourceSet> SetAttributes = new(StringComparer.Ordinal) {
        ["PerFrame"] = ResourceSet.PerFrame,
        ["PerView"] = ResourceSet.PerView,
        ["PerMaterial"] = ResourceSet.PerMaterial,
        ["PerDraw"] = ResourceSet.PerDraw
    };

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

    /// <summary>
    ///     The pipeline semantic a declaration is tagged with —
    ///     <c>[Semantic("POSITION")]</c>, <c>[Semantic("SV_Target")]</c> — or null.
    ///     This is what the backends key stage inputs and outputs off.
    /// </summary>
    public static string? GetSemanticName(SyntaxList<AttributeListSyntax> attributeLists) {
        foreach (var attribute in GetAttributes(attributeLists)) {
            if (GetAttributeName(attribute) != "Semantic" || attribute.ArgumentList is not { } arguments) {
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
