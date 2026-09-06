// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;

namespace Vixen.Editor.AssetEditors;

/// <summary>Every diagnostic id this assembly reports, and the one sentence each of them means.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/963">#963</a>: the shape
///         <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a> gave
///         <c>Vixen.Editor.TextureGraph</c> and <a
///         href="https://github.com/Rikarin/Vixen/issues/936">#936</a> gave
///         <c>Vixen.Editor.Texturing</c>, applied to the third assembly. The shape is copied; the
///         list is not, and that is the part the issue could not have known.
///     </para>
///     <para>
///         ⚠ <b>#963 says "today each of these three assemblies has exactly one id", and for this one
///         that is false.</b> It named <c>SG0000</c>, <c>CO0000</c> and <c>VF0000</c> — the three
///         document-kind ids meaning "this file did not parse at all". The assembly also reports
///         <c>SG0100</c> from <c>ShaderGraphDocument.Blame</c> and <c>CO0001</c>…<c>CO0006</c> from
///         <c>CompositorGraphCompiler</c>, which makes nine rather than three, and makes the second
///         half of the gate the useful half rather than a precaution: the collision this exists to
///         stop had already had eight chances.
///     </para>
///     <para>
///         ⚠ <b>The <c>SG</c> family is split across two assemblies and nothing here can see the
///         other half.</b> <c>SG0001</c>…<c>SG0004</c> are in
///         <c>Vixen.Editor.ShaderGraph.ShaderGraphCompiler</c>, which does not reference this
///         assembly and is not referenced by this declaration; a roll call anchored at one project's
///         sources cannot enumerate the other's. So <c>SG0100</c>'s distance from the compiler's
///         range is a convention held by nothing, and a tenth id numbered <c>SG0002</c> here would
///         compile, gate green, and mean two things in one panel — which is exactly #804's finding.
///         That is recorded rather than absorbed: it wants one declaration per <em>family</em>, in
///         the assembly both can reference.
///     </para>
///     <para>
///         ⚠ <b><c>&lt;XX&gt;0000</c> is a per-document-kind id and folding it into a compiler's
///         family would be the wrong tidy-up.</b> <c>SG…</c> is what the shader-graph
///         <em>compiler</em> says about a graph; <c>SG0000</c> is what the <em>document</em> says
///         when the file is not a graph at all, and it has three siblings spelled the same way —
///         <c>CO0000</c>, <c>VF0000</c> and <c>TX0000</c> one assembly over. One document kind out of
///         four spelling "this file is unreadable" unlike the other three is worse than two prefixes.
///     </para>
///     <para>
///         ⚠ <b><c>Ids</c> and emphatically not <c>All</c>.</b> The texture-graph kernel roll calls
///         sweep every type in their assembly for a static <c>All</c> returning strings and take what
///         it holds to be kernel names — #814. The name is copied along with the shape.
///     </para>
/// </remarks>
static class AssetEditorDiagnostics {
    /// <summary>
    ///     A <c>.vxshadergraph</c> did not parse, so the document opened empty and carries the
    ///     parser's own complaint. ⚠ Not a graph that is wrong — one whose bytes are not a graph.
    /// </summary>
    internal const string ShaderGraphFileDoesNotParse = "SG0000";

    /// <summary>
    ///     Raven objected to the source a shader graph emitted, blamed on the node whose span covers
    ///     the line.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One id for every Raven complaint, carrying that complaint's own severity.</b> The
    ///     shader graph does not re-diagnose what the language already diagnosed; what it adds is the
    ///     node the line belongs to, which is the only part a canvas can select.
    /// </remarks>
    internal const string ShaderGraphSourceRefused = "SG0100";

    /// <summary>A <c>.vxcompositor</c> did not parse. <c>SG0000</c>'s sentence, one document over.</summary>
    internal const string CompositorFileDoesNotParse = "CO0000";

    /// <summary>A node in a compositor graph is not a compositor node, so two libraries are mixed.</summary>
    internal const string CompositorNodeIsForeign = "CO0001";

    /// <summary>A compositor graph has two frame nodes, so there are two answers to what it renders.</summary>
    internal const string CompositorHasTwoFrames = "CO0002";

    /// <summary>A compositor graph has no frame node, so nothing says what it renders.</summary>
    internal const string CompositorHasNoFrame = "CO0003";

    /// <summary>A node with flow ports is not on the frame's chain, so it does not run.</summary>
    internal const string CompositorNodeIsUnreachable = "CO0004";

    /// <summary>A node is on two chains, so it would run twice.</summary>
    internal const string CompositorNodeRunsTwice = "CO0005";

    /// <summary>Two nodes are wired to one flow output, and two nodes cannot both be next.</summary>
    internal const string CompositorFlowForks = "CO0006";

    /// <summary>A <c>.vxvfx</c> did not parse. <c>SG0000</c>'s sentence, one document over.</summary>
    internal const string VfxFileDoesNotParse = "VF0000";

    /// <summary>Every id declared above, read off the declarations rather than listed again.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes a collision findable at all.</b> Two members holding the same
    ///     string compile perfectly; a duplicate in this array does not survive
    ///     <c>AssetEditorDiagnosticIdTests</c>. Reflection over <see cref="FieldInfo.IsLiteral" />
    ///     rather than a second array, because a second array is the thing that would go stale — and
    ///     the roll call checks the query itself found something, since an empty one is trivially
    ///     distinct.
    /// </remarks>
    internal static ImmutableArray<string> Ids { get; } = [
        .. typeof(AssetEditorDiagnostics)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
    ];
}
