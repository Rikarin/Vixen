// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Testing;

/// <summary>One thing a test did, and how it went.</summary>
/// <param name="Depth">How far into a chain it was, for indentation.</param>
/// <param name="Text">What it was, written the way somebody reading the test would say it.</param>
/// <param name="Outcome">What came of it, or <c>null</c> while it is still running.</param>
/// <param name="Frames">How many frames it waited before it succeeded.</param>
public readonly record struct LoggedCommand(int Depth, string Text, string? Outcome, int Frames) {
    /// <inheritdoc />
    public override string ToString() {
        var line = new StringBuilder();
        line.Append(' ', Depth * 2).Append(Text);

        if (Outcome is not null) {
            line.Append("  → ").Append(Outcome);
        }

        // Only when it actually waited. "(0 frames)" on every line is noise that hides the one line
        // where the number is the answer — a command that retried fifty-nine times and then passed
        // is a test about to become flaky, and it should be visible without being looked for.
        if (Frames > 0) {
            line.Append(" [").Append(Frames).Append(" frames]");
        }

        return line.ToString();
    }
}

/// <summary>Everything a test has done, in order.</summary>
/// <remarks>
///     <para>
///         Cypress's best idea, and the one worth copying before any of the assertions: when a test
///         fails, what you need is not the failing line but the twenty commands before it. A stack
///         trace says which assertion threw; this says the interface had three buttons when the test
///         expected one, that the click before it landed on the overlay, and that the assertion three
///         steps back passed only after fifty-eight frames.
///     </para>
///     <para>
///         Kept unconditionally rather than behind a flag. A log switched on after a failure is a log
///         for a failure that no longer reproduces, and the cost is a string per command in a test
///         that is already building an element tree.
///     </para>
/// </remarks>
public sealed class CommandLog {
    readonly List<LoggedCommand> commands = [];

    /// <summary>What has been done, in order.</summary>
    public IReadOnlyList<LoggedCommand> Commands => commands;

    /// <summary>How deep the chain currently is.</summary>
    internal int Depth { get; set; }

    /// <summary>Records a command that has not finished yet, and returns where it went.</summary>
    /// <param name="text">What it is.</param>
    /// <returns>Its index, for <see cref="Complete" />.</returns>
    internal int Begin(string text) {
        commands.Add(new(Depth, text, null, 0));
        return commands.Count - 1;
    }

    /// <summary>Records how a command turned out.</summary>
    /// <param name="index">What <see cref="Begin" /> returned.</param>
    /// <param name="outcome">What came of it.</param>
    /// <param name="frames">How many frames it waited.</param>
    internal void Complete(int index, string? outcome, int frames) {
        var command = commands[index];
        commands[index] = command with { Outcome = outcome, Frames = frames };
    }

    /// <summary>The whole log, one command per line.</summary>
    public override string ToString() {
        var text = new StringBuilder();

        foreach (var command in commands) {
            text.AppendLine(command.ToString());
        }

        return text.ToString().TrimEnd();
    }
}
