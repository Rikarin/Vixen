// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.HotReload;

namespace Vixen.Editor.Inspector;

/// <summary>Registering a <c>.vxml</c> component as the inspector for a type.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P4's other half.</b> <see cref="MarkupBinding" /> joins a built tree to what it
///         is editing; this is what builds the tree, and what puts it back together after a
///         <c>dotnet watch</c> has replaced the <c>Build</c> body the markup compiled to.
///     </para>
///     <para>
///         ⚠ <b>Mounted through the reload host rather than built directly, and that is the whole
///         difference between "markup works" and "markup is the authoring path".</b> A declarative
///         layout you have to restart the editor to see is a slower way to write C#. What makes it
///         worth adopting is the loop: change the file, the panel is different a second later.
///     </para>
///     <para>
///         ⚠ <b>A reload throws away the elements, so the binding has to be made again.</b>
///         <c>ReloadComponents</c> re-runs <c>Build</c> on the same component object — the fields
///         survive because the object does and the elements do not, which is
///         <see cref="HotReloadHost" />'s stated bargain. Every element a <c>binding-path</c> was
///         joined to is therefore gone, and a panel that did not re-bind would come back from a
///         reload showing rows that edit nothing.
///     </para>
/// </remarks>
public static class MarkupInspector {
    /// <summary>What each mounted markup inspector is bound to, so a reload can bind it again.</summary>
    /// <remarks>
    ///     Weak on the component, because a panel that was closed must not be kept alive by the
    ///     bookkeeping that would have reloaded it.
    /// </remarks>
    static readonly ConditionalWeakTable<Component, EditTarget> Bound = [];

    /// <summary>The hosts already subscribed to, so one subscription is made per host.</summary>
    /// <remarks>
    ///     ⚠ <b>A weak table rather than a set, and the difference is a leaked document per host.</b>
    ///     A strong static set would hold every editor's shell document for the life of the process —
    ///     invisible in an application that opens one and fatal in a test run that opens forty.
    /// </remarks>
    static readonly ConditionalWeakTable<HotReloadHost, object> Watched = [];

    /// <summary>Builds a markup component as a type's inspector.</summary>
    /// <typeparam name="T">The component the <c>.vxml</c> compiled to.</typeparam>
    /// <param name="host">
    ///     The document's reload host, or <see langword="null" /> for an editor without one — in which
    ///     case the panel still works and simply does not follow an edit to the markup.
    /// </param>
    /// <returns>What <c>CustomInspector.Build</c> takes.</returns>
    public static Action<UiElement, EditTarget> Of<T>(HotReloadHost? host) where T : Component, new() =>
        (body, target) => {
            ArgumentNullException.ThrowIfNull(body);
            ArgumentNullException.ThrowIfNull(target);

            var component = host is null
                ? BuildContext.Build<T>(body.Document, body)
                : host.Mount<T>(body);

            Bound.AddOrUpdate(component, target);
            MarkupBinding.Bind(component.Root, target);

            if (host is not null) {
                Watch(host);
            }
        };

    /// <summary>Re-binds every markup inspector this host holds, after it has rebuilt them.</summary>
    static void Watch(HotReloadHost host) {
        lock (Watched) {
            if (Watched.TryGetValue(host, out _)) {
                return;
            }

            Watched.Add(host, new object());
        }

        // ⚠ The handler captures the host it is stored on, which is a cycle rather than a leak: the
        // collector traces it and frees both together the moment nothing else points at the host.
        host.Reloaded += _ => {
            foreach (var component in host.Components) {
                if (Bound.TryGetValue(component, out var target)) {
                    MarkupBinding.Bind(component.Root, target);
                }
            }
        };
    }
}
