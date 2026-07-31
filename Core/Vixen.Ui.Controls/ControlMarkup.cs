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
///         ⚠ <b>Decided per element rather than per name.</b> A <c>&lt;div on:click&gt;</c> still
///         wants the tap: an element that is not a control raises no <see cref="ClickEvent" />, so
///         binding one to it would be a handler that never runs — silently, which is the failure
///         worth spending a type test to avoid. The test is one <c>is</c> at build time, not per
///         event.
///     </para>
/// </remarks>
static class ControlMarkup {
    /// <summary>
    ///     Runs when anything in this assembly is first touched, which — for a component whose
    ///     markup names a control — is <c>ctx.Child&lt;IconButton&gt;</c>, several statements before
    ///     the <c>ctx.On</c> that needs this to have happened.
    /// </summary>
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
            (element, handler, strategy) => {
                if (element is Control) {
                    element.AddHandler<ClickEvent>((_, args) => handler(args), strategy);
                    return;
                }

                element.AddHandler<TapEvent>((_, args) => handler(args), strategy);
            }
        );

        BuildContext.Subscribe(
            "dblclick",
            (element, handler, strategy) => {
                if (element is Control) {
                    element.AddHandler<ClickEvent>(
                        (_, args) => {
                            if (args.Count >= 2) {
                                handler(args);
                            }
                        },
                        strategy
                    );

                    return;
                }

                element.AddHandler<TapEvent>(
                    (_, args) => {
                        if (args.Count >= 2) {
                            handler(args);
                        }
                    },
                    strategy
                );
            }
        );
    }
}
