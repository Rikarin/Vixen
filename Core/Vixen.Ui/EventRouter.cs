// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>Walks an event down to an element and back out.</summary>
public static class EventRouter {
    /// <summary>Routes an event: capture from the root, then the target, then bubble back out.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="target">The element the event is about.</param>
    /// <param name="args">The event.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The route is taken before any handler runs</b>, so that "this event goes to these
    ///         elements" is a fact rather than a race: a handler is entitled to change the tree, and
    ///         an event recomputing its path as it went would visit an element that had just been
    ///         removed or skip one whose parent had changed underneath.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That is insurance, and it is currently untestable.</b> The tree is append-only
    ///         and <c>Parent</c> is fixed at creation, so no handler can yet change an ancestor
    ///         chain and snapshotting is indistinguishable from walking as you go — sabotaging it
    ///         fails nothing. It is kept because it is the correct model and because element removal
    ///         is owed, not because a test defends it. Said here so that nobody reads the paragraph
    ///         above as a covered claim.
    ///     </para>
    ///     <para>
    ///         The target is invoked once, with both its <see cref="RoutingStrategy.Direct" /> and
    ///         its <see cref="RoutingStrategy.Bubble" /> handlers — which is what a handler on the
    ///         element itself means by "bubble" and would otherwise be a surprising silence.
    ///     </para>
    /// </remarks>
    public static void Raise<T>(UiElement target, T args) where T : UiEvent {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(args);

        var route = new List<UiElement>();
        for (var element = target; element is not null; element = element.Parent) {
            route.Add(element);
        }

        args.Source ??= target;

        args.Phase = RoutingPhase.Capture;
        for (var i = route.Count - 1; i > 0; i--) {
            route[i].Invoke(args, RoutingStrategy.Capture);
        }

        args.Phase = RoutingPhase.Target;
        target.Invoke(args, RoutingStrategy.Capture);
        target.Invoke(args, RoutingStrategy.Direct);
        target.Invoke(args, RoutingStrategy.Bubble);

        args.Phase = RoutingPhase.Bubble;
        for (var i = 1; i < route.Count; i++) {
            route[i].Invoke(args, RoutingStrategy.Bubble);
        }
    }
}
