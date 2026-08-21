// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Engine.Frames;

namespace Vixen.Editor.SceneView;

/// <summary>One in-editor play session: the loop it steps, and everything that has to be undone.</summary>
/// <remarks>
///     <para>
///         <b>The seam [11](../../../docs/plan/11-editor.md) § "Play mode runs a system graph" left
///         open.</b> An <see cref="EngineLoop" />'s default registration is behaviours, coroutines
///         and transforms; every other system a game runs — physics, audio, navigation, the render
///         extractions — is added by that game's own <c>OnInitialise</c> against a host service.
///         There is no <c>AddStandardSystems</c> and nothing in a project says which of its systems a
///         scene wants, so the editor cannot reproduce a game's frame by scheduling harder. What it
///         *can* do is let whoever owns a service add the systems that need it, which is what an
///         <see cref="IPlaySystems" /> contribution is.
///     </para>
///     <para>
///         ⚠ <b>Per session, and that is the whole reason this object exists rather than a service in
///         <c>PluginServices</c>.</b> A <c>PhysicsScene</c> is created when Play is pressed and
///         destroyed when Stop is, because a body that simulates while somebody drags a gizmo is a
///         scene that edits itself — and because the entities its bodies live on must be *inside* the
///         snapshot's restore, so that stopping takes them away with everything else a session made.
///         Publishing that scene into a bag which has no removal would hand every later reader a
///         handle to a disposed native world.
///     </para>
///     <para>
///         ⚠ <b><see cref="Owns" /> and <see cref="OnStop" /> run before the world is restored.</b>
///         <c>PlayModeController.Stop</c> releases the session while its entities are still alive, so
///         a contributor destroying bodies has something to destroy — and so the leak comparison that
///         follows measures what the session leaked rather than what it had not got round to freeing
///         yet.
///     </para>
/// </remarks>
public sealed class PlaySession {
    readonly List<Action> undo = [];
    readonly List<string> running = [];
    readonly Dictionary<Type, object> provided = [];

    /// <summary>The graph this session steps.</summary>
    /// <remarks>
    ///     Systems are added with <c>EngineLoop.Add</c> or through an extension such as
    ///     <c>PhysicsSystems.AddPhysics</c>, exactly as a game's <c>OnInitialise</c> would. A system's
    ///     <c>[UpdateInGroup]</c> phase is what orders it; nothing here re-decides that.
    /// </remarks>
    public EngineLoop Loop { get; }

    /// <summary>The world it runs over, which is the one being edited.</summary>
    public World World { get; }

    /// <summary>What the contributors said they added, in the order they said it.</summary>
    /// <remarks>
    ///     ⚠ <b>Read out to the person pressing Play, and that is the point of it.</b> Doc 11's rule
    ///     for this feature is "run a whole graph of a named set, and name what is missing" — a Play
    ///     button that runs most of a frame and says nothing moves the failure out of the editor,
    ///     where it is, and into the user's game, where it is not. The complement is
    ///     <c>PlayModeController.Refused</c>.
    /// </remarks>
    public IReadOnlyList<string> Running => running;

    /// <summary>Builds a session over a loop.</summary>
    /// <param name="loop">The graph.</param>
    /// <param name="world">The world it runs over.</param>
    public PlaySession(EngineLoop loop, World world) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(world);

        Loop = loop;
        World = world;
    }

    /// <summary>Says what was added, for the report the session opens with.</summary>
    /// <param name="name">A short phrase — "physics", "terrain collision" — not a type name.</param>
    public void Runs(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        running.Add(name);
    }

    /// <summary>Hands something to the session to dispose when the session ends.</summary>
    /// <typeparam name="T">Its type.</typeparam>
    /// <param name="resource">The thing.</param>
    /// <returns>The same thing, so it can be assigned in one expression.</returns>
    public T Owns<T>(T resource) where T : IDisposable {
        ArgumentNullException.ThrowIfNull(resource);

        undo.Add(() => resource.Dispose());

        return resource;
    }

    /// <summary>Registers something to run when the session ends.</summary>
    /// <param name="action">What to do.</param>
    /// <remarks>
    ///     For what is not an <see cref="IDisposable" />: clearing a property a module was pointed at,
    ///     unsubscribing from an event, putting a setting back.
    /// </remarks>
    public void OnStop(Action action) {
        ArgumentNullException.ThrowIfNull(action);

        undo.Add(action);
    }

    /// <summary>Offers a service to the contributors that have not attached yet.</summary>
    /// <typeparam name="T">The contract, which is how they will ask for it.</typeparam>
    /// <param name="service">The service.</param>
    /// <remarks>
    ///     ⚠ <b>Keyed on the static type, like <c>PluginServices</c>, and for the same reason.</b>
    ///     Providing a <c>PhysicsScene</c> as its own type is what lets a navigation or a buoyancy
    ///     contributor find <em>the</em> simulation rather than standing up a second one — two
    ///     physics worlds over one scene being a state in which nothing collides with anything and
    ///     no error is raised.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Something already provided that contract.</exception>
    public void Provide<T>(T service) where T : class {
        ArgumentNullException.ThrowIfNull(service);

        if (!provided.TryAdd(typeof(T), service)) {
            throw new InvalidOperationException(
                $"Two contributions both provided a {typeof(T).Name} to this play session. One "
                + "simulation per scene is the contract; a second one is a world nothing else in the "
                + "frame collides with."
            );
        }
    }

    /// <summary>Asks for a service an earlier contribution provided.</summary>
    /// <typeparam name="T">The contract.</typeparam>
    /// <param name="service">The service, if there is one.</param>
    /// <returns>Whether there was.</returns>
    /// <remarks>
    ///     ⚠ <b>Contributions attach in registration order, so this answers for what came
    ///     <em>before</em>.</b> A module that needs another module's service must be registered after
    ///     it — which for the editor's own features is the order in <c>EditorModules.Standard</c>.
    ///     Answering <see langword="false" /> is the ordinary case and means "run without it", not
    ///     "fail".
    /// </remarks>
    public bool TryGet<T>(out T? service) where T : class {
        if (provided.TryGetValue(typeof(T), out var found)) {
            service = (T) found;

            return true;
        }

        service = null;

        return false;
    }

    /// <summary>Runs every undo, newest first.</summary>
    /// <remarks>
    ///     ⚠ <b>Every one of them runs even when one throws.</b> Stopping half-way would strand a
    ///     native world and leave the editor wedged in play mode with a session it cannot end —
    ///     which is a worse failure than the one that caused it. What is thrown is thrown afterwards,
    ///     because a teardown that failed silently is the class of bug the leak comparison exists to
    ///     catch.
    /// </remarks>
    internal void Release() {
        List<Exception>? failures = null;

        for (var index = undo.Count - 1; index >= 0; index--) {
            try {
                undo[index]();
            } catch (Exception failure) {
                (failures ??= []).Add(failure);
            }
        }

        undo.Clear();
        provided.Clear();

        if (failures is { Count: > 0 }) {
            throw new AggregateException("A play session's teardown failed.", failures);
        }
    }
}

/// <summary>What adds systems to an in-editor play session.</summary>
/// <remarks>
///     <para>
///         <b>Contributed to <c>IEditorRegistry</c>, which is the door every other editor extension
///         comes through</b> — doc 36 § D2. A module registers one in its <c>Activate</c>;
///         <c>PlayModeController</c> reads them when Play is pressed, so a plugin loaded three
///         seconds into a session still reaches the next one.
///     </para>
///     <para>
///         ⚠ <b>This is the general mechanism, and it is deliberately not "a project declares its
///         frame".</b> A project's own systems are constructed by that project's <c>OnInitialise</c>
///         against services it builds — the editor can list them by reflection and does, but it
///         cannot instantiate one without running the game's boot path, which is what the
///         out-of-process topology already is. What this closes is the *other* half of that gap: a
///         system whose service the <em>editor</em> can own — a <c>PhysicsScene</c>, an audio engine,
///         a navmesh — no longer needs an embedding host to add it by hand.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="Attach" /> that throws does not stop the session.</b> The contribution
///         is named in <c>PlayModeController.Refused</c> and reported, because a module that cannot
///         stand its systems up is a part of the frame that is missing — and the rule is that a
///         missing part is said out loud rather than left to read as a gameplay bug.
///     </para>
/// </remarks>
/// <example>
///     <code language="csharp" no-compile="a fragment; `context` is the module's PluginContext">
///     context.Owns(context.Services.Require&lt;IEditorRegistry&gt;().Add&lt;IPlaySystems&gt;(new PlayAudio()));
///     </code>
/// </example>
public interface IPlaySystems {
    /// <summary>Adds this contribution's systems to a session that is starting.</summary>
    /// <param name="session">The session: its loop, its world, and where to put the teardown.</param>
    /// <remarks>
    ///     Called after the snapshot has been taken and after the session's behaviours are in place,
    ///     so anything created here is inside what a stop restores.
    /// </remarks>
    void Attach(PlaySession session);
}
