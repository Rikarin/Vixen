// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Editor.TextureGraph;

namespace Tests;

/// <summary>
///     Where this assembly's kernels are declared, found by a predicate rather than by a member name.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The discriminator used to be the name <c>All</c>, which is not a predicate at all</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/814">#814</a>): every type in
///         <c>Vixen.Editor.TextureGraph</c> with a static <c>All</c> returning strings was taken to be
///         a kernel-declaring surface and whatever it held was taken to be kernel names. Measured —
///         <c>TextureDiagnostics</c> exposed its id list as <c>All</c> and two roll calls went red
///         with <c>Expected: "TG0001" / Actual: "Tile"</c>, a diff that says nothing about what
///         happened. It reads <see cref="TextureKernelSurfaceAttribute" /> now, which is a fact about
///         the type rather than about its contents.
///     </para>
///     <para>
///         ⚠ <b>The walk takes the types rather than the assembly, and that is what makes the
///         predicate provable.</b> A decoy declared in this test assembly can be handed to the same
///         method the roll calls use — with the attribute and without it — so the discriminator has a
///         true case and a false case that are one attribute apart. Sweeping an assembly directly
///         would leave "a surface without the attribute is ignored" as an assertion nobody could make
///         without shipping a decoy in the production assembly forever.
///     </para>
///     <para>
///         <b>What the two roll calls still do separately is ask different questions of the same
///         set.</b> <c>TextureColourKernelTests</c> asks whether the folder and the declarations
///         agree; <c>TextureNodeLibraryTests</c> asks whether the declarations and the node library
///         do. Sharing <em>where the declarations are</em> is not sharing either question — it is the
///         one line of convention both were transcribing.
///     </para>
/// </remarks>
static class TextureKernelSurfaces {
    /// <summary>Every type in the assembly that ships the kernels.</summary>
    /// <remarks>
    ///     ⚠ Named from <see cref="TextureKernels" /> rather than by string, so a renamed assembly is
    ///     a compile error here rather than an empty sweep that reports complete coverage.
    /// </remarks>
    public static Type[] Assembly => typeof(TextureKernels).Assembly.GetTypes();

    /// <summary>The declaring surfaces among some types.</summary>
    /// <param name="types">The types to consider.</param>
    /// <returns>Those marked as kernel-declaring surfaces.</returns>
    public static IEnumerable<Type> Surfaces(IEnumerable<Type> types) =>
        types.Where(type => type.GetCustomAttribute<TextureKernelSurfaceAttribute>() is not null);

    /// <summary>Every kernel name the given types declare.</summary>
    /// <param name="types">The types to consider; the surfaces among them are read.</param>
    /// <returns>Names, with duplicates, in declaration order.</returns>
    /// <remarks>
    ///     Two shapes are answered because a slice declares whichever it has:
    ///     <c>TextureColourKernels.All</c> is a list of names and <c>TextureSources.All</c> is a list
    ///     of ops that carry one. Neither is a list of kernels written twice.
    /// </remarks>
    public static IEnumerable<string> Names(IEnumerable<Type> types) {
        foreach (var value in Values(types)) {
            switch (value) {
                case IEnumerable<string> names:
                    foreach (var name in names) {
                        yield return name;
                    }

                    break;

                case IEnumerable<TextureOp> ops:
                    foreach (var op in ops) {
                        yield return op.Kernel;
                    }

                    break;
            }
        }
    }

    /// <summary>Every op the given types declare.</summary>
    /// <param name="types">The types to consider; the surfaces among them are read.</param>
    /// <returns>The ops, in declaration order.</returns>
    public static IEnumerable<TextureOp> Ops(IEnumerable<Type> types) =>
        Values(types).OfType<IEnumerable<TextureOp>>().SelectMany(ops => ops);

    /// <summary>What each surface's declaration member holds.</summary>
    /// <param name="types">The types to consider.</param>
    /// <returns>One value per surface that has an <c>All</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>The member name survives, and now it is only a member name.</b> The attribute decides
    ///     what a surface is; <c>All</c> merely says where on that surface the declaration lives, and
    ///     a marked type with no <c>All</c> contributes nothing rather than throwing — which is what
    ///     lets a surface be marked in the commit before the one that fills it.
    /// </remarks>
    static IEnumerable<object> Values(IEnumerable<Type> types) =>
        Surfaces(types)
            .Select(type => type.GetProperty("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(all => all is not null)
            .Select(all => all!.GetValue(null))
            .Where(value => value is not null)
            .Select(value => value!);
}
