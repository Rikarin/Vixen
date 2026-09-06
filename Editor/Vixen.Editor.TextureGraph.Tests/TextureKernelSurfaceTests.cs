// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     <a href="https://github.com/Rikarin/Vixen/issues/814">#814</a>: the roll calls' declaring-surface
///     detector, with the decoy that would have joined the kernel inventory under the old one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every assertion in this file is about the predicate and none is about the kernels.</b>
///         Whether the folder and the declarations agree is <c>TextureColourKernelTests</c>' question
///         and whether the declarations and the library agree is <c>TextureNodeLibraryTests</c>'; both
///         are worth nothing if the thing that decides <em>what a declaration is</em> answers yes to
///         everything. A rule's subject set is the half of it nothing checks.
///     </para>
///     <para>
///         ⚠ <b>The decoy is the false case, and it is a real one rather than an illustration.</b>
///         When <c>TextureDiagnostics</c> first exposed its derived id list as <c>All</c>, both roll
///         calls went red with an <c>Assert.Equal</c> collection diff reading
///         <c>Expected: "TG0001" / Actual: "Tile"</c> — a failure that says nothing about what
///         happened, and whose first reading is that a kernel went missing. The registry was renamed
///         to <c>Ids</c>, and until this file the fix was a convention: the next surface here to reach
///         for the obvious name got the same opaque failure.
///     </para>
/// </remarks>
public class TextureKernelSurfaceTests {
    /// <summary>A non-kernel registry that reaches for the obvious name, as one already did.</summary>
    /// <remarks>
    ///     Diagnostic-id-shaped strings rather than arbitrary ones, because that is what the measured
    ///     collision held: <c>TextureDiagnostics</c>' derived id list, which is a static <c>All</c> of
    ///     strings in this assembly's own idiom and is not a kernel declaration. ⚠ Out of the
    ///     registered range on purpose — <c>TextureDiagnosticIdTests</c> is a roll call over ids
    ///     written as literals, and a decoy holding a real one would make this file its finding.
    /// </remarks>
    static class Decoy {
        public static IReadOnlyList<string> All { get; } = ["TG9001", "TG9002"];
    }

    /// <summary>The same registry, marked — so the two differ in the attribute and nothing else.</summary>
    [TextureKernelSurface]
    static class MarkedDecoy {
        public static IReadOnlyList<string> All { get; } = ["TG9001", "TG9002"];
    }

    /// <summary>A marked surface declaring ops rather than names.</summary>
    [TextureKernelSurface]
    static class MarkedOps {
        public static ImmutableArray<TextureOp> All { get; } = [new() { Kernel = "Decoy/Op", Output = 0 }];
    }

    /// <summary>
    ///     ⚠ A static <c>All</c> of strings joins the kernel inventory only when its type says it is a
    ///     kernel surface.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both halves, one attribute apart.</b> The unmarked decoy contributes nothing and the
    ///         marked one contributes both its strings, so the predicate has a false case and a true
    ///         case and neither is the other's absence. Under the member-name detector the first of
    ///         these two assertions was impossible: there was nothing to write that would have been
    ///         false.
    ///     </para>
    ///     <para>
    ///         <b>Handed to the same method the roll calls call</b>, over a type list rather than an
    ///         assembly — which is the whole reason <see cref="TextureKernelSurfaces.Names" /> takes
    ///         types. Proving this against the production assembly would have meant shipping a decoy
    ///         in it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_unmarked_static_all_of_strings_is_not_a_kernel_declaration() {
        Assert.Empty(TextureKernelSurfaces.Names([typeof(Decoy)]));
        Assert.Equal(["TG9001", "TG9002"], TextureKernelSurfaces.Names([typeof(MarkedDecoy)]));

        // And the op shape is read from a marked type too, so the two declaration idioms are one
        // predicate rather than two.
        Assert.Equal(["Decoy/Op"], TextureKernelSurfaces.Names([typeof(MarkedOps)]));
        Assert.Empty(TextureKernelSurfaces.Ops([typeof(MarkedDecoy)]));
        Assert.Single(TextureKernelSurfaces.Ops([typeof(MarkedOps)]));
    }

    /// <summary>
    ///     ⚠ The marker finds the surfaces this assembly actually has, and finding none is a failure
    ///     rather than an empty roll call.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Ask what the roll calls print on the day nothing is marked.</b> Every kernel would
    ///         read as undeclared and <c>The_folder_holds_these_kernels_and_no_others</c> would fail
    ///         with a fifty-name diff — legible, but only after somebody has read fifty names. This
    ///         says it in one line, and it is the risk the attribute introduced that the member name
    ///         did not have.
    ///     </para>
    ///     <para>
    ///         <b>Named rather than counted.</b> A count would be re-blessed by whoever broke it; the
    ///         two named surfaces are the two declaration idioms, so a marker that stopped being read
    ///         at all and one that stopped answering ops are different failures here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_assembly_marks_the_surfaces_that_declare_its_kernels() {
        var surfaces = TextureKernelSurfaces.Surfaces(TextureKernelSurfaces.Assembly).ToList();

        Assert.True(
            surfaces.Count >= 8,
            $"Only {surfaces.Count} kernel-declaring surface(s) are marked in this assembly. Every roll call "
            + "here reads that marker, and one that finds nothing reports a complete inventory of nothing."
        );

        Assert.Contains(surfaces, type => string.Equals(type.Name, "TextureColourKernels", StringComparison.Ordinal));
        Assert.Contains(surfaces, type => string.Equals(type.Name, "TextureSources", StringComparison.Ordinal));

        // The two idioms are both present, so a marker that answered only one of them is a failure
        // here rather than a silently shorter inventory.
        Assert.NotEmpty(TextureKernelSurfaces.Ops(TextureKernelSurfaces.Assembly));
        Assert.NotEmpty(TextureKernelSurfaces.Names(TextureKernelSurfaces.Assembly));
    }
}
