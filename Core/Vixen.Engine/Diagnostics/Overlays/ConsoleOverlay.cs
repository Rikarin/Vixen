// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Engine.Diagnostics.Overlays;

/// <summary>The console: a prompt, what has been typed, and what the commands answered.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Keystrokes are pushed in; the overlay does not read a keyboard.</b> Which device
///         produces a character, and whether the game should stop seeing it while the console is
///         open, are the host's questions — and answering them here would mean <c>Vixen.Engine</c>
///         deciding how text entry works on every platform it runs on. <see cref="Type(char)" />,
///         <see cref="Backspace" />, <see cref="Submit" /> and the history moves are the whole
///         surface, which also makes the console testable without an input stack.
///     </para>
///     <para>
///         <see cref="IsCapturingInput" /> is what a host polls to know that the game should not act
///         on this frame's keys. Getting that wrong is how typing <c>reload</c> also makes the player
///         reload.
///     </para>
/// </remarks>
public sealed class ConsoleOverlay : IDiagnosticOverlay {
    /// <summary>How long a line may be typed.</summary>
    public const int MaxInputLength = 512;

    readonly StringBuilder input = new(128);
    readonly List<string> matches = [];

    int historyCursor = -1;

    /// <summary>Uses a registry.</summary>
    /// <param name="commands">The commands, and where their output goes.</param>
    public ConsoleOverlay(ConsoleCommands commands) {
        ArgumentNullException.ThrowIfNull(commands);
        Commands = commands;
    }

    /// <summary>What it runs.</summary>
    public ConsoleCommands Commands { get; }

    /// <inheritdoc />
    public string Name => "console";

    /// <inheritdoc />
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopRight;

    /// <inheritdoc />
    public bool Enabled { get; set; }

    /// <summary>How wide the panel is, in pixels.</summary>
    public float Width { get; set; } = 460f;

    /// <summary>How many lines of output are shown above the prompt.</summary>
    public int Lines { get; set; } = 12;

    /// <summary>What has been typed so far.</summary>
    public string Input => input.ToString();

    /// <summary>
    ///     Whether the host should keep this frame's keystrokes away from the game.
    /// </summary>
    public bool IsCapturingInput => Enabled;

    /// <summary>Adds a character to the line.</summary>
    /// <param name="character">The character. Control characters are ignored.</param>
    public void Type(char character) {
        // Filtered here rather than at the host, because every host would otherwise have to know
        // that a newline means Submit and a backspace means Backspace — and one of them would send
        // both, giving a line with a stray \b in it that no command will ever match.
        if (!char.IsControl(character) && input.Length < MaxInputLength) {
            input.Append(character);
            historyCursor = -1;
        }
    }

    /// <summary>Adds a run of characters to the line.</summary>
    /// <param name="text">The characters.</param>
    public void Type(ReadOnlySpan<char> text) {
        foreach (var character in text) {
            Type(character);
        }
    }

    /// <summary>Removes the last character.</summary>
    public void Backspace() {
        if (input.Length > 0) {
            input.Length--;
            historyCursor = -1;
        }
    }

    /// <summary>Throws the typed line away.</summary>
    public void ClearInput() {
        input.Clear();
        historyCursor = -1;
    }

    /// <summary>Runs what has been typed and clears the line.</summary>
    /// <returns><see langword="false" /> if the line was empty or the command was unknown.</returns>
    public bool Submit() {
        if (input.Length == 0) {
            return false;
        }

        var line = input.ToString();
        ClearInput();

        return Commands.Execute(line);
    }

    /// <summary>Replaces the line with an earlier one.</summary>
    /// <param name="back">Which way: back through the history, or forward again.</param>
    public void Recall(bool back) {
        var history = Commands.History;

        if (history.Count == 0) {
            return;
        }

        // -1 is "not recalling"; 0 is the newest entry. Stepping forward off the newest returns to
        // an empty line rather than sticking, which is what every shell does.
        historyCursor = back
            ? Math.Min(historyCursor + 1, history.Count - 1)
            : historyCursor - 1;

        input.Clear();

        if (historyCursor < 0) {
            historyCursor = -1;
            return;
        }

        input.Append(history[history.Count - 1 - historyCursor]);
    }

    /// <summary>Completes the typed line against the command names.</summary>
    /// <returns><see langword="true" /> if the line changed.</returns>
    /// <remarks>
    ///     Completes to the longest common prefix and lists the candidates when there is more than
    ///     one, which is what a shell does and therefore what fingers expect.
    /// </remarks>
    public bool Complete() {
        // Only the command word completes. Completing arguments would mean each command describing
        // what its arguments can be, which is a bigger surface than the console is worth today.
        if (input.Length == 0 || Input.Contains(' ', StringComparison.Ordinal)) {
            return false;
        }

        var typed = Input;

        if (Commands.Complete(typed, matches) == 0) {
            return false;
        }

        if (matches.Count == 1) {
            input.Clear();
            input.Append(matches[0]);
            input.Append(' ');
            return true;
        }

        var shared = matches[0].AsSpan();

        foreach (var candidate in matches) {
            var common = 0;

            while (common < shared.Length && common < candidate.Length
                && char.ToLowerInvariant(shared[common]) == char.ToLowerInvariant(candidate[common])) {
                common++;
            }

            shared = shared[..common];
        }

        Commands.Write(string.Join("  ", matches));

        if (shared.Length <= typed.Length) {
            return false;
        }

        input.Clear();
        input.Append(shared);

        return true;
    }

    /// <inheritdoc />
    public void Draw(OverlaySurface surface, in GameTime time) {
        ArgumentNullException.ThrowIfNull(surface);

        var theme = surface.Theme;
        var lines = Math.Max(1, Lines);
        var region = surface.Panel(Anchor, Width, lines + 1, "CONSOLE");

        if (region.IsEmpty) {
            return;
        }

        var output = Commands.Output;
        var first = Math.Max(0, output.Count - lines);
        var shown = output.Count - first;

        // ⚠ Cut to the panel rather than wrapped, and cut at all — nothing in the overlay pipeline
        // clips, so a `help` line longer than the panel is drawn straight through the border and off
        // the edge of the screen. Wrapping instead would turn twelve lines of history into four.
        var characters = Math.Max(1, (int) (region.ContentWidth / DebugFont.AdvanceFor(theme.TextSize)));

        // Bottom-aligned against the prompt, so the newest line is always in the same place and a
        // short session does not float in the middle of an empty panel.
        for (var index = 0; index < shown; index++) {
            var text = output[first + index].AsSpan();
            var colour = text.StartsWith(">", StringComparison.Ordinal) ? theme.Muted : theme.Text;

            region.Text(lines - shown + index, text[..Math.Min(text.Length, characters)], colour);
        }

        Span<char> prompt = stackalloc char[MaxInputLength + 4];

        prompt[0] = ']';
        prompt[1] = ' ';

        var written = 2;

        // ⚠ The *end* of the line, not the beginning. A line longer than the panel has to lose one
        // end or the other, and the end somebody is typing at is the one they need to see — showing
        // the head instead means the caret walks off the panel and typing becomes blind.
        var room = Math.Max(1, characters - 3);
        var start = Math.Max(0, input.Length - room);

        for (var index = start; index < input.Length && written < prompt.Length - 1; index++) {
            prompt[written++] = input[index];
        }

        // A caret that blinks, because a console with nothing typed in it and a console that is not
        // taking input look the same otherwise — and the second one is a bug worth seeing.
        if ((int) (time.TotalSeconds * 2d) % 2 == 0) {
            prompt[written++] = '_';
        }

        region.Text(lines, prompt[..written], theme.Heading);
    }
}
