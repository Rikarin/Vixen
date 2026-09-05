// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit.Sdk;

namespace Vixen.Testing;

/// <summary>
///     The assertion vocabulary over the Null backend's command log.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> § "Test infrastructure worth building early" asks for a
///         <c>RecordingBackend</c>: *"the Null backend's structured command log with a fluent
///         assertion API"*. ⚠ The recording half was built long ago and is used by seventy-odd test
///         files — <see cref="CommandRecorder" /> and <see cref="RecordedCommand" />. This is the
///         half that was missing, so it is a vocabulary rather than a backend, and the name is kept
///         because the document's is the name people will search for.
///     </para>
///     <para>
///         <b>What it buys is the failure message, not the assertion.</b>
///         <c>Assert.Equal(2, log.CountOf(Draw))</c> is already one line; what it prints when it
///         fails is "Expected: 2, Actual: 1", which says nothing about which draw went missing or
///         what the frame did instead. Every failure here carries the stream, indented by debug
///         group, for the same reason <c>UiTestException</c> carries the
///         command log: the question is never "was the number wrong", it is "which of the three
///         plausible things happened".
///     </para>
///     <para>
///         ⚠ <b>A negative assertion over an empty log proves nothing, so it is refused.</b> The
///         commonest way an RHI test comes to assert nothing at all is a device built without
///         <c>Record = true</c>, or a frame that threw before it drew: every
///         <c>ShouldNotContain</c> then passes, and a suite of them is a green report on a frame
///         that never ran. This is the Null-device trap one layer up — the same shape as a headless
///         run that falls back to a backend drawing nothing and still prints healthy counters — and
///         the answer is the same one <see cref="Log" /> applies: refuse by name rather than pass
///         quietly.
///     </para>
/// </remarks>
static class RecordingBackend {
    /// <summary>The device's command log, or a failure naming the switch that was not set.</summary>
    /// <param name="device">The device under test.</param>
    /// <remarks>
    ///     ⚠ The alternative is <c>device.Recorder!</c>, which is what every existing suite writes,
    ///     and on the day recording is off it is a <see cref="NullReferenceException" /> from inside
    ///     an assertion — a failure that reads like a bug in the code under test. This says which
    ///     line of the fixture is wrong.
    /// </remarks>
    public static CommandRecorder Log(this NullDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        return device.Recorder
            ?? throw new XunitException(
                "The device was created without `Record = true`, so there is no command log and no "
                + "assertion over one would mean anything. Build it as `new NullDevice(new() { Record = true })`."
            );
    }

    /// <summary>At least one call of this kind was recorded.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="kind">Which call.</param>
    /// <returns>A cursor at the first match, for the ordering assertions.</returns>
    public static CommandLogCursor ShouldContain(this CommandRecorder log, RecordedCommandKind kind) =>
        log.ShouldContain(kind, _ => true, kind.ToString());

    /// <summary>At least one call of this kind, matching this predicate, was recorded.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="kind">Which call.</param>
    /// <param name="where">The condition on its arguments.</param>
    /// <param name="described">How to name the expectation in a failure. The kind alone by default.</param>
    /// <returns>A cursor at the first match.</returns>
    public static CommandLogCursor ShouldContain(
        this CommandRecorder log,
        RecordedCommandKind kind,
        Func<RecordedCommand, bool> where,
        string? described = null
    ) {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(where);

        var what = described ?? kind.ToString();
        var commands = log.Commands;

        for (var index = 0; index < commands.Count; index++) {
            if (commands[index].Kind == kind && where(commands[index])) {
                return new(log, index, what);
            }
        }

        throw Failure($"expected a {what}", Found(commands, kind), log);
    }

    /// <summary>Exactly this many calls of this kind were recorded.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="kind">Which call.</param>
    /// <param name="times">How many. Zero is refused — see <see cref="ShouldNotContain" />.</param>
    public static void ShouldContain(this CommandRecorder log, RecordedCommandKind kind, int times) {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfNegative(times);

        if (times == 0) {
            throw new XunitException(
                $"`ShouldContain({kind}, times: 0)` is `ShouldNotContain({kind})` written so that an "
                + "empty log satisfies it. Use `ShouldNotContain`, which refuses an empty log."
            );
        }

        var counted = log.CountOf(kind);

        if (counted != times) {
            throw Failure($"expected {times} × {kind}", $"{counted}", log);
        }
    }

    /// <summary>No call of this kind was recorded, over a log that recorded something.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="kind">Which call.</param>
    /// <remarks>
    ///     ⚠ Fails on an empty log rather than passing. "The pass did not dispatch" and "the pass
    ///     did not run" are different claims and only the first is worth making; an empty stream is
    ///     the second wearing the first's clothes.
    /// </remarks>
    public static void ShouldNotContain(this CommandRecorder log, RecordedCommandKind kind) {
        ArgumentNullException.ThrowIfNull(log);

        if (log.Count == 0) {
            throw new XunitException(
                $"nothing at all was recorded, so \"no {kind}\" is vacuous. Assert on a frame that "
                + "recorded something, or the claim is about the fixture rather than the code."
            );
        }

        var found = log.OfKind(kind);

        if (found.Count > 0) {
            throw Failure($"expected no {kind}", $"{found.Count}, first at #{found[0].Sequence}", log);
        }
    }

    /// <summary>A draw of this many vertices was recorded.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="vertices">How many vertices.</param>
    /// <param name="instances">How many instances. One by default.</param>
    /// <returns>A cursor at the first match.</returns>
    public static CommandLogCursor ShouldContainDraw(this CommandRecorder log, long vertices, long instances = 1) =>
        log.ShouldContain(
            RecordedCommandKind.Draw,
            command => command.A == vertices && command.B == instances,
            $"Draw vertices={vertices} instances={instances}"
        );

    /// <summary>An indexed draw of this many indices was recorded.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="indices">How many indices.</param>
    /// <param name="instances">How many instances. One by default.</param>
    /// <returns>A cursor at the first match.</returns>
    public static CommandLogCursor ShouldContainDrawIndexed(
        this CommandRecorder log,
        long indices,
        long instances = 1
    ) =>
        log.ShouldContain(
            RecordedCommandKind.DrawIndexed,
            command => command.A == indices && command.B == instances,
            $"DrawIndexed indices={indices} instances={instances}"
        );

    /// <summary>A dispatch of this group count was recorded.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="x">Groups in x.</param>
    /// <param name="y">Groups in y. One by default.</param>
    /// <param name="z">Groups in z. One by default.</param>
    /// <returns>A cursor at the first match.</returns>
    public static CommandLogCursor ShouldContainDispatch(this CommandRecorder log, long x, long y = 1, long z = 1) =>
        log.ShouldContain(
            RecordedCommandKind.Dispatch,
            command => command.A == x && command.B == y && command.C == z,
            $"Dispatch groups={x}×{y}×{z}"
        );

    /// <summary>A render pass of this name was begun.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="name">The pass name, as the description carried it.</param>
    /// <returns>A cursor at the first match.</returns>
    public static CommandLogCursor ShouldContainRenderPass(this CommandRecorder log, string name) =>
        log.ShouldContain(
            RecordedCommandKind.BeginRenderPass,
            command => string.Equals(command.Text, name, StringComparison.Ordinal),
            $"BeginRenderPass '{name}'"
        );

    /// <summary>These kinds were recorded in this relative order.</summary>
    /// <param name="log">The stream.</param>
    /// <param name="kinds">The calls, in the order they must appear.</param>
    /// <remarks>
    ///     A subsequence rather than a run, unlike <see cref="CommandRecorder.Contains" />: this is
    ///     the "bind before you draw" question, and inserting a debug marker between the two must
    ///     not break it. Use <see cref="CommandRecorder.Contains" /> when the adjacency is the
    ///     claim.
    /// </remarks>
    public static void ShouldRecordInOrder(this CommandRecorder log, params ReadOnlySpan<RecordedCommandKind> kinds) {
        ArgumentNullException.ThrowIfNull(log);

        if (kinds.IsEmpty) {
            throw new XunitException("an order over no calls is satisfied by any stream, including an empty one.");
        }

        var commands = log.Commands;
        var next = 0;

        foreach (var command in commands) {
            if (command.Kind == kinds[next] && ++next == kinds.Length) {
                return;
            }
        }

        var wanted = string.Join(" → ", kinds.ToArray());
        throw Failure($"expected {wanted}", $"got as far as {kinds[next]}, which never arrived", log);
    }

    /// <summary>Builds the failure, with the stream under it.</summary>
    internal static XunitException Failure(string what, string found, CommandRecorder log) {
        var message = new StringBuilder();
        message.Append(what).AppendLine(".");
        message.Append("  Found: ").AppendLine(found);
        message.AppendLine();
        message.AppendLine(CultureInfo.InvariantCulture, $"Commands ({log.Count}):");
        message.Append(Indent(log.Dump()));
        return new(message.ToString());
    }

    static string Found(IReadOnlyList<RecordedCommand> commands, RecordedCommandKind kind) {
        var ofKind = commands.Where(command => command.Kind == kind).ToArray();

        if (ofKind.Length == 0) {
            return $"no {kind} at all";
        }

        var listed = string.Join(", ", ofKind.Select(command => command.ToString()));
        return $"{ofKind.Length} × {kind}, none matching: {listed}";
    }

    static string Indent(string text) =>
        string.Join(
            Environment.NewLine,
            text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line => "  " + line)
        );
}

/// <summary>One matched call, and the questions worth asking about what surrounds it.</summary>
/// <remarks>
///     The ordering half of the vocabulary. Twenty-odd suites in the tree write
///     <c>stream.FindIndex(c => c.Kind == BindDescriptorSet) &lt; stream.FindIndex(c => c.Kind == Draw)</c>,
///     which is three lines and, when it fails, an <c>Assert.True</c> with no message. ⚠ It is also
///     wrong when either call is absent: <c>FindIndex</c> returns −1, and −1 is less than every
///     index, so "bind before draw" passes when nothing bound anything. This type cannot express
///     that mistake — the cursor exists only because a match was found.
/// </remarks>
/// <param name="Log">The stream the match came from.</param>
/// <param name="Index">Where in the stream it is.</param>
/// <param name="What">How the expectation was named, for the failure messages of the chained calls.</param>
readonly record struct CommandLogCursor(CommandRecorder Log, int Index, string What) {
    /// <summary>The matched call.</summary>
    public RecordedCommand Command => Log.Commands[Index];

    /// <summary>This pipeline was bound before the matched call, and not replaced in between.</summary>
    /// <param name="pipeline">The pipeline, as the test created it.</param>
    /// <returns>This.</returns>
    public CommandLogCursor AfterBinding(PipelineHandle pipeline) => AfterBinding((long)pipeline.Value.Packed);

    /// <summary>This pipeline was bound before the matched call, and not replaced in between.</summary>
    /// <param name="pipeline">The pipeline handle, as <see cref="RecordedCommand.A" /> carries it.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     "Not replaced in between" is what makes this the assertion the caller means. A frame that
    ///     binds Opaque, binds Shadow and then draws did bind Opaque before the draw, and the draw
    ///     used Shadow.
    /// </remarks>
    public CommandLogCursor AfterBinding(long pipeline) {
        var commands = Log.Commands;

        for (var index = Index - 1; index >= 0; index--) {
            if (commands[index].Kind != RecordedCommandKind.BindPipeline) {
                continue;
            }

            return commands[index].A == pipeline
                ? this
                : throw RecordingBackend.Failure(
                    $"expected {What} under pipeline {pipeline}",
                    $"the pipeline in force was {commands[index].A}, bound at #{commands[index].Sequence}",
                    Log
                );
        }

        throw RecordingBackend.Failure(
            $"expected {What} under pipeline {pipeline}",
            "no pipeline was bound at all",
            Log
        );
    }

    /// <summary>A call of this kind was recorded before the matched one.</summary>
    /// <param name="kind">Which call.</param>
    /// <returns>This.</returns>
    public CommandLogCursor After(RecordedCommandKind kind) {
        var commands = Log.Commands;

        for (var index = Index - 1; index >= 0; index--) {
            if (commands[index].Kind == kind) {
                return this;
            }
        }

        throw RecordingBackend.Failure(
            $"expected a {kind} before {What}",
            commands.Any(command => command.Kind == kind)
                ? $"the only {kind} calls come after it"
                : $"no {kind} at all",
            Log
        );
    }

    /// <summary>A call of this kind was recorded after the matched one.</summary>
    /// <param name="kind">Which call.</param>
    /// <returns>This.</returns>
    public CommandLogCursor Before(RecordedCommandKind kind) {
        var commands = Log.Commands;

        for (var index = Index + 1; index < commands.Count; index++) {
            if (commands[index].Kind == kind) {
                return this;
            }
        }

        throw RecordingBackend.Failure(
            $"expected a {kind} after {What}",
            commands.Any(command => command.Kind == kind)
                ? $"the only {kind} calls come before it"
                : $"no {kind} at all",
            Log
        );
    }

    /// <summary>The matched call is inside an open debug group of this name.</summary>
    /// <param name="name">The group name.</param>
    /// <returns>This.</returns>
    /// <remarks>
    ///     Walks the group stack rather than looking for the nearest push, so a draw inside
    ///     <c>Opaque → Instanced</c> is inside both.
    /// </remarks>
    public CommandLogCursor InsideDebugGroup(string name) {
        var commands = Log.Commands;
        var open = new Stack<string?>();

        for (var index = 0; index < Index; index++) {
            switch (commands[index].Kind) {
                case RecordedCommandKind.PushDebugGroup:
                    open.Push(commands[index].Text);
                    break;
                case RecordedCommandKind.PopDebugGroup when open.Count > 0:
                    open.Pop();
                    break;
            }
        }

        return open.Any(group => string.Equals(group, name, StringComparison.Ordinal))
            ? this
            : throw RecordingBackend.Failure(
                $"expected {What} inside debug group '{name}'",
                open.Count == 0 ? "no group was open" : $"open groups were {string.Join(" → ", open.Reverse())}",
                Log
            );
    }

    /// <summary>Reads the cursor as the call it matched, so an argument can be asserted directly.</summary>
    /// <param name="cursor">The cursor.</param>
    public static implicit operator RecordedCommand(CommandLogCursor cursor) => cursor.Command;
}
