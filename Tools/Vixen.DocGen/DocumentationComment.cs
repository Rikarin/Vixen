// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Vixen.DocGen;

/// <summary>The parsed form of a symbol's XML doc comment — docs/plan/25 § 3.3.</summary>
/// <param name="Summary">The one-line answer. Also the search result's subtitle.</param>
/// <param name="Remarks">Everything else the author wrote.</param>
/// <param name="Returns">What a method gives back.</param>
/// <param name="SeeAlso">Documentation ids named by <c>&lt;see cref&gt;</c> and <c>&lt;seealso&gt;</c>.</param>
sealed record DocumentationComment(
    string? Summary,
    string? Remarks,
    string? Returns,
    IReadOnlyList<string> SeeAlso
) {
    public static readonly DocumentationComment Empty = new(null, null, null, []);

    /// <summary>True when the author wrote nothing a page could show.</summary>
    public bool IsEmpty => Summary is null && Remarks is null && Returns is null;

    /// <summary>
    ///     Reads a symbol's doc comment, resolving <c>&lt;inheritdoc/&gt;</c> — docs/plan/25 § 3.3.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Roslyn does not do this for us.</b> <c>GetDocumentationCommentXml</c> expands
    ///         <c>&lt;include&gt;</c> and leaves <c>&lt;inheritdoc/&gt;</c> exactly as written, so a
    ///         type that inherits its documentation renders as a blank page unless somebody walks the
    ///         base chain. The engine uses the tag, so somebody is this method.
    ///     </para>
    ///     <para>
    ///         Base class before interfaces, breadth-first, and cycle-guarded — the order the C#
    ///         documentation defines, and the one an IDE shows.
    ///     </para>
    /// </remarks>
    public static DocumentationComment For(ISymbol symbol) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<ISymbol>();

        queue.Enqueue(symbol);

        while (queue.Count > 0) {
            var current = queue.Dequeue();
            var id = current.GetDocumentationCommentId();

            if (id is not null && !seen.Add(id)) {
                continue;
            }

            var xml = current.GetDocumentationCommentXml();
            var parsed = Parse(xml);

            if (!parsed.IsEmpty) {
                return parsed;
            }

            // Only follow the chain when the author asked for it. A type that simply has no comment
            // has no comment; borrowing its base type's would put one type's prose on another's page.
            if (xml is null || !xml.Contains("<inheritdoc", StringComparison.Ordinal)) {
                continue;
            }

            foreach (var inherited in Inherited(current)) {
                queue.Enqueue(inherited);
            }
        }

        return Empty;
    }

    static ISymbol? Overridden(ISymbol symbol) => symbol switch {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null
    };

    static IEnumerable<ISymbol> Inherited(ISymbol symbol) {
        switch (symbol) {
            case INamedTypeSymbol type:
                if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType) {
                    yield return baseType;
                }

                foreach (var @interface in type.Interfaces) {
                    yield return @interface;
                }

                break;

            case { IsOverride: true } when Overridden(symbol) is { } overridden:
                yield return overridden;

                break;

            default:
                // An implicit interface implementation inherits from the member it implements, which
                // is the common case in this engine: the interface carries the prose and the class
                // carries the code.
                foreach (var member in symbol.ContainingType?.AllInterfaces
                    .SelectMany(@interface => @interface.GetMembers(symbol.Name))
                    .Where(candidate => SymbolEqualityComparer.Default.Equals(
                        symbol.ContainingType.FindImplementationForInterfaceMember(candidate), symbol))
                    ?? []) {
                    yield return member;
                }

                break;
        }
    }

    /// <summary>Parses what Roslyn hands back from <c>GetDocumentationCommentXml</c>.</summary>
    /// <remarks>
    ///     Roslyn already resolved <c>cref</c>s to documentation ids and already expanded
    ///     <c>&lt;inheritdoc/&gt;</c> when it could, so this reads the result rather than the source
    ///     text. Malformed XML returns <see cref="Empty" /> instead of throwing: a doc comment that
    ///     does not parse is a page with less on it, not a build that stops.
    /// </remarks>
    public static DocumentationComment Parse(string? xml) {
        if (string.IsNullOrWhiteSpace(xml)) {
            return Empty;
        }

        XElement root;

        try {
            root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
        } catch (System.Xml.XmlException) {
            return Empty;
        }

        var seeAlso = root.Descendants()
            .Where(element => element.Name.LocalName is "see" or "seealso")
            .Select(element => element.Attribute("cref")?.Value ?? string.Empty)
            .Where(cref => cref.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new DocumentationComment(
            Text(root.Element("summary")),
            Text(root.Element("remarks")),
            Text(root.Element("returns")),
            seeAlso
        );
    }

    /// <summary>
    ///     The element's text with its inline markup flattened and its whitespace collapsed.
    /// </summary>
    /// <remarks>
    ///     Collapsing is not cosmetic. A doc comment is written as <c>///</c> lines whose indentation
    ///     is an artefact of where the declaration sits in the file, and carrying that into JSON puts
    ///     it into the search index and onto the page. Paragraph structure that matters is in
    ///     <c>&lt;para&gt;</c>, which survives as a blank line.
    /// </remarks>
    static string? Text(XElement? element) {
        if (element is null) {
            return null;
        }

        var parts = new List<string>();

        foreach (var node in element.Nodes()) {
            switch (node) {
                case XText text:
                    parts.Add(text.Value);

                    break;

                case XElement child when child.Name.LocalName is "para":
                    parts.Add("\n\n" + Text(child) + "\n\n");

                    break;

                case XElement child when child.Name.LocalName is "see" or "seealso":
                    // A cref reads as the thing it names; the link itself is carried in SeeAlso and
                    // rebuilt by the site, which is the only place that knows the URL scheme.
                    parts.Add(ShortName(child.Attribute("cref")?.Value ?? child.Value));

                    break;

                case XElement child:
                    parts.Add(Text(child) ?? string.Empty);

                    break;
            }
        }

        var joined = string.Join(string.Empty, parts);
        var paragraphs = joined.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => string.Join(' ', paragraph.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(paragraph => paragraph.Length > 0);

        var result = string.Join("\n\n", paragraphs);

        return result.Length == 0 ? null : result;
    }

    /// <summary>`T:Vixen.Ecs.World` reads as `World`, `M:…World.Query(…)` as `Query`.</summary>
    static string ShortName(string cref) {
        var withoutPrefix = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var parameters = withoutPrefix.IndexOf('(');
        var name = parameters < 0 ? withoutPrefix : withoutPrefix[..parameters];
        var lastDot = name.LastIndexOf('.');

        return lastDot < 0 ? name : name[(lastDot + 1)..];
    }
}
