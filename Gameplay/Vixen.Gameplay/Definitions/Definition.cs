// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Reflection;

namespace Vixen.Gameplay;

/// <summary>Authored, immutable, addressable content — the first of doc 28's four layers.</summary>
/// <remarks>
///     <para>
///         An item, a quest, an ability, a recipe, a loot table, a currency, a battleground: all of
///         them are a record deriving from this, authored as YAML with a type tag, built by the
///         content build, and resolved through a <see cref="DefinitionRegistry" />. A game's own
///         <c>!MyCustomDefinition</c> is the same kind of object as the engine's, which is the seam
///         that keeps the library set opinionated without being closed.
///     </para>
///     <para>
///         <b>A record, so a definition is compared and copied by value</b>, and so
///         <see cref="Address" /> can be stamped on with a <c>with</c> expression by whoever knows the
///         address — which is the content build, not the file. The file holds what a designer wrote;
///         where it sits is the pipeline's answer, and a definition that carried its own address
///         would be a definition that could disagree with where it was found.
///     </para>
///     <para>
///         ⚠ <b><see cref="Address" /> and <see cref="Id" /> are set together or not at all.</b>
///         <see cref="DefinitionCatalogBuilder" /> is the only thing that sets them, and it derives
///         the second from the first. Setting them apart is possible — they are ordinary <c>init</c>
///         properties — and produces a definition whose id names something else, so do not.
///     </para>
/// </remarks>
public abstract record Definition {
    /// <summary>Where the content build found it — <c>items/flamebrand</c>.</summary>
    /// <remarks>
    ///     <see cref="DataMemberIgnoreAttribute" />: it is not authored, it is where the authoring
    ///     lives. A <c>.vxdef</c> that set it would be a file arguing with the directory it is in.
    /// </remarks>
    [DataMemberIgnore]
    public string Address { get; init; } = string.Empty;

    /// <summary>The hash of <see cref="Address" /> — what the wire carries and a saved row stores.</summary>
    [DataMemberIgnore]
    public DefId Id { get; init; }

    /// <summary>What this definition is called in a <c>.vxdef</c> type tag, for diagnostics.</summary>
    /// <remarks>
    ///     Read off the generated type descriptor rather than off <c>GetType().Name</c>, because the
    ///     tag is the <c>[DataContract]</c> alias and those are allowed to differ — and because a
    ///     trimmed publish keeps the descriptor and may not keep the name.
    /// </remarks>
    public string TypeName =>
        TypeRegistry.TryGet(GetType(), out var descriptor) ? descriptor.Alias : GetType().Name;

    /// <summary>Every tag this definition mentions, so the content build can bake the tag table.</summary>
    /// <param name="tags">What to add the names to.</param>
    /// <remarks>
    ///     <para>
    ///         <b>Declared rather than discovered.</b> The alternative is walking a definition's fields
    ///         by reflection looking for tag-shaped strings, which is a trim hazard, a guess, and
    ///         silently wrong for a tag that lives inside a list of nested records. A definition
    ///         knows what it mentions; overriding this is three lines and the build fails loudly
    ///         without it — a rule about a tag nobody baked matches nothing.
    ///     </para>
    ///     <para>
    ///         The base adds nothing. A definition with no tags does not need to override it.
    ///     </para>
    /// </remarks>
    public virtual void CollectTags(ICollection<string> tags) { }
}
