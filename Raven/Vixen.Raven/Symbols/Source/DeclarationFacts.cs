using Vixen.Raven.Binding;
using Vixen.Raven.Syntax;

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

    public static bool Has(SyntaxList<SyntaxToken> modifiers, SyntaxKind kind) {
        foreach (var modifier in modifiers) {
            if (modifier.Kind == kind) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The declared accessibility, or <paramref name="fallback" /> when no access
    ///     modifier is present.
    /// </summary>
    public static Accessibility GetAccessibility(SyntaxList<SyntaxToken> modifiers, Accessibility fallback) {
        var isProtected = false;
        var isInternal = false;

        foreach (var modifier in modifiers) {
            switch (modifier.Kind) {
                case SyntaxKind.PublicKeyword:
                    return Accessibility.Public;
                case SyntaxKind.PrivateKeyword:
                    return Accessibility.Private;
                case SyntaxKind.ProtectedKeyword:
                    isProtected = true;
                    break;
                case SyntaxKind.StaticKeyword:
                case SyntaxKind.AbstractKeyword:
                default:
                    break;
            }
        }

        return isProtected ? Accessibility.Protected : isInternal ? Accessibility.Internal : fallback;
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
