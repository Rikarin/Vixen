// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;

namespace Vixen.Editor.Texturing;

/// <summary>Every diagnostic id this assembly reports, and the one sentence each of them means.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/936">#936</a>: the shape
///         <a href="https://github.com/Rikarin/Vixen/issues/804">#804</a> gave
///         <c>Vixen.Editor.TextureGraph</c>, applied to the assembly next door before it has anything
///         to collide with.</b> There is exactly one id here today, so nothing is broken and that is
///         the point of doing it now: <c>TG0012</c>, <c>TG0017</c> and <c>TG0018</c> each came to mean
///         two things because a tenth call site typed four characters no list anywhere held, and a
///         call site cannot know what the first one means when there is nowhere to read it.
///     </para>
///     <para>
///         ⚠ <b><c>TX</c> is not a second prefix for the texture graph, and folding it into
///         <c>TG</c> would be the wrong tidy-up</b> — which the issue asked to be checked before the
///         shape was copied, and the answer is no. <c>TG…</c> is what the <em>compiler</em> says about
///         a graph: a node that refused, an expression that will not fold. <c>TX0000</c> is what the
///         <em>document</em> says when the file did not parse at all, and it has three siblings
///         spelled the same way — <c>SG0000</c> in <c>ShaderGraphDocument</c>, <c>CO0000</c> in
///         <c>CompositorDocument</c>, <c>VF0000</c> in <c>VfxDocument</c>. Two prefixes for one
///         authoring surface is a thing an author has to know; so is one document kind out of four
///         spelling "this file is unreadable" unlike the other three, and that one is worse.
///     </para>
///     <para>
///         ⚠ <b>Both halves of that paragraph's successor are closed —
///         <a href="https://github.com/Rikarin/Vixen/issues/963">#963</a>.</b> It said the three
///         siblings were still bare literals and that <c>LayerStackDocument</c> reported its load
///         failure with no id at all. <c>AssetEditorDiagnostics</c> now declares those three and the
///         six the same assembly reports beside them — nine, not three, which the issue did not know
///         — and <see cref="StackFileDoesNotParse" /> below is the fifth document kind's.
///     </para>
///     <para>
///         ⚠ <b>And that is why the fifth one is not numbered <c>0000</c>.</b> The convention is
///         per document <em>kind</em> and the prefix is per <em>assembly</em>, and this assembly
///         holds two kinds — so a <c>.vxlayers</c> cannot spell "this file is unreadable" the way its
///         four siblings do without colliding with the <c>.vxtexgraph</c> next to it. Two answers,
///         and the collision is the one that cannot be lived with.
///     </para>
///     <para>
///         <b>The declaration is here and the check reads it.</b> Two members holding one id is not a
///         compile error, so <see cref="Ids" /> is derived from the literals below by reflection and
///         <c>TexturingDiagnosticIdTests</c> requires it to be distinct — and the other half of that
///         gate walks this project's sources and refuses a <c>"TX…"</c> literal anywhere but here,
///         which is what stops the second id being invented in passing rather than found afterwards.
///     </para>
///     <para>
///         ⚠ <b><c>Ids</c> and emphatically not <c>All</c>.</b> <c>TextureDiagnostics</c> carries the
///         measurement: the kernel roll calls sweep every type in their assembly for a static
///         <c>All</c> returning strings and take what it holds to be kernel names, so a diagnostic
///         surface called <c>All</c> turns two unrelated suites red — #814. The name is copied along
///         with the shape.
///     </para>
/// </remarks>
static class TexturingDiagnostics {
    /// <summary>
    ///     A <c>.vxtexgraph</c> did not parse, so the document opened empty and carries the parser's
    ///     own complaint. ⚠ Not a graph that is wrong — one whose bytes are not a graph at all.
    /// </summary>
    /// <remarks>
    ///     <c>0000</c> because that is what the three sibling documents use for the same thing, and a
    ///     number an author learns once should mean the same thing in all four panels.
    /// </remarks>
    internal const string GraphFileDoesNotParse = "TX0000";

    /// <summary>
    ///     A <c>.vxlayers</c> did not parse, or names something this build does not have — a blend
    ///     mode a later version added — so the document opened on an empty stack.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Wider than <see cref="GraphFileDoesNotParse" />, because <c>LayerStackYaml.Read</c>
    ///     refuses rather than defaults.</b> A blend mode this build cannot spell would otherwise
    ///     composite every layer using it as a <c>Copy</c>, which is a picture rather than an error —
    ///     so an unreadable *value* lands here alongside bytes that are not YAML.
    /// </remarks>
    internal const string StackFileDoesNotParse = "TX0001";

    /// <summary>Every id declared above, read off the declarations rather than listed again.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what makes a collision findable at all.</b> Two members holding the same
    ///     string compile perfectly; a duplicate in this array does not survive
    ///     <c>TexturingDiagnosticIdTests</c>. Reflection over <see cref="FieldInfo.IsLiteral" />
    ///     rather than a second array, because a second array is the thing that would go stale — and
    ///     the roll call checks the query itself found something, since an empty one is trivially
    ///     distinct.
    /// </remarks>
    internal static ImmutableArray<string> Ids { get; } = [
        .. typeof(TexturingDiagnostics)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
    ];
}
