// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Editor.Plugin;

/// <summary>Everything one plugin added, and how to take each of it back out.</summary>
/// <remarks>
///     <para>
///         <b>Unloading is undoing, and this is the undo stack.</b> A collectible
///         <see cref="PluginLoadContext" /> only collects once nothing outside it refers to anything
///         inside it — and a command whose <c>Run</c> is a lambda over the plugin's own state is
///         precisely such a reference, held by the editor's command registry. So a plugin that
///         registered five things and was then unloaded without them being removed does not leak
///         five entries; it leaks its whole assembly, permanently, with no error anywhere.
///     </para>
///     <para>
///         ⚠ <b>Undone in reverse order.</b> The same reason an undo stack is: a plugin that
///         registered a panel and then a menu entry naming its command would otherwise have the
///         command taken away while a menu still names it. The menu builder survives that — an id
///         nothing registered is skipped — but the general case does not, and reverse order costs
///         nothing.
///     </para>
///     <para>
///         ⚠ <b>A failing undo does not stop the rest.</b> One that threw halfway through would
///         leave the plugin half-registered <i>and</i> its context uncollectable, which is strictly
///         worse than the thing it was complaining about. Failures are collected into
///         <see cref="Failures" /> and reported.
///     </para>
/// </remarks>
public sealed class PluginRegistrations : IDisposable {
    readonly List<Action> undo = [];
    readonly List<Exception> failures = [];

    /// <summary>How many things are registered.</summary>
    public int Count => undo.Count;

    /// <summary>What this plugin asked to have called once a frame. See <see cref="PluginContext.OnUpdate" />.</summary>
    /// <remarks>
    ///     Held here rather than on the host so that unloading takes them out with everything else —
    ///     a per-frame callback left behind is not merely a wasted call, it is a delegate over the
    ///     plugin's own state held by the editor's loop, which is the reference that stops its
    ///     assembly being collected.
    /// </remarks>
    public List<Action<TimeSpan>> Updates { get; } = [];

    /// <summary>
    ///     What this plugin asked to be told before the device goes. See
    ///     <see cref="PluginContext.OnDeviceLost" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Held here for <see cref="Updates" />' reason and for one of its own.</b> A release
    ///     callback is a delegate over the plugin's own device objects, so a plugin that has been
    ///     unloaded while the editor still has a device must not be called on the <i>next</i> window
    ///     loss: everything it would have destroyed went with its scope, and what is left is a
    ///     delegate holding its assembly loaded.
    /// </remarks>
    public List<Action<IGraphicsDevice>> DeviceLost { get; } = [];

    /// <summary>What went wrong while undoing, if anything.</summary>
    public IReadOnlyList<Exception> Failures => failures;

    /// <summary>Records something to undo when the plugin goes away.</summary>
    /// <param name="action">How to take it back out.</param>
    public void Add(Action action) {
        ArgumentNullException.ThrowIfNull(action);
        undo.Add(action);
    }

    /// <summary>Takes everything back out.</summary>
    /// <remarks>Safe to call twice: the second time there is nothing left to undo.</remarks>
    public void Dispose() {
        // Before the undo list, because an undo that removes an update would otherwise be walking a
        // list this is about to clear anyway — and because a plugin half-way through unloading must
        // not be called again from a frame that lands mid-teardown.
        Updates.Clear();

        // And the release callbacks with them, for the same reason.
        //
        // ⚠ Belt and braces rather than the mechanism, and the same is true of the line above:
        // `PluginContext.OnDeviceLost` and `OnUpdate` each record a removal in the undo list, so both
        // of these are already empty by the end of the loop below — sabotaging this line leaves every
        // test green. What it covers is the route neither method owns: these lists are public and a
        // host that appends to one directly gets the same guarantee.
        DeviceLost.Clear();

        for (var index = undo.Count - 1; index >= 0; index--) {
            try {
                undo[index]();
            } catch (Exception exception) {
                failures.Add(exception);
            }
        }

        undo.Clear();
    }
}
