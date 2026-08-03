// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui.Testing;

namespace Vixen.Editor.Testing;

/// <summary>How big the editor is, where it keeps its things, and how patient a command is.</summary>
/// <remarks>
///     ⚠ <b>The two directories are separate because the editor treats them as separate.</b> One
///     holds the user's layouts, keymap and theme; the other is the project being edited. A scenario
///     that restarts the editor with a fresh project and the same preferences — or the reverse — is
///     testing something real, and it cannot be written if the harness has conflated them.
/// </remarks>
public sealed class EditorSessionOptions {
    /// <summary>The window's width in device-independent pixels.</summary>
    /// <remarks>
    ///     The default is the same 1600×1000 <c>WindowPlacement.Default</c> opens a first run at. A
    ///     harness whose editor is a different shape from the shipped one is a harness where a panel
    ///     is off screen in the tests and on screen in the product.
    /// </remarks>
    public float Width { get; set; } = 1600f;

    /// <inheritdoc cref="Width" />
    public float Height { get; set; } = 1000f;

    /// <summary>Where the user's layouts, keymap and theme go, or <see langword="null" /> for a fresh one.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>The project to open, or <see langword="null" /> for the scratch one under the data directory.</summary>
    public string? ProjectRoot { get; set; }

    /// <summary>Where this session's contributions go, or <see langword="null" /> for one of its own.</summary>
    /// <remarks>
    ///     ⚠ <b>A registry per session by default, and it is not an optimisation.</b> The product
    ///     runs one editor per process and <c>EditorRegistry.Default</c> is right for it; a suite
    ///     runs several at once, and a plugin loaded by one session would then appear in another
    ///     session's Create menu, its inspector and its tool list. Set this to
    ///     <c>EditorRegistry.Default</c> only to assert something about generated registrations,
    ///     which land there and nowhere else.
    /// </remarks>
    public IEditorRegistry? Extensions { get; set; }

    /// <summary>Whether to borrow a font off the machine.</summary>
    /// <remarks>
    ///     ⚠ <b>On, and turning it off is almost always wrong.</b> A document with no font measures
    ///     every label at zero, and a row whose label is zero wide is one whose hit test lands
    ///     somewhere a person's click would not — so a suite that asserts nothing about text still
    ///     needs the font in order to click anything. It is a switch at all because a machine with no
    ///     usable face should fail with a sentence rather than with a hundred missed clicks.
    /// </remarks>
    public bool InstallFonts { get; set; } = true;

    /// <summary>What the document harness underneath is given.</summary>
    /// <remarks>
    ///     Its <c>FrameDelta</c> is what the gesture recogniser reads and its <c>RetryFrames</c> is
    ///     how long an assertion waits, so a slow scenario says so here rather than by sprinkling
    ///     frame counts through itself.
    /// </remarks>
    public UiTestOptions? Ui { get; set; }
}
