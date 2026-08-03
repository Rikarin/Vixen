// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Desktop;

namespace Vixen.Editor.App;

/// <summary>The editor's entry point.</summary>
/// <remarks>
///     <para>
///         <b>The editor is a Vixen application</b> — doc 11's first sentence, and this file is what
///         makes it literally true: a platform, a window, and a frame loop over a
///         <c>UiDocument</c>. There is no WPF, no Avalonia and no ImGui below this line.
///     </para>
///     <para>
///         ⚠ <b>Not <c>Vixen.App</c>, and the reason is the frame loop rather than the layering.</b>
///         That host exists to run a game: it builds an ECS world, a fixed-step accumulator and a
///         systems graph, and it is the right answer for a game and for the editor's play mode. The
///         editor's own loop is an interface — it has no world, and the moment it has one it has it
///         because a viewport is hosting a game rather than because the editor is one. Doc 17's "the
///         app is the executable" is the same argument from the other side.
///     </para>
///     <para>
///         <c>--frames N</c> runs exactly N frames and exits, which is how CI proves the whole stack
///         starts, presents and stops without a validation error or a hang — the same flag
///         <c>Samples/01</c> introduced and for the same reason. <c>--run ID</c> executes one editor
///         command on the first frame, which is how the frames after it prove that a background task
///         the editor started actually finished.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        var frames = Frames(arguments);

        // ⚠ The surface has to be asked for at creation. SDL needs the Vulkan window flag when the
        // window is made, and one made without it has nothing to present to.
        using var platform = new DesktopPlatform(
            new() { Organisation = "Vixen", Application = "Editor", RequestGpuSurface = true }
        );

        // ⚠ `--project` first, then the last project this user had open, then the scratch one. Doc
        // 20's A2 is blunt that "which project" is the first question an editor is asked and that
        // `--project` is not an answer for a user; reopening what they were in is what every editor
        // that keeps a recent list does, and the startup browser is what a first run gets instead.
        var project = Project(arguments) ?? LastProject(platform.FileSystem.DataDirectory);

        // ⚠ Before the window, because a window's size is decided when it is made. Everything else
        // about the editor's shape already persists — the arrangement, the keymap, the theme — and
        // doc 20's third bar is "the window is theirs", which a window that opens at a fixed size
        // however it was closed plainly is not.
        var (size, saved) = WindowPlacement.Load(platform.FileSystem.DataDirectory);

        // A position restored onto a display that has since been unplugged is a window nobody can
        // find and cannot move, which is the commonest way persisted geometry goes wrong. Dropped,
        // and the platform puts the window where it would have.
        var position = saved is { } where && WindowPlacement.IsVisible(platform, where, size)
            ? where
            : (Int2?) null;

        using var window = platform.CreateWindow(
            new WindowOptions {
                Title = "Vixen Editor",
                Size = size,
                Position = position,
                IsVisible = true,
                IsResizable = true
            }
        );

        // ⚠ A loop, and it is the whole of "open another project without restarting". Each pass owns
        // one editor over one project; choosing another leaves the loop's body, disposes everything
        // that editor built — world, documents, plugins, device — and builds the next one over the
        // same window. Doc 20 called swapping a project underneath a live editor the reason New and
        // Open Project were declared-and-disabled, and this is the answer: nothing is swapped.
        var command = Option(arguments, "--run");

        while (true) {
            var host = new EditorHost(platform, window, project) { Command = command };

            try {
                var code = host.Run(frames);

                if (code != 0 || host.NextProject is not { } next) {
                    return code;
                }

                project = next;

                // ⚠ The one-shot command and the frame budget belong to the run that was asked for.
                // A `--run` repeated against every project the user then opened would be a CI flag
                // that fires an unbounded number of times.
                command = null;
                frames = 0;
            } finally {
                host.Dispose();
            }
        }
    }

    /// <summary>The most recent project that is still on disk, or nothing.</summary>
    /// <remarks>
    ///     Read straight out of the user's history file, in the same position
    ///     <see cref="WindowPlacement" /> is read from and for the same reason: this happens before
    ///     there is a window, a log, or any way to tell somebody that it went wrong.
    /// </remarks>
    static string? LastProject(string dataDirectory) {
        foreach (var entry in new ProjectHistory(dataDirectory).Entries) {
            if (entry.Exists) {
                return entry.Path;
            }
        }

        return null;
    }

    /// <summary>Reads an option that takes a value, or null.</summary>
    static string? Option(string[] arguments, string name) {
        for (var i = 0; i < arguments.Length - 1; i++) {
            if (arguments[i] == name) {
                return arguments[i + 1];
            }
        }

        return null;
    }

    /// <summary>Reads <c>--project PATH</c>, or nothing for the scratch project.</summary>
    /// <remarks>
    ///     A directory that does not exist yet is fine and is the ordinary way to start one: the
    ///     asset database tolerates a missing <c>Assets/</c> and creates what it needs when something
    ///     is first written.
    /// </remarks>
    static string? Project(ReadOnlySpan<string> arguments) {
        for (var i = 0; i + 1 < arguments.Length; i++) {
            if (arguments[i] == "--project") {
                return arguments[i + 1];
            }
        }

        return null;
    }

    /// <summary>Reads <c>--frames N</c>, or zero for "until the window is closed".</summary>
    static int Frames(ReadOnlySpan<string> arguments) {
        for (var i = 0; i + 1 < arguments.Length; i++) {
            if (arguments[i] is "--frames" or "--vixen-frames"
                && int.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out var count)) {
                return Math.Max(0, count);
            }
        }

        return 0;
    }
}
