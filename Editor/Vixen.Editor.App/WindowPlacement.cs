// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Editor.Ui;
using Vixen.Platform;
using Vixen.Platform.Ui;

namespace Vixen.Editor.App;

/// <summary>How big the editor's window was, and where.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The window opened at a fixed 1600×1000 every time, whatever it was closed at.</b>
///         Everything else about the editor's shape persists — the arrangement, the keymap, the
///         theme, and doc 20's third bar is "the window is theirs" — so a window that forgets its own
///         size is the one thing on screen the user has to fix on every launch.
///     </para>
///     <para>
///         ⚠ <b>Its own file, read before the window exists.</b> The user store holds the layout, the
///         keymap and the theme and is created by <c>EditorApplication</c> — which needs a window to
///         exist first, because the shell is built against a surface size. So this reads and writes
///         one small file in the same directory rather than going through a store nothing can
///         construct that early.
///     </para>
///     <para>
///         ⚠ <b>Position is remembered and validated, size is remembered and clamped.</b> A window
///         restored onto a display that has since been unplugged is a window nobody can find and
///         cannot move — the commonest complaint about applications that persist geometry naively —
///         so a placement that is not on any current display is dropped and the platform is left to
///         put the window where it would have.
///     </para>
/// </remarks>
static class WindowPlacement {
    /// <summary>What the file is called, beside the layout and the keymap.</summary>
    public const string File = "window.yaml";

    /// <summary>The size a first run opens at.</summary>
    public static Int2 Default { get; } = new(1600, 1000);

    /// <summary>The smallest window that is still an editor rather than a strip of chrome.</summary>
    /// <remarks>
    ///     A saved size below this is not honoured. A window restored at 40×12 — which a bad shutdown
    ///     or a hand-edited file can produce — is one the user cannot resize back because the grips
    ///     are smaller than the pointer.
    /// </remarks>
    static readonly Int2 Smallest = new(640, 400);

    /// <summary>What the last session left, or the default.</summary>
    /// <param name="directory">The user's data directory.</param>
    /// <returns>The size, and the position if one was saved and is still on a display.</returns>
    public static (Int2 Size, Int2? Position) Load(string directory) {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        var store = new EditorUserStore(directory);

        if (store.Read(File) is not { } yaml) {
            return (Default, null);
        }

        YamlMapping saved;

        try {
            if (YamlReader.Read(yaml) is not YamlMapping mapping) {
                return (Default, null);
            }

            saved = mapping;
        } catch (YamlParseException) {
            // ⚠ A malformed file opens the default window rather than stopping the editor. This is
            // read before anything else exists — before the window, before the log, before there is
            // any way to tell somebody — so a throw here is a process that exits with a stack trace
            // because of a stray character in a file about window sizes.
            return (Default, null);
        }

        var size = new Int2(
            Number(saved, "width", Default.X),
            Number(saved, "height", Default.Y)
        );

        if (size.X < Smallest.X || size.Y < Smallest.Y) {
            size = Default;
        }

        return (size, saved["x"] is null || saved["y"] is null
            ? null
            : new Int2(Number(saved, "x", 0), Number(saved, "y", 0)));
    }

    /// <summary>Writes where the window is now.</summary>
    /// <param name="directory">The user's data directory.</param>
    /// <param name="window">The window.</param>
    /// <remarks>
    ///     ⚠ <b>Called on the way down, like everything else the editor persists.</b> A file written
    ///     on every resize event would be the noisiest thing on the disk during a single drag of a
    ///     window corner — the same argument <c>EditorApplication.Persist</c> makes about the layout.
    /// </remarks>
    public static void Save(string directory, IWindow window) {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(window);

        var size = window.ClientSize;

        if (size.X < Smallest.X || size.Y < Smallest.Y) {
            // A minimised or otherwise degenerate window is not a size to come back to.
            return;
        }

        var mapping = new YamlMapping()
            .Set("width", Scalar(size.X))
            .Set("height", Scalar(size.Y));

        var position = window.Position;

        // ⚠ Only where the platform positions windows at all. Elsewhere `Position` reads as zero,
        // and saving that would move the window to the top-left corner on the next launch — on a
        // platform whose whole point is that it decides where windows go.
        if (position != Int2.Zero) {
            mapping.Set("x", Scalar(position.X)).Set("y", Scalar(position.Y));
        }

        try {
            new EditorUserStore(directory).Write(File, YamlWriter.Write(mapping));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A window size that could not be written is not worth failing a shutdown over.
        }
    }

    /// <summary>Whether a saved position is on a display this machine currently has.</summary>
    /// <param name="platform">The platform, for the display list.</param>
    /// <param name="position">Where the window would go.</param>
    /// <param name="size">How big it would be.</param>
    /// <returns>Whether enough of it would be visible to grab.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The title bar has to be reachable, not the whole window.</b> A window half off
    ///         the right edge is one the user put there; a window entirely on a display that has been
    ///         unplugged is one they cannot see, cannot move and will conclude did not start.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Deferred to <see cref="PlatformWindowHost.IsReachable" /> rather than repeated
    ///         here.</b> A torn-off panel restored onto an unplugged display has exactly this problem
    ///         and the docking host has to answer it too — see <c>DockFloat</c> — so two copies of
    ///         the grip rule would be two places for "how much of a window counts as reachable" to
    ///         drift. One of them would then be right and nobody would know which.
    ///     </para>
    /// </remarks>
    public static bool IsVisible(IPlatform platform, Int2 position, Int2 size) =>
        PlatformWindowHost.IsReachable(platform, position.X, position.Y, size.X, size.Y);

    static int Number(YamlMapping mapping, string key, int fallback) =>
        mapping[key] is YamlScalar scalar
        && int.TryParse(scalar.Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    static YamlScalar Scalar(int value) => new(value.ToString(CultureInfo.InvariantCulture));
}
