// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Composition;

/// <summary>What a capitalised markup tag is allowed to name.</summary>
/// <remarks>
///     <para>
///         Two things answer to it and there will not be a third: a <see cref="Component" />, which
///         is what a <c>.vxml</c> compiles to, and a <see cref="UiElement" />, which is what the
///         control library is made of. <c>&lt;ProgressBar /&gt;</c> and <c>&lt;Counter /&gt;</c> are
///         written the same way and mean different things, and the difference is settled by C#
///         overload resolution at the use site rather than by the markup compiler — which resolves
///         no types at all, deliberately.
///     </para>
///     <para>
///         ⚠ <b>An empty interface, and it is carrying its weight.</b>
///         <see cref="BuildContext.Child{T}" /> could take <c>where T : new()</c> and sort the two
///         out at runtime; the constraint is what makes <c>&lt;Stopwatch /&gt;</c> — a real type
///         that is neither — a compile error on the tag the author wrote, which is the whole bargain
///         the markup channel is built on.
///     </para>
/// </remarks>
public interface IComposable;
