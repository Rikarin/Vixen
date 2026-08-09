// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>One declaration read out of a <c>style="…"</c> attribute, before it is interned.</summary>
/// <param name="Property">The property, as a stylesheet writes it — <c>width</c>, <c>flex-grow</c>.</param>
/// <param name="Value">Its value, with any <c>!important</c> already taken off.</param>
/// <param name="Important">Whether the declaration carried <c>!important</c>.</param>
/// <remarks>
///     ⚠ <b>Strings rather than a <see cref="Declaration" />, and that is the point of the type.</b> A
///     <c>Declaration</c> holds indices into a <see cref="NameTable" />, so producing one commits the
///     caller to a particular engine's tables at parse time. The one consumer that exists —
///     <c>BuildContext</c>'s <c>style</c> attribute — needs to hand what it read to
///     <c>UiElement.SetStyle</c>, which does its own interning and also has to be told which
///     properties to <i>remove</i>; a name it cannot spell is no use for that.
/// </remarks>
public readonly record struct InlineDeclaration(string Property, string Value, bool Important);
