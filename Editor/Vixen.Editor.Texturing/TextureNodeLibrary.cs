// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;

namespace Vixen.Editor.Texturing;

/// <summary>A registry of texture node types, with whatever a graph containing one needs to compile.</summary>
/// <param name="Registry">The node types — the generated ones, and every compound published as one.</param>
/// <param name="SubGraphs">
///     What a compiler resolves a published node type through, as its <c>SubGraphSource</c>.
/// </param>
/// <param name="Problems">Every compound file that could not be published, and why.</param>
/// <remarks>
///     ⚠ <b>The two travel together and that is the whole point of the type.</b> A registry holding a
///     published node type whose graph the compiler cannot resolve is <em>worse</em> than one without
///     it: the node is in the search popup, an author places it, and the compilation says
///     <c>TG0001</c> — "nothing inlined it" — about a node the menu offered. So a caller that takes
///     the registry takes the source with it, and the compiler that gets one gets both.
/// </remarks>
sealed record TextureLibrary(
    NodeTypeRegistry Registry,
    ISubGraphSource SubGraphs,
    ImmutableArray<TextureCompoundProblem> Problems
);

/// <summary>The node types a <c>.vxtexgraph</c> may contain.</summary>
/// <remarks>
///     <para>
///         <b>One line for the generated half, because the generator already wrote the list.</b>
///         <c>Vixen.Editor.TextureGraph.NodeTypes</c> is emitted by
///         <c>Vixen.Editor.NodeGraph.Generator</c> over that assembly's own <c>[Node]</c> classes, so
///         the library here cannot drift from the library the compiler walks: adding a node there
///         puts it in this menu with no edit anywhere.
///     </para>
///     <para>
///         ⚠ <b>And a second half, which had no caller anywhere in the tree until now —
///         <a href="https://github.com/Rikarin/Vixen/issues/799">#799</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/803">#803</a>.</b> Doc 48 § 4.9's four
///         compounds ship inside <c>Vixen.Editor.TextureGraph</c> as <c>.vxtexgraph</c> files and
///         § D9's published graphs are the same mechanism; <c>TextureCompoundLibrary.Publish</c> is
///         what turns either into a node type, and it was called from three tests and nothing else.
///         <see cref="Publish" /> is that call. Everything batches 5–7 built behind it —
///         <c>ITextureGraphLibrary.ParametersOf</c>, the parameter-scoped expression folding, the
///         settings a sub-graph node draws — is reachable from a host for the first time through it.
///     </para>
///     <para>
///         ⚠ <b>A project's own compounds live in <c>Assets/Compounds</c> and nowhere else.</b> The
///         alternative #803 offers — walking the project for every <c>.vxtexgraph</c> — would make
///         every material's own graph a node type in every other material's menu, and the recursion
///         refusal would be the only thing between an author and a graph that contains itself. A
///         named folder is a convention somebody can read; the folder not existing is the ordinary
///         case and publishes the shipped four alone.
///     </para>
/// </remarks>
static class TextureNodeLibrary {
    /// <summary>Where a project keeps the graphs it publishes as nodes, under <c>Assets/</c>.</summary>
    public const string CompoundFolder = "Compounds";

    /// <summary>A registry holding this build's texture nodes, and nothing published.</summary>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     ⚠ <b>Deliberately without the compounds</b>, because a caller of this one has nowhere to
    ///     put a <c>SubGraphSource</c> — and a registry offering a node the compiler cannot resolve
    ///     is a <c>TG0001</c> waiting for the author who places it. A caller that compiles wants
    ///     <see cref="Publish" />; this is for one that only needs the node <em>types</em>, such as
    ///     a graph built in code out of the atomic nodes.
    ///     <para>
    ///         A fresh one per call, for <c>NodeTypeRegistry</c>'s own reason: nothing here is
    ///         global, and two panels wanting different libraries is a thing that happens the moment
    ///         two projects are open.
    ///     </para>
    /// </remarks>
    public static NodeTypeRegistry Create() {
        var registry = new NodeTypeRegistry();

        TextureGraph.NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>This build's texture nodes, plus every compound published as one.</summary>
    /// <param name="assets">
    ///     A project's <c>Assets/</c> folder, whose <c>Compounds</c> subfolder is published too, or
    ///     <see langword="null" /> for the shipped compounds alone.
    /// </param>
    /// <returns>The registry, the source that resolves the published types, and any file that failed.</returns>
    /// <remarks>
    ///     <b>A file that will not read is reported and skipped rather than thrown</b>, which is
    ///     <c>TextureCompoundLibrary.Publish</c>'s decision and the reason
    ///     <see cref="TextureLibrary.Problems" /> exists: one unreadable compound in a project must
    ///     not cost an author every other node in the menu.
    /// </remarks>
    public static TextureLibrary Publish(string? assets = null) {
        var registry = Create();
        var source = TextureCompoundLibrary.Publish(registry, FolderOf(assets), out var problems);

        return new(registry, source, problems);
    }

    /// <summary>Which folder a project's compounds are read from.</summary>
    /// <param name="assets">A project's <c>Assets/</c> folder, or <see langword="null" /> for none.</param>
    /// <returns>The folder, or <see langword="null" /> when there is no project.</returns>
    /// <remarks>
    ///     ⚠ <b>Here rather than at each caller, because a second spelling of it is a second answer
    ///     to "did that file change".</b> <c>TextureGraphDocument.Republish</c> asks whether a saved
    ///     graph is one of these, and a copy of the <c>Path.Combine</c> would be a copy that stops
    ///     agreeing the day the convention moves.
    /// </remarks>
    public static string? FolderOf(string? assets) =>
        assets is { Length: > 0 } ? Path.Combine(assets, CompoundFolder) : null;
}
