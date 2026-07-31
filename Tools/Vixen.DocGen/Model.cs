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

/// <summary>One quantised field of a replicated component, and what it costs on the wire.</summary>
/// <param name="Field">The field the attribute is on.</param>
/// <param name="Min">The smallest value sent exactly.</param>
/// <param name="Max">The largest.</param>
/// <param name="Bits">How many bits it spends.</param>
sealed record DocQuantized(string Field, float Min, float Max, int Bits);

/// <summary>
///     The kind-specific facts — docs/plan/25 § 2.6. Every one is derived from a declaration the
///     engine already reads at compile time, and absent rather than guessed when it cannot be.
/// </summary>
sealed record DocFacets {
    /// <summary>A component's size, when its layout is knowable. See <see cref="TypeLayout" />.</summary>
    public int? SizeBytes { get; init; }

    /// <summary>Rows a 16 KB chunk holds with this component alone on the archetype.</summary>
    public int? EntitiesPerChunk { get; init; }

    /// <summary>A system's phase. <c>Update</c> when it does not say, which is the ECS's default.</summary>
    public string? Phase { get; init; }

    // Null rather than empty, all through: these are written into a file the site loads on every
    // page, and `"Reads": []` on 3 500 nodes is a megabyte of nothing.
    public IReadOnlyList<string>? Reads { get; init; }

    public IReadOnlyList<string>? Writes { get; init; }
    public IReadOnlyList<string>? RunsBefore { get; init; }
    public IReadOnlyList<string>? RunsAfter { get; init; }

    /// <summary>How a replicated component is sent.</summary>
    public string? Channel { get; init; }

    public int? SendRate { get; init; }
    public int? Priority { get; init; }
    public IReadOnlyList<DocQuantized>? Quantized { get; init; }

    /// <summary>The file extensions an importer claims.</summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>A graph node's create-menu path, which is also the key a saved graph stores.</summary>
    public string? MenuPath { get; init; }

    public string? MenuSummary { get; init; }

    /// <summary>What an annotation may be put on.</summary>
    public IReadOnlyList<string>? Targets { get; init; }

    public bool? AllowMultiple { get; init; }

    /// <summary>True when nothing was derivable, in which case the node carries no facets at all.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        SizeBytes is null && EntitiesPerChunk is null && Phase is null && Channel is null
        && SendRate is null && Priority is null && MenuPath is null && MenuSummary is null
        && AllowMultiple is null && Reads is null && Writes is null && RunsBefore is null
        && RunsAfter is null && Quantized is null && Extensions is null && Targets is null;
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

    /// <summary>The kind-specific facts, or null when the kind has none to give.</summary>
    public DocFacets? Facets { get; init; }

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
