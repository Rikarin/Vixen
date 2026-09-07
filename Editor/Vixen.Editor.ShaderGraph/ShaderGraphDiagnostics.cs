// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;

namespace Vixen.Editor.ShaderGraph;

/// <summary>Every <c>SG</c> and <c>SGP</c> diagnostic, wherever in the tree it is reported from.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/982">#982</a>: one declaration per
///         <em>family</em>, which is the scope the per-assembly shape
///         <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a>,
///         <a href="https://github.com/Rikarin/Vixen/issues/936">#936</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/963">#963</a> gave three assemblies got
///         wrong.</b> <c>SG0000</c> and <c>SG0100</c> were declared in
///         <c>Vixen.Editor.AssetEditors.AssetEditorDiagnostics</c> while <c>SG0001</c>…<c>SG0004</c>
///         were bare literals here, so neither project's roll call could enumerate the other's half
///         and a tenth id numbered <c>SG0002</c> in the wrong file would have compiled, gated green,
///         and meant two things in the one panel an author reads them in.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Editor.NodeGraph</c>, and #982's reason for proposing
///         that home does not hold.</b> The issue says the two assemblies "do not reference one
///         another"; <c>Vixen.Editor.AssetEditors.csproj</c> has a <c>ProjectReference</c> to this
///         project, and always did. Only one of the two directions was ever missing, and it is the
///         one that points away from the family's own name — so the declaration lives beside the
///         compiler that raises most of it rather than a layer down beside <c>NodeDiagnostic</c>,
///         which #982 itself notes would put it "a long way from what reports it".
///     </para>
///     <para>
///         ⚠ <b><c>SGP</c> is a second family and is declared here anyway, because it is reported
///         from this assembly and nowhere else.</b> Leaving three literals behind in
///         <c>ShaderGraphPreview</c> would have been the same defect one prefix over, and a roll call
///         that walks this project for <c>SG</c> and steps over <c>SGP</c> is a gate with a hole cut
///         in it on purpose.
///     </para>
///     <para>
///         ⚠ <b><c>Ids</c> and emphatically not <c>All</c>.</b> The texture-graph kernel roll calls
///         sweep every type in their assembly for a static <c>All</c> returning strings and take what
///         it holds to be kernel names — #814. The name is copied along with the shape.
///     </para>
/// </remarks>
static class ShaderGraphDiagnostics {
    /// <summary>
    ///     A <c>.vxshadergraph</c> did not parse, so the document opened empty and carries the
    ///     parser's own complaint. ⚠ Not a graph that is wrong — one whose bytes are not a graph.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A per-document-kind id, and its three siblings are spelled the same way one assembly
    ///     over</b> — <c>CO0000</c>, <c>VF0000</c> and <c>TX0000</c>. That is why it keeps the family's
    ///     prefix rather than joining the compiler's numbering: <c>SG…</c> is what the shader-graph
    ///     compiler says about a graph, and this is what the <em>document</em> says when the file is
    ///     not a graph at all. It is reported from <c>Vixen.Editor.AssetEditors</c>, which is the
    ///     seam this declaration exists to close.
    /// </remarks>
    internal const string FileDoesNotParse = "SG0000";

    /// <summary>A node in the graph's library is not a shader node, so it can emit nothing.</summary>
    internal const string NodeIsNotAShaderNode = "SG0001";

    /// <summary>A graph has two master nodes, and a shader has one output.</summary>
    internal const string GraphHasTwoMasters = "SG0002";

    /// <summary>A graph has no master node, so there is nothing for the shader to write.</summary>
    internal const string GraphHasNoMaster = "SG0003";

    /// <summary>A surface graph reads a stream a surface shader does not have.</summary>
    internal const string SurfaceReadsAForbiddenStream = "SG0004";

    /// <summary>
    ///     Raven objected to the source a shader graph emitted, blamed on the node whose span covers
    ///     the line.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One id for every Raven complaint, carrying that complaint's own severity.</b> The
    ///         shader graph does not re-diagnose what the language already diagnosed; what it adds is
    ///         the node the line belongs to, which is the only part a canvas can select.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its distance from <see cref="SurfaceReadsAForbiddenStream" /> is now a convention
    ///         something holds.</b> The gap was chosen to leave the compiler room to grow, and while
    ///         the two halves of the family were declared in assemblies that could not see each other
    ///         it was a convention held by nothing at all — the state #982 reports.
    ///     </para>
    /// </remarks>
    internal const string SourceRefused = "SG0100";

    /// <summary>A preview was asked for a node the graph does not hold.</summary>
    internal const string PreviewNodeIsNotInTheGraph = "SGP0001";

    /// <summary>A preview was asked for a node whose type nothing registers, so it has no expression.</summary>
    internal const string PreviewNodeTypeIsUnregistered = "SGP0002";

    /// <summary>A preview was asked for a node with no output that could be shown as a colour.</summary>
    internal const string PreviewNodeHasNoColourOutput = "SGP0003";

    /// <summary>Every id declared above, read off the declarations rather than listed again.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes a collision findable at all.</b> Two members holding the same
    ///     string compile perfectly; a duplicate in this array does not survive
    ///     <c>ShaderGraphDiagnosticIdTests</c>. Reflection over <see cref="FieldInfo.IsLiteral" />
    ///     rather than a second array, because a second array is the thing that would go stale — and
    ///     the roll call checks the query itself found something, since an empty one is trivially
    ///     distinct.
    /// </remarks>
    internal static ImmutableArray<string> Ids { get; } = [
        .. typeof(ShaderGraphDiagnostics)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
    ];
}
