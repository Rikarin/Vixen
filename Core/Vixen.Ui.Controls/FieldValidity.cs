// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;

namespace Vixen.Ui.Controls;

/// <summary>The three state bits a control that takes part in constraint validation writes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Shared because the invariant is shared, and the invariant is the half that is easy
///         to write wrong.</b> Exactly one of <see cref="ElementState.Valid" /> and
///         <see cref="ElementState.Invalid" /> on a control that validates, and neither on one that
///         does not — <c>Valid</c> is a bit of its own rather than the absence of <c>Invalid</c>
///         precisely so that a plain container is neither (Selectors 4 § 10.6). A control that wrote
///         <c>Invalid</c> on the way in and forgot <c>Valid</c> on the way back would leave the
///         element matching neither pseudo-class, which a stylesheet cannot tell apart from a
///         <c>div</c>. Six controls writing that by hand is six chances to get it wrong; this is
///         one — and the count keeps moving, which is the argument.
///     </para>
///     <para>
///         ⚠ <b>Internal, and deliberately not a virtual on <c>Control</c>.</b> Putting the verdict
///         on the shared base would give every panel, toolbar and separator in the document a
///         validity — which is exactly the "every element is <c>:valid</c>" outcome the two-bit
///         arrangement exists to refuse.
///     </para>
/// </remarks>
static class FieldValidity {
    /// <summary>Writes the declaration and the verdict, and says whether the verdict moved.</summary>
    /// <param name="element">The control.</param>
    /// <param name="required">Whether a value has to be supplied.</param>
    /// <param name="valid">Whether what it holds is acceptable.</param>
    /// <returns>Whether the verdict changed, so the caller can tell the accessibility tree.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Unconditional, because the first call is the one at <c>OnCreated</c>.</b> A
    ///         control that is valid and stays valid never goes through a change, so a writer that
    ///         only published when something moved would leave it carrying neither bit for its whole
    ///         life. Writing them is free when they are already right — <see cref="UiElement.State" />
    ///         compares before it invalidates anything.
    ///     </para>
    ///     <para>
    ///         The <c>invalid</c> class rides along for the reason <c>TextField</c>'s does: the
    ///         themes select on it, and it is what tells the caller whether the verdict moved without
    ///         the caller having to keep a copy.
    ///     </para>
    /// </remarks>
    internal static bool Publish(UiElement element, bool required, bool valid) {
        var state = element.State;

        state = required ? state | ElementState.Required : state & ~ElementState.Required;

        state = valid
            ? (state | ElementState.Valid) & ~ElementState.Invalid
            : (state | ElementState.Invalid) & ~ElementState.Valid;

        element.State = state;

        return valid ? element.RemoveClass("invalid") : element.AddClass("invalid");
    }
}

/// <summary>A control that takes part in constraint validation, as a <see cref="Form" /> sees it.</summary>
/// <remarks>
///     ⚠ <b>Internal, on <see cref="FieldValidity" />'s terms.</b> The six controls that validate
///     already answer <c>IsValid</c> and <c>Revalidate()</c> publicly and each in its own words;
///     this only lets the one caller that has to ask all of them — a form walking its fields — do so
///     without a type switch that the seventh would silently miss.
/// </remarks>
interface IValidated {
    /// <summary>Whether what it holds is acceptable.</summary>
    bool IsValid { get; }

    /// <summary>Asks again and republishes the answer.</summary>
    void Revalidate();
}
