// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Ecs.Systems;
using Vixen.Editor.Core;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Vixen.Rendering;

namespace Vixen.Editor.SceneView;

/// <summary>The editor's play session: the transport's verbs over a <see cref="PlayModeController" />.</summary>
/// <remarks>
///     <para>
///         <b>Moved out of the application, not rewritten (#1289).</b> <c>EnterPlay</c>,
///         <c>LeavePlay</c>, the pause toggle, <c>ReportPlayGaps</c>' wording, <c>ProjectSystems</c>
///         and <c>DrainPlayDiagnostics</c> were private on <c>EditorApplication</c>; the debug draw,
///         the overlay registry and the drained segments were its fields. They are this object's now,
///         and the application constructs one, ticks it, publishes it, and subscribes to
///         <see cref="Entered" /> and <see cref="Left" /> for the parts that are the workspace's.
///     </para>
///     <para>
///         ⚠ <b>The selection is asked for, not held.</b> Stop translates the selection through the
///         restore's handle map, and which selection that is — the active scene's — is the
///         composition root's answer and can change while a session runs.
///     </para>
/// </remarks>
public sealed class EditorPlaySession : IPlaySession {
    readonly Func<Selection<Entity>> selection;

    /// <summary>This frame's overlay geometry as segment pairs, which is what a pane consumes.</summary>
    readonly List<LineVertex> lines = [];

    /// <summary>Wraps a controller.</summary>
    /// <param name="controller">The snapshot, the loop, and the state machine.</param>
    /// <param name="selection">The selection Stop translates back — the active scene's.</param>
    public EditorPlaySession(PlayModeController controller, Func<Selection<Entity>> selection) {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(selection);

        Controller = controller;
        this.selection = selection;
    }

    /// <summary>Snapshot, restore, and what state the transport is in.</summary>
    public PlayModeController Controller { get; }

    /// <inheritdoc />
    public PlayState State => Controller.State;

    /// <inheritdoc />
    public bool IsPlaying => Controller.IsPlaying;

    /// <inheritdoc />
    public PlaySession? Current => Controller.Session;

    /// <summary>Where a running play session's overlays put their world geometry.</summary>
    /// <remarks>
    ///     ⚠ <b>One per editor and it outlives the session</b>
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1247">#1247</a>). It is emptied before
    ///     the loop steps and read after, so what a pane draws is this frame's — see
    ///     <see cref="Tick" />. <c>WaterPresenter</c> owns a second one for the same
    ///     reason one layer down: nothing in this tree constructs a <c>DebugDraw</c> in a running
    ///     program, so every subsystem's gizmos were written into an accumulator that did not exist.
    /// </remarks>
    public DebugDraw Debug { get; } = new();

    /// <inheritdoc />
    /// <remarks>
    ///     Also one per editor. ⚠ <b>That alone does not carry a toggle across Stop, and this used to
    ///     say it did.</b> <c>Enabled</c> lives on the overlay object, which is per session —
    ///     <c>PlayPhysics</c> registers it and takes it back out, because it holds a
    ///     <c>PhysicsScene</c> that Stop destroys — so the next Play's overlay arrived switched off.
    ///     What outlives the session is the application's <c>overlayWishes</c>, which
    ///     <c>FollowPlayOverlays</c> applies to each new one (#1263).
    /// </remarks>
    public DiagnosticOverlays Overlays { get; } = new();

    /// <summary>The overlays' world geometry, as segment pairs a pane's line pass takes.</summary>
    /// <remarks>
    ///     The same list every frame, so a pane handed it once keeps reading the current frame's.
    ///     Empty is the ordinary answer: an editing frame runs no session, and a session runs no
    ///     overlay until somebody switches one on.
    /// </remarks>
    public IReadOnlyList<LineVertex> DiagnosticLines => lines;

    /// <inheritdoc />
    public event Action<IPlaySession>? Entered;

    /// <inheritdoc />
    public event Action<IPlaySession>? Left;

    /// <inheritdoc />
    public bool Enter() {
        if (!Controller.Play()) {
            return false;
        }

        Entered?.Invoke(this);
        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<Entity> Leave() {
        var selected = selection();
        var restored = Controller.Stop(selected);

        selected.Set(restored);
        Left?.Invoke(this);

        return restored;
    }

    /// <inheritdoc />
    public void TogglePause() {
        if (Controller.State == PlayState.Paused) {
            Controller.Resume();
        } else {
            Controller.Pause();
        }
    }

    /// <inheritdoc />
    public void StepFrame() => Controller.Step();

    /// <summary>Runs one frame of the session, if one is running, and drains what its overlays drew.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <returns>Whether the game's frame ran — which is what replaces the editor's transform pass.</returns>
    /// <remarks>
    ///     ⚠ <b>Emptied before the step and read after it, which is the only order that shows this
    ///     frame's colliders</b>: <c>PhysicsDebugDrawSystem</c> runs in <c>PreRender</c>, inside
    ///     <c>Tick</c>. Clearing afterwards would draw nothing at all and clearing nowhere would
    ///     accumulate every frame of the session into one solid mass of lines.
    /// </remarks>
    public bool Tick(TimeSpan delta) {
        Debug.Clear();

        var ticked = Controller.Tick(delta);

        DrainDiagnostics();
        return ticked;
    }

    /// <summary>What the session runs, said in a sentence.</summary>
    /// <returns>The information line play mode writes to the log on the way in.</returns>
    /// <remarks>
    ///     ⚠ <b>Read off the session rather than written out here, and that is the point of the list
    ///     existing.</b> This sentence used to name physics as a thing an in-editor session does not
    ///     run, and an <c>IPlaySystems</c> contribution has since made that false for the editor Vixen
    ///     ships — a fixed sentence would now be a report that lies in the safe direction, which is
    ///     the one that costs somebody a day.
    /// </remarks>
    public string DescribeRunning() {
        var added = Controller.Session?.Running ?? [];

        return $"Play mode runs behaviours, coroutines and transforms{Also(added)}. Anything else a game "
            + "registers imperatively in its own OnInitialise — rather than declaring with "
            + "[GameSystem] — takes a host service the editor was never handed, so an in-editor "
            + "session does not run it.";
    }

    /// <summary>Names anything this session is not running, or <see langword="null" /> when that is nothing.</summary>
    /// <param name="project">The project's own assembly, if one is built and loaded.</param>
    /// <returns>The warning play mode logs and shows, or <see langword="null" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20's first bar applied to a frame rather than to a menu line: a thing that
    ///         does not happen must be <i>visibly</i> not happening.</b> An in-editor session steps
    ///         an <c>EngineLoop</c>'s default graph — behaviours, coroutines, transforms — and
    ///         nothing else, because every other system a game runs is registered by that game's own
    ///         <c>OnInitialise</c> against a <c>PhysicsScene</c>, an <c>AudioEngine</c>, an
    ///         <c>InputService</c> or a <c>RenderView</c> that an editor either does not have or
    ///         already has a second, differently-aimed one of. A Play button that ran most of a frame
    ///         and said nothing would make the missing part read as a gameplay bug, which is the one
    ///         outcome worse than not stepping at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The project's own systems are named, not counted.</b> "Some systems did not run"
    ///         sends somebody looking through their whole game; a list of three type names sends them
    ///         to the three files. They are found by reflection over the assembly
    ///         <c>ProjectAssemblies</c> already built and loaded, so the list is what this project
    ///         declares rather than what the engine ships.
    ///     </para>
    /// </remarks>
    public string? DescribeMissing(Assembly? project) {
        // ⚠ The project's systems minus the ones that just ran, and the subtraction is the whole
        // point of #320. This list used to be every `ISystem` the project's assembly declared,
        // because none of them could run; a system that carries `[GameSystem]` and found its
        // services is now in the frame, and naming it as missing would be a report that lies.
        var systems = ProjectSystems(project).Except(Controller.Declared.Running, StringComparer.Ordinal).ToArray();
        var unsatisfied = Controller.Declared.Missing;
        var behaviors = Controller.Unsupported;
        var refused = Controller.Refused;

        if (systems.Length == 0 && unsatisfied.Count == 0 && behaviors.Count == 0 && refused.Count == 0) {
            return null;
        }

        List<string> said = [];

        // ⚠ Two different failures, said differently. An undeclared system is one nobody asked the
        // editor to run — the fix is `[GameSystem]`, and saying so is worth more than the list. An
        // unsatisfied one *was* declared and could not be built, which is a service the editor does
        // not have and a much narrower thing to go and look at.
        if (systems.Length > 0) {
            said.Add(
                $"{systems.Length} system(s) this project declares but does not mark [GameSystem]: "
                + $"{string.Join(", ", systems)}."
            );
        }

        if (unsatisfied.Count > 0) {
            said.Add($"{unsatisfied.Count} declared system(s) whose services are not here: {string.Join("; ", unsatisfied)}.");
        }

        if (behaviors.Count > 0) {
            said.Add($"{behaviors.Count} behaviour(s) the session could not take over: {string.Join(", ", behaviors)}.");
        }

        // ⚠ A contribution that threw is the loudest of the three, because it is the editor's own
        // wiring failing rather than a gap the editor never claimed to fill. A session with no
        // physics after this editor started publishing one is a difference somebody will otherwise
        // spend the afternoon looking for in their game.
        if (refused.Count > 0) {
            said.Add($"{refused.Count} contribution(s) that failed to start: {string.Join(", ", refused)}.");
        }

        return "Not running — " + string.Join(" ", said);
    }

    /// <summary>", plus physics and terrain collision" — or nothing, for a session with no additions.</summary>
    static string Also(IReadOnlyList<string> added) => added.Count == 0 ? string.Empty : $", plus {Join(added)}";

    /// <summary>A list a person reads: "a", "a and b", "a, b and c".</summary>
    static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    /// <summary>The <c>ISystem</c> types the project's own assembly declares.</summary>
    /// <remarks>
    ///     ⚠ <b>Every <c>ISystem</c> in the assembly, including the ones that just ran.</b> The
    ///     caller subtracts <c>PlayModeController.Declared.Running</c>, so what is left is the set a
    ///     project still registers by hand in its <c>Game.OnInitialise</c> — code no editor runs.
    ///     Until <c>[GameSystem]</c> that was all of them, and this list was the closest true
    ///     statement available; it is now a list of systems whose author has not opted in yet, which
    ///     is a thing they can act on.
    /// </remarks>
    static IReadOnlyList<string> ProjectSystems(Assembly? assembly) {
        List<string> found = [];

        if (assembly is null) {
            return found;
        }

        Type?[] declared;

        try {
            declared = assembly.GetTypes();
        } catch (ReflectionTypeLoadException partial) {
            // A project referencing something that did not load still has the types that did, and a
            // partial list is a better answer here than none: this is a report, not a gate.
            declared = partial.Types;
        }

        foreach (var type in declared) {
            if (type is { IsClass: true, IsAbstract: false } && typeof(ISystem).IsAssignableFrom(type)) {
                found.Add(type.Name);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Turns what the session's overlays drew into segments a pane's line pass takes.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>World lines only, and the other two kinds are honestly absent</b> — the same
    ///         boundary <c>WaterPresenter.DebugLines</c> draws and for the same reason. A
    ///         <c>DebugDraw</c> also carries screen-space segments and world labels, and a pane has
    ///         no screen-space line pass and no world text pass to drain them into. The physics
    ///         overlay's wireframes, contact points, constraint anchors, broad-phase bounds and body
    ///         axes are all world lines, so all of it arrives.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What does <em>not</em> arrive is the counts panel.</b>
    ///         <c>PhysicsDebugDrawSystem.Draw</c> writes "bodies awake/all" into an
    ///         <c>OverlaySurface</c>, which is a game's HUD and not a pane — and the awake count is
    ///         the number that tells "every body is asleep" from "nothing is being stepped" apart. It
    ///         is a real gap and it is stated here rather than left to be discovered from an editor
    ///         showing wireframes and no numbers.
    ///     </para>
    ///     <para>
    ///         Run every frame rather than only while playing, because the frame after Stop has to
    ///         empty the list — a pane that kept the last session's lines would show a set of
    ///         colliders standing where the bodies were.
    ///     </para>
    /// </remarks>
    void DrainDiagnostics() {
        lines.Clear();

        foreach (var line in Debug.Lines) {
            lines.Add(new(line.From, line.Colour));
            lines.Add(new(line.To, line.Colour));
        }
    }
}
