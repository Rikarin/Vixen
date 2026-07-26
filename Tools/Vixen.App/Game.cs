// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Platform;

namespace Vixen.App;

/// <summary>What an application overrides.</summary>
/// <remarks>
///     <para>
///         Six hooks, all optional, called in a fixed order the host documents and does not vary.
///         There is deliberately nothing else on this type: no scene, no world, no content manager.
///         Those belong to <c>Vixen.Engine</c> in Phase 2, which layers on top of this rather than
///         replacing it — so an application that wants a frame loop and a window and nothing else
///         does not acquire an entity-component system by accident.
///     </para>
///     <para>
///         This is also what makes the application-framework claim testable: a
///         <c>dotnet new vixen-app</c> project derives from <see cref="Game" /> and never references
///         <c>Vixen.Engine</c>.
///     </para>
/// </remarks>
public abstract class Game : IDisposable {
    /// <summary>Everything the host built, available from <see cref="OnInitialise" /> onwards.</summary>
    /// <exception cref="InvalidOperationException">Read before the host has initialised.</exception>
    protected AppServices Services =>
        services ?? throw new InvalidOperationException(
            "Services are not available until OnInitialise. OnConfigure runs before the platform exists, "
            + "which is the point of it — it decides what the platform will be."
        );

    AppServices? services;

    /// <summary>
    ///     Decides what to build, before anything is built.
    /// </summary>
    /// <param name="config">The defaults, with the command line already applied.</param>
    /// <remarks>
    ///     Nothing exists yet — no platform, no window, no services — and that is why this hook is
    ///     separate from <see cref="OnInitialise" />. Choosing the window size is a decision that has
    ///     to happen before the window, and folding the two together would mean either configuring
    ///     something that already exists or initialising against something that does not.
    /// </remarks>
    protected internal virtual void OnConfigure(AppConfig config) { }

    /// <summary>Runs once, after the platform, window and services exist.</summary>
    protected internal virtual void OnInitialise() { }

    /// <summary>
    ///     Offered every platform event, in the order they happened, before the host acts on it.
    /// </summary>
    /// <param name="platformEvent">What happened.</param>
    /// <returns>
    ///     <see langword="true" /> to stop the host from taking its default action for this event.
    /// </returns>
    /// <remarks>
    ///     The default actions are few and all of them are ones an application legitimately wants to
    ///     intercept: a close request ends the application, and a quit ends it. Returning
    ///     <see langword="true" /> for a close request is how "save before quitting?" gets a chance
    ///     to ask.
    /// </remarks>
    protected internal virtual bool OnEvent(in PlatformEvent platformEvent) => false;

    /// <summary>Runs once per frame, before <see cref="OnRender" />.</summary>
    /// <param name="time">How long the last frame took, and how much time has passed.</param>
    /// <remarks>
    ///     Variable-step. The fixed-step accumulator that decides how many simulation steps a frame
    ///     owes belongs to <c>Vixen.Engine</c> (<c>docs/plan/03</c>), which will call this hook from
    ///     the right place once it exists.
    /// </remarks>
    protected internal virtual void OnUpdate(GameTime time) { }

    /// <summary>Runs once per frame, after <see cref="OnUpdate" />.</summary>
    /// <param name="time">How long the last frame took, and how much time has passed.</param>
    /// <remarks>
    ///     Separate from <see cref="OnUpdate" /> from the start, before there is a renderer to make
    ///     the separation matter. The split is what lets a later frame loop run several simulation
    ///     steps per rendered frame, and retrofitting it means revisiting every application that was
    ///     written against a single hook.
    /// </remarks>
    protected internal virtual void OnRender(GameTime time) { }

    /// <summary>Runs once, before the services are torn down.</summary>
    protected internal virtual void OnShutdown() { }

    /// <inheritdoc />
    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases what the application owns.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing) { }

    internal void Attach(AppServices attached) => services = attached;
}
