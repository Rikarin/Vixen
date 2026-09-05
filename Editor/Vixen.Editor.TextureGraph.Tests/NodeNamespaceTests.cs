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
            .Where(type => type.Namespace == "Vixen.Editor.TextureGraph")
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var shadows = types
            .Where(type => type.Namespace == "Vixen.Editor.TextureGraph.Nodes" && above.Contains(type.Name))
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

    /// <summary>⚠ The guard can see a shadow at all — which is the half a list makes easy to lose.</summary>
    /// <remarks>
    ///     <b>An instrument that has stopped working reports success here</b>: an assembly whose types
    ///     came back empty, a namespace renamed, a reflection query that matches nothing. So the same
    ///     query is run over the tolerated names and required to find them, and the day the node slice
    ///     deletes the last of them this test says so in its message rather than failing.
    /// </remarks>
    [Fact]
    public void The_guard_finds_the_shadows_it_tolerates() {
        var types = Declared();

        var above = types
            .Where(type => type.Namespace == "Vixen.Editor.TextureGraph")
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var found = types
            .Where(type => type.Namespace == "Vixen.Editor.TextureGraph.Nodes" && above.Contains(type.Name))
            .Select(type => type.Name)
            .ToArray();

        Assert.NotEmpty(above);
        Assert.Contains("TextureFilter", above);

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
