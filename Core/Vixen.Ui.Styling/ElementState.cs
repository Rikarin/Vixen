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
    ///         <see cref="UserInteracted" /> is what pairs with it to make <c>:user-invalid</c>.
    ///     </para>
    /// </remarks>
    Invalid = 1 << 12,

    /// <summary>Its value is outside the bounds it carries.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A bit for the violation and not for the compliance, which is the opposite way
    ///         round from <see cref="Valid" /> beside it — and the difference is not an
    ///         inconsistency.</b> Selectors 4 § 10.7 gives <c>:in-range</c> and <c>:out-of-range</c>
    ///         only to elements with a range, so an element with none is neither; but a range is
    ///         <i>declared</i> rather than computed, and a control that has one always answers. So
    ///         the pair collapses to one bit and its negation, <c>:read-write</c>'s arrangement,
    ///         with the same stated divergence: everything that never declared bounds is
    ///         <c>:in-range</c> here where a browser would say neither.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It could not be true at all until the control stopped clamping.</b>
    ///         <c>NumericInput</c> used to bring the value back inside the bounds in its coerce, so
    ///         the condition this bit describes could not be held for any length of time by any
    ///         route — a variant registered against it would have compiled, indexed and matched
    ///         nothing. What moved was the control: a typed or assigned number is now held and
    ///         reported, and only the arrows, the spinner and the scrub still clamp.
    ///     </para>
    /// </remarks>
    OutOfRange = 1 << 13,

    /// <summary>The user has had a go at it, rather than only having been shown it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Never a pseudo-class on its own — it is half of two.</b> CSS spells
    ///         <c>:user-valid</c> and <c>:user-invalid</c>, which are <see cref="Valid" /> and
    ///         <see cref="Invalid" /> gated on the user having touched the control, and the whole
    ///         point of having both pairs is that a form must not turn red before it has been filled
    ///         in. The matcher's state test is already <c>(state &amp; mask) == mask</c>, so the
    ///         conjunction costs one comparison and no new selector kind.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It never clears.</b> What changes back is the verdict; having been in a field is
    ///         not something that stops being true. A control that cleared it on focus loss would
    ///         make <c>:user-invalid</c> a state visible only while the caret was in the field, which
    ///         is precisely when a form should <i>not</i> be shouting.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The name is refused by the parser and reaches the compiler as a rewrite.</b>
    ///         Measured: ExCSS 4.3.2 has no literal for either word — the UTF-16 bytes are not in the
    ///         assembly — so <c>textbox:user-invalid</c> comes back as one <c>UnknownSelector</c>
    ///         covering the whole compound, exactly as <c>:where()</c> does.
    ///         <c>SelectorCompiler.TryRewrite</c> is where both are repaired, on the same scan.
    ///     </para>
    /// </remarks>
    UserInteracted = 1 << 14,

    /// <summary>A disclosure, a picker or a popover that is currently showing its contents.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The refusal this bit answers had expired rather than been satisfied.</b>
    ///         <c>:open</c> was recorded — correctly, when it was written — as a parser problem
    ///         rather than a missing bit: ExCSS 4.3.2 hands <c>select:open</c> back as one
    ///         <c>UnknownSelector</c> covering the whole compound, so no pseudo-class code ever ran
    ///         and a table entry would have been refused at compile time. That is still true of the
    ///         parser and stopped being a blocker the day <c>:user-valid</c> shipped, because the
    ///         repair it needed is the same one: <c>SelectorCompiler.TryRewrite</c> already re-reads
    ///         a selector ExCSS could not parse, and <c>:open</c> rides that scan.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On the thing that opens and not on what it opened.</b> HTML gives <c>:open</c>
    ///         to the <c>details</c>, the <c>select</c> and the <c>dialog</c> — the element an author
    ///         writes a rule for — rather than to the popup, which in this framework is an overlay in
    ///         a layer of its own and is not a descendant of the control at all. So <c>Expander</c>
    ///         and <c>SelectBase</c> are the writers, and a rule written against the list would find
    ///         a subtree the cascade reaches by a different route.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Expander</c> already wrote a bit for this and it was the wrong one.</b> Its
    ///         header carries <see cref="Checked" />, which is what makes the chevron turn — but
    ///         <c>:checked</c> is a control's <i>value</i> and a themer reaching for the open section
    ///         writes <c>expander:open</c>, not <c>expander-header:checked</c>. Both are set now, on
    ///         two different elements, and the header's is unchanged.
    ///     </para>
    /// </remarks>
    Open = 1 << 15
}
