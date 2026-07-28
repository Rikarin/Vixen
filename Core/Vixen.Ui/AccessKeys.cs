// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>An access key was pressed and this element is what it names.</summary>
/// <remarks>
///     <para>
///         <b>Raised on the element rather than acted on by the document</b>, because what an access
///         key <i>does</i> is the control's business: a button presses, a text field takes the focus
///         and does nothing else, a tab selects itself. The document decides <i>which</i> element,
///         which is the part no control can work out for itself.
///     </para>
///     <para>
///         ⚠ It bubbles, like everything else. A panel that wants to hear its children's access keys
///         — a menu closing after one of its items runs — listens without the item knowing.
///     </para>
/// </remarks>
public sealed class AccessKeyEvent : UiEvent {
    /// <summary>The character that was pressed, upper-cased.</summary>
    public char Key { get; init; }
}

/// <summary>The marker convention for writing an access key into a label.</summary>
/// <remarks>
///     <para>
///         <c>_Save</c> is Save with an access key of <c>S</c>, and <c>__</c> is a literal
///         underscore. Underscore rather than ampersand: this framework's labels arrive through
///         markup, where <c>&amp;</c> already means something, and every mistake with that convention
///         is an entity that half-parses.
///     </para>
///     <para>
///         ⚠ <b>Nothing calls this automatically.</b> Setting <see cref="UiElement.AccessKey" /> is
///         explicit, and a control that wanted <c>Label = "_Save"</c> to mean both the text and the
///         key would have to strip the marker itself — which is a decision about every existing
///         label in every application, not a helper. This is here so that the convention is written
///         down once rather than in each caller that wants it.
///     </para>
/// </remarks>
public static class AccessKey {
    /// <summary>The marker that precedes the access key in a label.</summary>
    public const char Marker = '_';

    /// <summary>Pulls the access key out of a label and gives back the text to draw.</summary>
    /// <param name="label">The label, with or without a marker in it.</param>
    /// <param name="key">Receives the key, upper-cased, or <c>'\0'</c> if the label has none.</param>
    /// <returns>The label with the marker removed.</returns>
    public static string Parse(string? label, out char key) {
        key = '\0';

        if (string.IsNullOrEmpty(label) || label.IndexOf(Marker, StringComparison.Ordinal) < 0) {
            return label ?? "";
        }

        var text = new System.Text.StringBuilder(label.Length);

        for (var i = 0; i < label.Length; i++) {
            if (label[i] != Marker) {
                text.Append(label[i]);
                continue;
            }

            // ⚠ A doubled marker is a literal one and does *not* also set the key. Without that
            // rule, `snake__case` would silently claim `c` as an access key, and the collision would
            // only appear when somebody held Alt.
            if (i + 1 < label.Length && label[i + 1] == Marker) {
                text.Append(Marker);
                i++;
                continue;
            }

            // The first marked character wins. A second one in the same label is a mistake, and
            // taking the first is both what every toolkit does and the one that can be predicted.
            if (key == '\0' && i + 1 < label.Length) {
                key = char.ToUpperInvariant(label[i + 1]);
            }
        }

        return text.ToString();
    }
}

public sealed partial class UiDocument {
    /// <summary>Activates whatever an access key names, if anything does.</summary>
    /// <param name="key">The character, in any case.</param>
    /// <returns>Whether an element took it.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Within the focus scope, not the whole document.</b> A modal dialog is a focus scope,
    ///         and a dialog whose <c>_Save</c> could be answered by a toolbar button in the window
    ///         behind it is not modal. This is the same scope <see cref="MoveFocus(FocusDirection)" />
    ///         uses, for the same reason.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Repeats cycle rather than re-firing.</b> Two elements sharing a key is ordinary —
    ///         a form with two <c>_Name</c> fields in different groups — and CSS-shaped conventions
    ///         have no answer for it. Pressing the key again moves to the next one, which is what
    ///         every toolkit does and what makes a collision a small annoyance rather than one of the
    ///         two controls being unreachable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Disabled and hidden elements are skipped</b>, and a hidden one is skipped by
    ///         asking the layout rather than the style: an element inside a collapsed panel has no
    ///         box, and reading <c>display</c> off it alone would find a visible element inside an
    ///         invisible parent. The price is that this reads the <i>last</i> layout — an access key
    ///         pressed before the document has ever been laid out finds nothing, which is the same
    ///         limitation arrow navigation has and for the same reason.
    ///     </para>
    /// </remarks>
    public bool InvokeAccessKey(char key) {
        var wanted = char.ToUpperInvariant(key);
        if (wanted == '\0') {
            return false;
        }

        var candidates = new List<UiElement>();
        Collect(Scope(), wanted, candidates);

        if (candidates.Count == 0) {
            return false;
        }

        // Where the focus already sits on one of them, the next press takes the one after it.
        var from = Focused is null ? -1 : candidates.IndexOf(Focused);
        var target = candidates[(from + 1) % candidates.Count];

        // ⚠ No `if (target.Focusable)` around this, and an earlier draft had one — `Focus` already
        // refuses an element that cannot hold the focus and answers false. The guard read as a rule
        // and was insurance against a method that does not need insuring; sabotaging it failed no
        // test, because there was nothing there to fail.
        Focus(target);

        var args = new AccessKeyEvent { Key = wanted };
        target.Raise(args);

        return true;
    }

    /// <summary>Everything under a subtree that answers to a key, in tree order.</summary>
    static void Collect(UiElement element, char key, List<UiElement> into) {
        // ⚠ Disabled is read off the *style state* rather than off a control property, because this
        // assembly has no controls in it. `Control.Disabled` sets `ElementState.Disabled` so that
        // `:disabled` works, and that is exactly the fact worth reading here — an access key that
        // pressed a greyed-out button would be the one way past being disabled.
        if (char.ToUpperInvariant(element.AccessKey) == key && !element.State.HasFlag(ElementState.Disabled)) {
            into.Add(element);
        }

        foreach (var child in element.Children) {
            // ⚠ A child with no box is not searched, and neither is anything inside it. That is the
            // same test `DrawListBuilder` uses for "on screen" — flexbox reports `display: none` as a
            // zero box — so a collapsed panel's access keys go away with the panel, which is the
            // whole point of collapsing it. Checked per child rather than on entry so that the scope
            // itself is always searched.
            if (child.Width > 0f && child.Height > 0f) {
                Collect(child, key, into);
            }
        }
    }

    /// <summary>Whether a key press is an access key, and which character it names.</summary>
    /// <remarks>
    ///     ⚠ <b>Alt exactly, not Alt-and-whatever.</b> Ctrl-Alt-S is somebody's shortcut and
    ///     Alt-Shift-S is another; answering to either would take a key an application had already
    ///     bound. This is the same argument <see cref="KeyEvent.Has" /> makes and the same test.
    /// </remarks>
    static bool TryAccessKey(KeyEvent args, out char key) {
        key = '\0';

        if (args.Action != KeyAction.Pressed || !args.Has(ModifierKeys.Alt)) {
            return false;
        }

        if (args.Key is >= InputKey.A and <= InputKey.Z) {
            key = (char) ('A' + (args.Key - InputKey.A));
            return true;
        }

        if (args.Key is >= InputKey.Number1 and <= InputKey.Number9) {
            key = (char) ('1' + (args.Key - InputKey.Number1));
            return true;
        }

        if (args.Key == InputKey.Number0) {
            key = '0';
            return true;
        }

        return false;
    }
}
