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
///         <c>Samples/01</c> introduced and for the same reason.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        var frames = Frames(arguments);
        var project = Project(arguments);

        // ⚠ The surface has to be asked for at creation. SDL needs the Vulkan window flag when the
        // window is made, and one made without it has nothing to present to.
        using var platform = new DesktopPlatform(
            new() { Organisation = "Vixen", Application = "Editor", RequestGpuSurface = true }
        );

        using var window = platform.CreateWindow(
            new WindowOptions {
                Title = "Vixen Editor",
                Size = new Int2(1600, 1000),
                IsVisible = true,
                IsResizable = true
            }
        );

        using var host = new EditorHost(platform, window, project);
        return host.Run(frames);
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
