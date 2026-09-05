// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vixen.Ui.Composition;

namespace Vixen.Ui.Controls;

/// <summary>What the control library adds to the vocabulary a <c>.vxml</c> is written in.</summary>
/// <remarks>
///     <para>
///         Nothing here is called by hand. <c>Vixen.Ui</c>'s event table knows the events
///         <c>Vixen.Ui</c> raises, which are pointer gestures; a control's activation is
///         <see cref="ClickEvent" /> and is raised by four different things, only one of which is a
///         tap. So <c>on:click</c> has to mean something different on a control, and this is where
///         that is said.
///     </para>
///     <para>
///         ⚠ <b>Both events, on every element, rather than one chosen by the element's type.</b>
///         Choosing was the shape this had until it was found to be undecidable at build time: only
///         <see cref="ButtonBase" /> and <c>ColorSwatch</c> raise a <see cref="ClickEvent" />, so
///         <c>&lt;Card on:click&gt;</c>, <c>&lt;Panel on:click&gt;</c> and every other one of the
///         thirty-odd plain <see cref="Control" />s bound a handler that could never run — the
///         silent failure the type test was meant to avoid, moved rather than removed. Subscribing
///         to both is also what the DOM does, where <c>click</c> is one event that a press and a
///         keypress both produce.
///     </para>
///     <para>
///         ⚠ <b>What keeps one press from counting twice is <see cref="Control.RaisesActivation" />,
///         asked of the element the tap started on and every element between that and the listener.</b>
///         A control that reports its own activation has already told the handler, so its tap is
///         left alone; anything else has not, so its tap <i>is</i> the click. The walk is needed
///         because a button's label is a child element and the hit test lands on it — and it is
///         what makes a <c>&lt;Card on:click&gt;</c> containing a button hear one press once,
///         through the activation that bubbles rather than through the tap the button marked
///         handled.
///     </para>
/// </remarks>
static class ControlMarkup {
    /// <summary>
    ///     Runs when anything in this assembly is first touched, which — for a component whose
    ///     markup names a control — is <c>ctx.Child&lt;IconButton&gt;</c>, several statements before
    ///     the <c>ctx.On</c> that needs this to have happened.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>That sentence is true of <c>on:</c> and is not true of everything registered
    ///     below.</b> It rests on the markup naming a capitalised tag, and <c>help=</c> and
    ///     <c>context-menu=</c> are legal on a <c>&lt;div&gt;</c> — so a <c>.vxml</c> of plain boxes
    ///     with a tooltip on one of them reads this seam having touched nothing in this assembly.
    ///     <c>on:</c> survives that because the event names it needs are <c>Vixen.Ui</c>'s own and
    ///     the entries here only sharpen them; <c>BuildContext.Describes</c> and
    ///     <c>Contextualises</c> have nothing underneath. <c>ControlTheme.Install</c> is what
    ///     touches the assembly in practice, and <c>BuildContext</c>'s failure message now says so.
    /// </remarks>
    [ModuleInitializer]
    [SuppressMessage(
        "Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification =
            "The rule's concern is a library doing observable work on load. This adds two entries to "
            + "a lookup table and touches nothing else — the load-order-sensitive case the rule "
            + "protects against is what it exists to avoid, since the alternative is asking every "
            + "consumer to call a registration method the generated code cannot call for them."
    )]
    internal static void Register() {
        BuildContext.Subscribe(
            "click",
            (element, handler, how) => {
                how.Listen<ClickEvent>(element, (_, args) => handler(args));

                how.Listen<TapEvent>(
                    element,
                    (listener, args) => {
                        if (!Reported(listener, args.Source)) {
                            handler(args);
                        }
                    }
                );
            }
        );

        BuildContext.Subscribe(
            "dblclick",
            (element, handler, how) => {
                how.Listen<ClickEvent>(
                    element,
                    (_, args) => {
                        if (args.Count >= 2) {
                            handler(args);
                        }
                    }
                );

                how.Listen<TapEvent>(
                    element,
                    (listener, args) => {
                        if (args.Count >= 2 && !Reported(listener, args.Source)) {
                            handler(args);
                        }
                    }
                );
            }
        );

        // ⚠ Here rather than in `Vixen.Ui` for `click`'s reason exactly: what counts as submitting
        // is a control's decision — Enter is a line break in a `TextArea` and a submission in every
        // other field — and `Vixen.Ui` has no fields. It is the moment `bind:Value.submit` commits
        // on, and the only route by which `TextField.Submitted` was ever reachable from markup.
        BuildContext.Subscribe(
            "submit",
            (element, handler, how) => how.Listen<SubmitEvent>(element, (_, args) => handler(args))
        );

        // ⚠ `help` is the accessible description first and the hover box second, which is why the
        // whole of it is `Tooltip.Attach` rather than a `Text` written somewhere. Attach wires
        // `AccessibleRelation.DescribedBy`, so the sentence is in `AccessibleDescription` — read on
        // demand, whether or not anything is hovering — and a markup spelling that drew a box and
        // told nobody would be the accessibility bug the C# API was careful not to be.
        //
        // The tooltip is a root child because every overlay is: the draw list is document order, so
        // one nested inside the button it describes would be clipped by every `overflow: hidden`
        // between them.
        BuildContext.Describes(target => {
            var tip = target.Document.Root.Add<Tooltip>();
            tip.Attach(target);

            return tip;
        });

        // ⚠ The menu arrives as a `UiElement` because `Vixen.Ui` has no name for a menu, so this is
        // where the type is checked — and it throws rather than doing nothing, because an attribute
        // that silently attached nothing is a right-click that reads as a broken panel.
        BuildContext.Contextualises((target, menu) => {
            if (menu is not ContextMenu context) {
                throw new ArgumentException(
                    $"'context-menu' wants a {nameof(ContextMenu)} and was given a "
                    + $"{menu.GetType().Name}. A menu opened at the pointer is a distinct type from a "
                    + $"{nameof(Menu)} dropped beside an anchor, because the placement rule differs.",
                    nameof(menu)
                );
            }

            context.Attach(target);
        });

        // ⚠ The write-back leg of a bound open state, and its absence is what made `bind:IsOpen`
        // look like the missing feature. `IsOpen` is deliberately not a `[UiProperty]` — see
        // `Overlay` — so `change:IsOpen`, which is the ordinary way a control tells a model it
        // changed its own mind, cannot name it. `OpenChangedEvent` was raised all along, by
        // `Overlay.Restate` and by `Disclosure`, and no `.vxml` in the tree could hear it: a name
        // absent from this table is an `on:` the binder rejects. Without it a state binding is
        // one-way in, which is an overlay the user closes and the model reopens on the next flush.
        BuildContext.Subscribe(
            "openchanged",
            (element, handler, how) => how.Listen<OpenChangedEvent>(element, (_, args) => handler(args))
        );
    }

    /// <summary>Whether the activation this tap produced has already been reported to the handler.</summary>
    /// <param name="listener">The element the <c>on:click</c> was written on.</param>
    /// <param name="source">Where the tap landed, which for a button is its label part.</param>
    /// <returns>Whether a <see cref="ClickEvent" /> is on its way, or has already been.</returns>
    /// <remarks>
    ///     ⚠ <b>Stops at <paramref name="listener" />, and that is the whole of the rule.</b> An
    ///     activating control <i>above</i> the listener is not this tap's business — the listener is
    ///     inside it and the activation will not reach down — so a walk to the root would silence a
    ///     perfectly good <c>&lt;div on:click&gt;</c> that happens to live inside a menu item.
    /// </remarks>
    static bool Reported(UiElement listener, UiElement? source) {
        for (var element = source; element is not null; element = element.Parent) {
            if (element is Control { RaisesActivation: true }) {
                return true;
            }

            if (ReferenceEquals(element, listener)) {
                break;
            }
        }

        return false;
    }
}
