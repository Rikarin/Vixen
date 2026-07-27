// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.HotReload;

/// <summary>Marks a component member whose value should survive being replaced.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is not for the common case, and saying which case it <i>is</i> for matters.</b>
///         Re-running a component's <c>Build</c> keeps the component object, so its fields — its
///         signals above all — survive by construction and need no attribute. What does not survive
///         is a <i>replacement</i>: an edit .NET calls rude, a type that has to be recreated, a
///         reload that constructs a fresh instance. Then the new object starts from its own field
///         initialisers, and this says what to carry over.
///     </para>
///     <para>
///         Carried by name and by name only. Two instances of two versions of a type share nothing
///         else — not a field slot, not a token, not an order — and matching on anything more
///         clever would move a value onto whatever happened to be in the same position.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class HotReloadStateAttribute : Attribute;
