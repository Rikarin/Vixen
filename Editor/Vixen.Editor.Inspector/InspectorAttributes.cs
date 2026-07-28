// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Inspector;

/// <summary>Marks a member as something the inspector draws an editor for.</summary>
/// <remarks>
///     <para>
///         <b>Opt in, not opt out.</b> <c>Vixen.Core</c>'s <c>[EditorVisible(false)]</c> hides a
///         member from the generic property grid, which is the right default for a serialised type
///         where nearly every member is meant to be seen. An authored component is the other way
///         round: it has caches, back-references and scratch state, and an inspector that showed all
///         of them by default would be one whose author spends their time hiding things.
///     </para>
///     <para>
///         The attribute is what the generator looks for, so a type with none of these gets no
///         descriptor and costs nothing.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true)]
public sealed class InspectorAttribute : Attribute {
    /// <summary>What the row is labelled, instead of the member's own name.</summary>
    public string? Name { get; set; }

    /// <summary>Where the member sits, for a type that wants an order other than declaration order.</summary>
    /// <remarks>
    ///     Zero — the default — means "where it was declared". A non-zero order sorts before or after
    ///     everything left at zero, which is deliberately blunt: an inspector whose layout is a total
    ///     order over hand-assigned integers is one nobody can insert a field into.
    /// </remarks>
    public int Order { get; set; }
}

/// <summary>Starts a titled section before the member it is on.</summary>
/// <remarks>
///     The heading belongs to the member that follows it rather than being its own declaration,
///     because a heading with nothing under it is the state a section gets into when someone deletes
///     the last field in it — and a heading attached to a field cannot survive that field's deletion.
/// </remarks>
/// <param name="title">What the section is called.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class HeaderAttribute(string title) : Attribute {
    /// <summary>What the section is called.</summary>
    public string Title { get; } = title;
}

/// <summary>Shows the member only when another member is true.</summary>
/// <remarks>
///     <para>
///         The condition names a <c>bool</c> member of the same type, evaluated against whichever
///         object is being inspected. With several selected it is evaluated against each: a row is
///         drawn when <i>any</i> of them would show it, and the ones that would not are left out of
///         the write. Hiding a row because one of twenty objects has a flag off is how an edit
///         silently misses nineteen objects.
///     </para>
///     <para>
///         ⚠ <b>A condition is a presentation rule, not an invariant.</b> A hidden member keeps its
///         value and is still serialised; nothing resets it when the flag goes off.
///     </para>
/// </remarks>
/// <param name="member">The name of the <c>bool</c> member to test.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ShowIfAttribute(string member) : Attribute {
    /// <summary>The member tested.</summary>
    public string Member { get; } = member;
}

/// <summary>Shows the member only when another member is false.</summary>
/// <param name="member">The name of the <c>bool</c> member to test.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class HideIfAttribute(string member) : Attribute {
    /// <summary>The member tested.</summary>
    public string Member { get; } = member;
}

/// <summary>Says how a colour member should be edited.</summary>
/// <remarks>
///     A colour is the one type where the editor cannot be derived from the type: an albedo tint is
///     four channels in zero-to-one, an emissive tint is three channels with an intensity that goes
///     well past one, and a picker that offered the wrong one produces either a clipped material or a
///     meaningless alpha slider.
/// </remarks>
/// <param name="hdr">Whether values above one are meaningful, which adds an intensity control.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColorUsageAttribute(bool hdr = false) : Attribute {
    /// <summary>Whether values above one are meaningful.</summary>
    public bool Hdr { get; } = hdr;

    /// <summary>Whether the alpha channel is shown.</summary>
    public bool ShowAlpha { get; set; } = true;
}

/// <summary>Edits the member by choosing an asset of a given type.</summary>
/// <remarks>
///     The type is what the picker filters by and what a drag from the project browser is validated
///     against. It is carried as a <see cref="Type" /> rather than a GUID or a name so that renaming
///     the asset type is a compile error rather than a picker that silently offers everything.
/// </remarks>
/// <param name="assetType">What kind of asset may be chosen.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class AssetPickerAttribute(Type assetType) : Attribute {
    /// <summary>What kind of asset may be chosen.</summary>
    public Type AssetType { get; } = assetType;

    /// <summary>Whether the member may be left empty.</summary>
    public bool AllowNull { get; set; } = true;
}

/// <summary>Edits the member as a curve rather than as whatever its fields are.</summary>
/// <remarks>
///     An animation curve is a list of keys with tangents, and the generic editor for that is a list
///     of four-float rows nobody can read. The attribute is what says "this list is a shape".
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class CurveAttribute : Attribute {
    /// <summary>The lowest value the vertical axis shows.</summary>
    public float Minimum { get; set; }

    /// <summary>The highest value the vertical axis shows.</summary>
    public float Maximum { get; set; } = 1f;
}

/// <summary>Edits a string over several lines.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class MultilineAttribute : Attribute {
    /// <summary>How tall the field starts out, in lines.</summary>
    public int Lines { get; set; } = 4;
}

/// <summary>Shows the member without letting it be changed.</summary>
/// <remarks>
///     Different from having no setter: a computed diagnostic is unwritable and this is a member the
///     inspector is told not to write. Both end up read-only, and only one of them is a decision.
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
public sealed class InspectorReadOnlyAttribute : Attribute;
