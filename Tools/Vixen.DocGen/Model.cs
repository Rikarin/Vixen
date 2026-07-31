// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Vixen.DocGen;

/// <summary>What kind of thing a node is — docs/plan/25 § 2.3.</summary>
/// <remarks>
///     A kind is a fact about the code rather than a label somebody maintains: every value below is
///     decided by <see cref="Taxonomy" /> from an attribute, a base type or an interface that the
///     engine already relies on at compile time. The string form is what the site filters on, so the
///     names are stable and kebab-cased.
/// </remarks>
enum DocKind {
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    Component,
    SceneComponent,
    System,
    Behavior,
    ReplicatedComponent,
    UiControl,
    GraphNode,
    Importer,
    Annotation,
    Generator
}

/// <summary>Where a declaration is, and where to read it on GitHub.</summary>
/// <param name="Path">Repository-relative, forward slashes, so it is the same on every OS.</param>
/// <param name="StartLine">1-based, inclusive.</param>
/// <param name="EndLine">1-based, inclusive.</param>
/// <param name="Url">The blob URL at the documented commit, or null when there is no commit to name.</param>
sealed record DocSource(string Path, int StartLine, int EndLine, string? Url);

/// <summary>An attribute as it was written, argument values included.</summary>
/// <param name="Id">The attribute type's documentation-comment id.</param>
/// <param name="Name">Its short name without the <c>Attribute</c> suffix, for display.</param>
/// <param name="Arguments">Positional then named, formatted as source.</param>
sealed record DocAttribute(string Id, string Name, IReadOnlyList<string> Arguments);

/// <summary>One public member of a type.</summary>
sealed record DocMember {
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Field, property, method, event, constructor, operator — the display grouping.</summary>
    public required string MemberKind { get; init; }

    public required string Signature { get; init; }
    public string? Summary { get; init; }
    public string? Returns { get; init; }
    public bool IsStatic { get; init; }
    public string? Obsolete { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<DocAttribute> Attributes { get; init; } = [];

    public DocSource? Source { get; init; }
}

/// <summary>One documented type.</summary>
sealed record DocNode {
    /// <summary>The ECMA-334 documentation-comment id — <c>T:Vixen.Ecs.World</c>. See § 2.2.</summary>
    public required string Id { get; init; }

    public required DocKind Kind { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required string Namespace { get; init; }
    public required string Assembly { get; init; }

    /// <summary>Top-level folder — <c>Core</c>, <c>Platform</c>, <c>Editor</c>, <c>Tools</c>, <c>Raven</c>.</summary>
    public required string Area { get; init; }

    /// <summary>The URL path the site serves this at, derived from the id and never stored twice.</summary>
    public required string Slug { get; init; }

    public required string Signature { get; init; }
    public string? Summary { get; init; }
    public string? Remarks { get; init; }
    public string? BaseType { get; init; }
    public IReadOnlyList<string> Interfaces { get; init; } = [];
    public IReadOnlyList<DocAttribute> Attributes { get; init; } = [];
    public IReadOnlyList<DocMember> Members { get; init; } = [];

    /// <summary>Ids named by <c>&lt;see cref&gt;</c> and <c>&lt;seealso&gt;</c> in the doc comment.</summary>
    public IReadOnlyList<string> SeeAlso { get; init; } = [];

    public string? Obsolete { get; init; }

    /// <summary>True when the declaration came from a generator rather than from a file in the tree.</summary>
    public bool IsGenerated { get; init; }

    /// <summary>True when the assembly carries a <c>PublicAPI.*.txt</c> — the surface CheckApi gates.</summary>
    public bool IsPackable { get; init; }

    /// <summary>
    ///     Other assemblies that compile the same declaration, when shared source is linked across a
    ///     project boundary rather than referenced. The page is written once, at the packable copy.
    /// </summary>
    public IReadOnlyList<string> AlsoIn { get; init; } = [];

    public DocSource? Source { get; init; }
}

/// <summary>The whole emitted graph, and what it was produced from.</summary>
sealed record DocGraph {
    public required string Solution { get; init; }
    public required string Configuration { get; init; }
    public string? Commit { get; init; }
    public required int ProjectCount { get; init; }
    public required int GeneratedDocumentCount { get; init; }
    public required IReadOnlyList<DocNode> Nodes { get; init; }
}
