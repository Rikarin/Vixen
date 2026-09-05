// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The transient conditions a selector can ask about.</summary>
/// <remarks>
///     Kept as flags on the element rather than as a query into the input system, because the
///     cascade has to be able to answer "what would this element look like if it were hovered"
///     without anything actually being hovered — which is what a transition needs in order to know
///     what it is transitioning to.
/// </remarks>
[Flags]
public enum ElementState : uint {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The pointer is over it.</summary>
    Hover = 1 << 0,

    /// <summary>It is being pressed.</summary>
    Active = 1 << 1,

    /// <summary>It has keyboard focus.</summary>
    Focus = 1 << 2,

    /// <summary>It has focus and the focus should be shown — keyboard navigation rather than a click.</summary>
    FocusVisible = 1 << 3,

    /// <summary>It does not accept input.</summary>
    Disabled = 1 << 4,

    /// <summary>It is a checkbox, radio or toggle that is on.</summary>
    Checked = 1 << 5,

    /// <summary>Focus is on it or on something inside it.</summary>
    FocusWithin = 1 << 6,

    /// <summary>It can be read and selected but not edited.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A state and not a class, which reverses an argument <c>TextField.ReadOnly</c>
    ///         used to make.</b> That remark said this enum is "the set of <i>transient</i>
    ///         conditions a selector asks about" and that read-only is a mode a field was put into —
    ///         but <see cref="Disabled" /> and <see cref="Checked" /> are modes on exactly the same
    ///         terms, and both have been here since the enum was written. What separates a state from
    ///         a class is not how long it lasts; it is whether CSS spells it as a pseudo-class, and
    ///         <c>:read-only</c> is one.
    ///     </para>
    ///     <para>
    ///         ⚠ The <c>read-only</c> <i>class</i> is still written beside it, deliberately: the
    ///         editor's themes select on it, and a state bit that quietly replaced it would restyle
    ///         every inspector field in the same commit as a variant nobody had used yet.
    ///     </para>
    /// </remarks>
    ReadOnly = 1 << 7,

    /// <summary>It is showing its placeholder because it has no value of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, which is what separates it from "empty".</b> CSS Selectors 4 § 10.4
    ///     matches an input that is <i>currently displaying</i> placeholder text — so a field with no
    ///     value and no placeholder to show is not it, and neither is one with a placeholder and a
    ///     value. The control already carried an <c>empty</c> class for the first half alone, and a
    ///     variant compiled against that would have matched every empty field in the document.
    /// </remarks>
    PlaceholderShown = 1 << 8,

    /// <summary>It is a checkbox or a progress bar whose value is neither of the two answers.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a third value of <see cref="Checked" />, and the enum shape is what says so.</b> A
    ///     tri-state checkbox that is indeterminate is <i>not</i> checked — CSS matches
    ///     <c>:indeterminate</c> and not <c>:checked</c> — so the two bits are independent and a
    ///     control that set both would make <c>checked:</c> apply to a box showing a dash.
    /// </remarks>
    Indeterminate = 1 << 9,

    /// <summary>A value has to be supplied before the control is acceptable.</summary>
    /// <remarks>
    ///     ⚠ <b>A declaration and not a verdict, which is why it is separable from
    ///     <see cref="Valid" />.</b> It says the field is mandatory; it says nothing about whether it
    ///     has been filled in. <c>:optional</c> is compiled as its negation rather than given a bit —
    ///     <see cref="ReadOnly" />'s arrangement, and for the same reason: two bits meaning opposite
    ///     halves of one fact disagree the first time a control writes one and forgets the other.
    /// </remarks>
    Required = 1 << 10,

    /// <summary>Its value satisfies whatever rule it carries.</summary>
    /// <remarks>
    ///     ⚠ <b>A bit of its own rather than the absence of <see cref="Invalid" />, which is the one
    ///     place this enum departs from the <c>:read-write</c> arrangement beside it — deliberately,
    ///     because CSS departs the same way.</b> Selectors 4 § 10.6 matches <c>:valid</c> and
    ///     <c>:invalid</c> only on elements that <i>take part in constraint validation</i>: a plain
    ///     container is neither, and a negation would have made every element in the document
    ///     <c>:valid</c>. So a control that validates writes exactly one of the two and everything
    ///     else writes neither — which <c>ElementStateBitTests</c> asserts as an invariant rather
    ///     than as two separate positives, because the failure worth catching is both at once.
    /// </remarks>
    Valid = 1 << 11,

    /// <summary>Its value breaks a rule it carries.</summary>
    /// <remarks>
    ///     <para>See <see cref="Valid" /> for why these are two bits and not one.</para>
    ///     <para>
    ///         ⚠ <b>There is no <c>UserInteracted</c> beside it, and the reason is a dependency
    ///         rather than a design.</b> CSS pairs these with <c>:user-valid</c> and
    ///         <c>:user-invalid</c> — the same verdict, shown only once the user has had a go — which
    ///         is one more bit and a two-bit mask, since the matcher's state test is already
    ///         <c>(state &amp; mask) == mask</c>. ⚠ <b>Measured: ExCSS 4.3.2 does not know either
    ///         name</b>, and hands the whole compound back as an <c>UnknownSelector</c>, exactly as
    ///         it does for <c>:open</c> and <c>:autofill</c>. So the bit and its writer were written
    ///         and then taken back out: nothing could ever have selected on them, and a state
    ///         nothing selects on is the defect this family was filed to stop.
    ///     </para>
    /// </remarks>
    Invalid = 1 << 12
}
