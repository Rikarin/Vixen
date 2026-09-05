// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>What a type inside <c>Nodes/</c> may not be: a second declaration of a name above it.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/727">#727</a>.</b>
///         <c>Vixen.Editor.TextureGraph.Nodes</c> is a child namespace, so a type it declares
///         <em>hides</em> one of the same name in the parent with no diagnostic and no build error —
///         and a <c>using Vixen.Editor.TextureGraph;</c> does not change that, because the
///         enclosing-namespace walk is consulted before any import. So a node reading
///         <c>TextureFilter</c> gets whichever declaration is nearer, and moving that node one folder
///         out changes what its code means while still compiling.
///     </para>
///     <para>
///         ⚠ <b>It matters because these are kernel contracts.</b> The parent's
///         <c>TextureFilter</c> has three members and the shadow in <c>FilterNodes.cs</c> has two, so
///         the refusal <c>TextureSettings.Enum</c> writes lists two of three, and the number that
///         reaches the kernel comes from a second table that has to agree with the first — which is
///         precisely the arrangement <c>TextureSettings</c>' own remarks exist to argue against.
///     </para>
///     <para>
///         ⚠ <b>The two known shadows are tolerated by name and by name only.</b> They are in files
///         another agent owns this batch, so this cannot be a red test today — what it does instead
///         is stop a third from being added, and name the two that have to go. It deliberately does
///         <em>not</em> fail when a listed name is cleaned up, unlike <c>docs/WhitespaceExempt.txt</c>
///         and the rest of this repository's shrink-only lists: that symmetry would put a failure on
///         the branch that <em>fixed</em> the defect. Delete the line with the declaration.
///     </para>
/// </remarks>
public class NodeNamespaceTests {
    /// <summary>The namespace every node type is in, spelled once.</summary>
    /// <remarks>
    ///     ⚠ <b>One spelling because two would drift apart silently.</b> Both facts below are
    ///     computed from this string, so a rename that makes the query match nothing makes
    ///     <see cref="The_guard_can_see_the_namespace_it_guards" /> red rather than making everything
    ///     here vacuously true — which is what
    ///     <a href="https://github.com/Rikarin/Vixen/issues/747">#747</a> was.
    /// </remarks>
    const string Nodes = "Vixen.Editor.TextureGraph.Nodes";

    /// <summary>The namespace they may not redeclare a name from.</summary>
    const string Assembly = "Vixen.Editor.TextureGraph";

    /// <summary>The shadows that exist today, which the node slice owes a deletion for.</summary>
    /// <remarks>
    ///     <c>Nodes/FilterNodes.cs</c> declares both. The parent declarations are in
    ///     <c>TextureKernels.Colour.cs</c> and are what a node should read — ⚠ noting that the
    ///     parent's <c>TextureFilter.Box</c> is a <c>Resample</c>-only mode which
    ///     <c>Transform2D.rvn</c> treats as bilinear, so the node owes a refusal of that member by
    ///     name rather than a two-member twin of the enum.
    /// </remarks>
    static readonly string[] Known = ["TextureTiling", "TextureFilter"];

    /// <summary>No type under <c>Nodes/</c> redeclares a name the assembly already has.</summary>
    [Fact]
    public void A_node_type_does_not_shadow_a_name_the_assembly_already_declares() {
        var types = Declared();

        var above = types
            .Where(type => type.Namespace == Assembly)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var shadows = types
            .Where(type => type.Namespace == Nodes && above.Contains(type.Name))
            .Select(type => type.Name)
            .Where(name => !Known.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            shadows.Length == 0,
            $"Nodes/ declares {string.Join(", ", shadows)}, which the parent namespace already declares. "
            + "Inside Nodes/ the nearer declaration wins with no diagnostic, so the two can drift a member "
            + "apart and a node's number can come from the wrong table — #727. Use the assembly's type."
        );
    }

    /// <summary>⚠ Both halves of the guard's query find something, so neither is vacuously true.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>An instrument that has stopped working reports success</b>: an assembly whose types
    ///         came back empty, a namespace renamed, a reflection query that matches nothing. Both of
    ///         the guard's queries are therefore required to return types — the parent's, which is
    ///         where a shadowed name is declared, and <c>Nodes/</c>'s, which is where a shadow would
    ///         be.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second half was missing and it is exactly the half that matters</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/747">#747</a>. The only assertion over
    ///         the <c>Nodes/</c> query was an <c>Assert.All</c>, which passes over an empty sequence,
    ///         so renaming the namespace literal by one character left both tests green with the
    ///         guard dead — measured, and this file's own remarks claimed the opposite. A shadow list
    ///         makes that trivially easy to reach: the tolerated names are the only thing the query is
    ///         expected to find, and "found nothing" and "found only tolerated names" are the same
    ///         answer to an <c>Assert.All</c>.
    ///     </para>
    ///     <para>
    ///         It still does <em>not</em> require a shadow to exist: the day the node slice deletes
    ///         the last of them the guard is alive and finding nothing, which is the point. What it
    ///         requires is that <c>Nodes/</c> holds types at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_guard_can_see_the_namespace_it_guards() {
        var types = Declared();

        var above = types
            .Where(type => type.Namespace == Assembly)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var inNodes = types.Where(type => type.Namespace == Nodes).Select(type => type.Name).ToArray();

        var found = inNodes.Where(name => above.Contains(name)).ToArray();

        Assert.NotEmpty(above);
        Assert.Contains("TextureFilter", above);

        // The half #747 was about: without it the query below could match nothing and every
        // assertion over it would pass.
        Assert.True(
            inNodes.Length > 0,
            $"No type in this assembly is in namespace '{Nodes}', so the shadow guard matches nothing "
            + "and passes over an empty sequence. Either the node classes moved, or the namespace was "
            + "renamed and this literal was not — #747."
        );

        // Not an equality with Known: a name that has been cleaned up is progress, and failing on it
        // would put the failure on the branch that did the cleaning.
        Assert.All(found, name => Assert.Contains(name, Known, StringComparer.Ordinal));
    }

    /// <summary>The assembly's own types, without the ones the compiler wrote.</summary>
    /// <remarks>
    ///     ⚠ <b>The first version of this query reported eight shadows and all of them were
    ///     <c>&lt;&gt;c</c></b> — a lambda's display class is nested, carries its enclosing
    ///     namespace, and is called the same thing in every namespace there is. A guard whose failure
    ///     names a compiler-generated type is a guard nobody will read the second time.
    /// </remarks>
    static Type[] Declared() =>
        [
            .. typeof(TexturePlan).Assembly.GetTypes()
                .Where(type => !type.IsNested && !type.Name.StartsWith('<'))
        ];
}
