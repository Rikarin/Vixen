// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Diagnostics.Overlays;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>The console: parsing, running, history and completion, none of which needs a screen.</summary>
public sealed class ConsoleTests {
    [Fact]
    public void ACommandRunsWithItsArguments() {
        var commands = new ConsoleCommands();
        var seen = string.Empty;

        commands.Register("teleport", "Moves something", entry => seen = string.Join('|', entry.Arguments));

        Assert.True(commands.Execute("teleport 1 2 3"));
        Assert.Equal("1|2|3", seen);
    }

    /// <summary>A quoted run is one argument, so <c>echo "hello world"</c> says what it looks like.</summary>
    [Fact]
    public void QuotedArgumentsSurviveAsOne() {
        var commands = new ConsoleCommands();
        var count = 0;

        commands.Register("say", "", entry => count = entry.Count);

        commands.Execute("say \"hello world\" again");

        Assert.Equal(2, count);
    }

    [Fact]
    public void AnUnknownCommandIsReportedRatherThanThrown() {
        var commands = new ConsoleCommands();

        Assert.False(commands.Execute("nonsense"));
        Assert.Contains(commands.Output, line => line.Contains("nonsense", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A console is typed at while something is already going wrong, so taking the process down
    ///     because an argument was mistyped would destroy the state being inspected.
    /// </summary>
    [Fact]
    public void ACommandThatThrowsIsReportedRatherThanPropagated() {
        var commands = new ConsoleCommands();
        commands.Register("boom", "", static _ => throw new InvalidOperationException("bang"));

        Assert.False(commands.Execute("boom"));
        Assert.Contains(commands.Output, line => line.Contains("bang", StringComparison.Ordinal));
    }

    [Fact]
    public void FlagsAcceptTheWordsPeopleActuallyType() {
        var commands = new ConsoleCommands();
        var results = new List<bool>();

        commands.Register(
            "set",
            "",
            entry => {
                if (entry.TryFlag(0, out var value)) {
                    results.Add(value);
                }
            }
        );

        foreach (var word in (string[]) ["on", "off", "1", "0", "true", "false", "yes", "no"]) {
            commands.Execute($"set {word}");
        }

        Assert.Equal([true, false, true, false, true, false, true, false], results);
    }

    [Fact]
    public void HistoryDoesNotRecordARepeat() {
        var commands = new ConsoleCommands();

        commands.Execute("help");
        commands.Execute("help");
        commands.Execute("clear");

        Assert.Equal(["help", "clear"], commands.History);
    }

    [Fact]
    public void RecallWalksBackAndForwardsThroughTheHistory() {
        var commands = new ConsoleCommands();
        commands.Execute("help");
        commands.Execute("clear");

        var console = new ConsoleOverlay(commands);

        console.Recall(back: true);
        Assert.Equal("clear", console.Input);

        console.Recall(back: true);
        Assert.Equal("help", console.Input);

        console.Recall(back: false);
        Assert.Equal("clear", console.Input);

        // Forward off the newest returns to an empty line rather than sticking, as every shell does.
        console.Recall(back: false);
        Assert.Equal(string.Empty, console.Input);
    }

    [Fact]
    public void CompletingOneMatchFinishesTheWordAndAddsASpace() {
        var commands = new ConsoleCommands();
        commands.Register("teleport", "", static _ => { });

        var console = new ConsoleOverlay(commands);
        console.Type("tele");

        Assert.True(console.Complete());
        Assert.Equal("teleport ", console.Input);
    }

    [Fact]
    public void CompletingSeveralMatchesTakesTheCommonPrefixAndListsThem() {
        var commands = new ConsoleCommands();
        commands.Register("quality", "", static _ => { });
        commands.Register("quantise", "", static _ => { });

        var console = new ConsoleOverlay(commands);
        console.Type("qu");

        console.Complete();

        Assert.Equal("qua", console.Input);
        Assert.Contains(commands.Output, line => line.Contains("quality", StringComparison.Ordinal));
    }

    [Fact]
    public void ControlCharactersAreNotTyped() {
        var console = new ConsoleOverlay(new());

        console.Type("ab\ncd\t");

        Assert.Equal("abcd", console.Input);
    }

    [Fact]
    public void SubmittingRunsTheLineAndClearsIt() {
        var commands = new ConsoleCommands();
        var ran = false;

        commands.Register("go", "", _ => ran = true);

        var console = new ConsoleOverlay(commands);
        console.Type("go");

        Assert.True(console.Submit());
        Assert.True(ran);
        Assert.Equal(string.Empty, console.Input);
    }

    /// <summary>
    ///     The host has to be able to tell that the game should not act on this frame's keys —
    ///     getting it wrong is how typing <c>reload</c> also makes the player reload.
    /// </summary>
    [Fact]
    public void AnOpenConsoleCapturesInput() {
        var console = new ConsoleOverlay(new()) { Enabled = true };

        Assert.True(console.IsCapturingInput);

        console.Enabled = false;

        Assert.False(console.IsCapturingInput);
    }

    [Fact]
    public void TheOverlayCommandTogglesAnOverlay() {
        var overlays = new DiagnosticOverlays();
        var stats = new FrameStatsOverlay { Enabled = false };

        overlays.Add(stats);

        var commands = new ConsoleCommands();
        overlays.RegisterCommands(commands);

        commands.Execute("overlay stats on");
        Assert.True(stats.Enabled);

        commands.Execute("overlay stats");
        Assert.False(stats.Enabled);

        commands.Execute("overlays");
        Assert.Contains(commands.Output, line => line.Contains("stats", StringComparison.Ordinal));
    }

    [Fact]
    public void TheConsoleDrawsItsPromptAndOutput() {
        var commands = new ConsoleCommands();
        commands.Execute("help");

        var overlays = new DiagnosticOverlays();
        overlays.Add(new ConsoleOverlay(commands) { Enabled = true });

        var draw = new DebugDraw();
        overlays.Draw(draw, new Vector2(1280f, 720f), GameTime.Zero);

        Assert.True(draw.ScreenCount > 0);
    }

    /// <summary>
    ///     ⚠ Nothing in the overlay pipeline clips, so a line longer than the panel is drawn straight
    ///     through the border and off the edge of the screen. Both the output and the typed line have
    ///     to be cut by the overlay itself.
    /// </summary>
    [Fact]
    public void NothingIsDrawnPastThePanelsEdge() {
        var commands = new ConsoleCommands();
        commands.Register("say", "", static entry => entry.Write(new string('x', 400)));
        commands.Execute("say");

        var console = new ConsoleOverlay(commands) { Enabled = true, Width = 300f };
        console.Type(new string('y', 400));

        var overlays = new DiagnosticOverlays();
        overlays.Add(console);

        var draw = new DebugDraw();
        overlays.Draw(draw, new Vector2(1280f, 720f), GameTime.Zero);

        var right = 1280f - overlays.Theme.Margin;

        foreach (var line in draw.ScreenLines) {
            Assert.True(line.From.X <= right, $"a stroke reaches x = {line.From.X}, past the panel at {right}");
            Assert.True(line.To.X <= right, $"a stroke reaches x = {line.To.X}, past the panel at {right}");
        }
    }

    /// <summary>The tail of a long line is shown, because that is where the caret is.</summary>
    [Fact]
    public void ALongTypedLineShowsItsEnd() {
        var console = new ConsoleOverlay(new()) { Enabled = true, Width = 300f };

        console.Type("teleport ");
        console.Type(new string('z', 200));
        console.Type("END");

        var draw = new DebugDraw();
        var overlays = new DiagnosticOverlays();
        overlays.Add(console);
        overlays.Draw(draw, new Vector2(1280f, 720f), GameTime.Zero);

        // Whole and unclipped in the buffer, whatever the panel shows — the overlay cuts the drawing
        // and not the line, so submitting still runs what was typed.
        Assert.Equal(212, console.Input.Length);
        Assert.EndsWith("END", console.Input, StringComparison.Ordinal);
    }
}
