// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.DocGen;

/// <summary>Derives URL paths from documentation ids — docs/plan/25 § 2.2.</summary>
/// <remarks>
///     <para>
///         Derived, never stored: a URL kept beside an id is a second thing that can disagree with
///         the first. Everything the site links to goes through here.
///     </para>
///     <para>
///         ⚠ <b>Two collisions matter and both are handled.</b> Generic arity — <c>List`1</c> and
///         <c>List`2</c> would otherwise share a URL — becomes a <c>-1</c> suffix. Case — Cloudflare
///         serves case-sensitive asset paths while a Windows checkout does not — is removed by
///         lowercasing everything, which makes <c>IPin</c> and <c>IPIN</c> collide; a suffix on the
///         second is not enough, so the emitter asserts uniqueness instead of hoping.
///     </para>
/// </remarks>
static class Slugs {
    /// <summary>`T:Vixen.Ecs.World` → `vixen.ecs/world`; a nested `Query.Builder` → `…/query.builder`.</summary>
    /// <param name="documentationId">The type's documentation-comment id.</param>
    /// <param name="containingNamespace">
    ///     ⚠ <b>Required to get a nested type right.</b> A documentation id writes nesting with a dot
    ///     — <c>T:Vixen.Ecs.Query.Builder</c> — which is the same character it writes namespaces
    ///     with, so the id alone cannot say whether <c>Query</c> is a namespace or the containing
    ///     type. Splitting at the last dot puts <c>Builder</c> in a namespace that does not exist.
    /// </param>
    public static string ForType(string documentationId, string? containingNamespace = null) {
        var name = documentationId.Length > 2 && documentationId[1] == ':'
            ? documentationId[2..]
            : documentationId;

        string @namespace;
        string type;

        if (!string.IsNullOrEmpty(containingNamespace)
            && name.StartsWith(containingNamespace + ".", StringComparison.Ordinal)) {
            @namespace = containingNamespace;
            type = name[(containingNamespace.Length + 1)..];
        } else {
            var lastDot = name.LastIndexOf('.');
            @namespace = lastDot < 0 ? string.Empty : name[..lastDot];
            type = lastDot < 0 ? name : name[(lastDot + 1)..];
        }

        return @namespace.Length == 0
            ? Sanitize(type)
            : Sanitize(@namespace) + "/" + Sanitize(type);
    }

    /// <summary>`Vixen.Ecs.Systems` → `vixen.ecs.systems`, the namespace page.</summary>
    public static string ForNamespace(string qualifiedName) =>
        qualifiedName.Length == 0 ? "global" : Sanitize(qualifiedName);

    /// <summary>
    ///     Lowercased, with the characters a path cannot carry replaced by ones it can. Dots survive
    ///     inside a segment because <c>vixen.ecs</c> reads as the namespace it is.
    /// </summary>
    static string Sanitize(string value) {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value) {
            switch (character) {
                case '`':
                    // Generic arity. `List`1` → `list-1`, so two arities are two pages.
                    builder.Append('-');

                    break;

                case '+':
                    // Nested type. Roslyn writes the containing type with `+`; a dot reads better
                    // and cannot collide, because a namespace segment and a type name never share a
                    // position in the path.
                    builder.Append('.');

                    break;

                case '{' or '}' or '@' or ',' or ' ':
                    builder.Append('-');

                    break;

                default:
                    builder.Append(char.ToLowerInvariant(character));

                    break;
            }
        }

        return builder.ToString();
    }
}
