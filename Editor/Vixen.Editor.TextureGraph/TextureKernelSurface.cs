// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Vixen.Editor.TextureGraph;

/// <summary>
///     Marks a type as one of the places this assembly declares its kernels.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists because the discriminator used to be a member name, and a member name is
///         not a predicate</b> (<a href="https://github.com/Rikarin/Vixen/issues/814">#814</a>). Two
///         roll calls swept every type in this assembly for a static <c>All</c> returning strings and
///         took whatever it held to be kernel names — so any surface that reached for the obvious name
///         joined the kernel inventory. <b>Measured, not hypothesised:</b> when
///         <c>TextureDiagnostics</c> first exposed its id list as <c>All</c>, both roll calls went red
///         with a collection diff whose expected value was a diagnostic id and whose actual value was
///         a kernel name — a failure that says nothing whatever about what happened, and whose first
///         reading is that a kernel went missing. The registry was renamed to <c>Ids</c> and the trap
///         was left as a convention — and a convention is what the next surface here does not know
///         about.
///     </para>
///     <para>
///         <b>Declared where the kernels are declared, so it is not a second list.</b> A slice that
///         adds a surface and forgets the attribute fails the way a slice that adds a kernel with no
///         node already does: its kernels ship, nothing declares them, and
///         <c>TextureNodeLibraryTests.The_folder_holds_these_kernels_and_no_others</c> says which ones
///         are missing by name. That is a legible failure rather than the opaque one above, which is
///         the whole difference the attribute buys.
///     </para>
///     <para>
///         ⚠ <b>There was deliberately no honest discriminator to add inside the walk itself.</b>
///         "These strings are kernel names" is exactly what the case the walk feeds asserts, so a
///         filter derived from the kernel folder would have made the roll call agree with itself —
///         the shape this workstream keeps having to refuse. The marker is a fact about the type
///         rather than about its contents, which is why it can be read without begging the question.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class TextureKernelSurfaceAttribute : Attribute;
