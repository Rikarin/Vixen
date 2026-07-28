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
    ///         ⚠ <b>That is insurance, and it is still untestable — though no longer for the reason
    ///         first given here.</b> The original note said the tree was append-only, and it is not
    ///         any more: an element can be removed mid-event. What keeps the two indistinguishable is
    ///         narrower and worth stating exactly — <c>Parent</c> is fixed at creation and
    ///         <i>survives removal</i>, so even a handler that deletes the target's ancestor leaves
    ///         the chain a later walk would climb. Sabotaging the snapshot still fails nothing. It is
    ///         kept because it is the correct model, not because a test defends it, and reparenting
    ///         is what will finally make the difference visible.
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

    /// <summary>Delivers an event to one element and nowhere else.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="target">The element.</param>
    /// <param name="args">The event.</param>
    /// <remarks>
    ///     ⚠ <b>Only <see cref="RoutingStrategy.Direct" /> handlers run</b>, which is the difference
    ///     from <see cref="Raise" /> at the target — that invokes the bubble handlers too, because a
    ///     handler written on the element itself means the element. This does not, and the events
    ///     that use it are the ones where bubbling is wrong rather than merely unwanted: a pointer
    ///     entering an element is raised once <i>per element</i> that it entered, so a bubbling
    ///     version would tell the same ancestor several times about one movement.
    /// </remarks>
    public static void Direct<T>(UiElement target, T args) where T : UiEvent {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(args);

        args.Source ??= target;
        args.Phase = RoutingPhase.Target;
        target.Invoke(args, RoutingStrategy.Direct);
    }
}
