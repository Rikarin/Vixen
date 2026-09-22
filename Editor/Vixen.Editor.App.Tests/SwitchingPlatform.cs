// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Vixen.Platform;
using Vixen.Platform.Headless;

namespace Vixen.Editor.App.Tests;

/// <summary>A headless platform whose semantic palette moves while the loop is running.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seam <see cref="EditorHost" /> does not have, and without it half of this host's
///         palette wiring cannot be told from the other half.</b> Both hosts read the platform in two
///         places — a seed before the first frame, because no desktop posts an event for the
///         appearance the machine already had, and a handler on
///         <see cref="PlatformEventKind.SystemColorSchemeChanged" />. <c>UiApplication</c> has a
///         per-frame hook a test can reach through, so there the two are separable by setting the
///         palette mid-run. <c>EditorHost.Run</c> has none and re-runs its seed on every call, so a
///         test that set the palette between two runs would be exercising the seed twice while
///         believing it had reached the handler.
///     </para>
///     <para>
///         <b>So the platform is the hook, which is also what a real one does.</b> A desktop's poll
///         notices a new palette and posts the appearance event from inside the pump; this notices a
///         frame count and does the same two things. The value genuinely differs between the seed's
///         read and the handler's, which is what makes deleting either line red on its own test.
///     </para>
///     <para>
///         ⚠ Everything else forwards to a real <see cref="HeadlessPlatform" /> rather than being
///         answered here. A stub more permissive than the runtime is one of this repository's named
///         instrument failures, and the editor's loop asks this object for windows, a file system, a
///         lifecycle and an input source on every frame.
///     </para>
/// </remarks>
/// <param name="inner">The platform everything but the palette comes from.</param>
/// <param name="after">How many pumps to run before the palette moves.</param>
/// <param name="moved">What it moves to.</param>
sealed class SwitchingPlatform(HeadlessPlatform inner, int after, SystemSemanticColors moved) : IPlatform {
    int pumps;

    /// <summary>Whether the palette actually moved, so a test cannot pass on a hook that never ran.</summary>
    public bool Switched { get; private set; }

    /// <inheritdoc />
    public SystemSemanticColors SemanticColors => Switched ? moved : SystemSemanticColors.Unknown;

    /// <inheritdoc />
    public string Name => inner.Name;

    /// <inheritdoc />
    public PlatformCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public IReadOnlyList<IWindow> Windows => inner.Windows;

    /// <inheritdoc />
    public IDisplayInfo Displays => inner.Displays;

    /// <inheritdoc />
    public SystemColorScheme ColorScheme => inner.ColorScheme;

    /// <inheritdoc />
    public SystemAccessibility Accessibility => inner.Accessibility;

    /// <inheritdoc />
    public SystemAccent Accent => inner.Accent;

    /// <inheritdoc />
    public IFileSystemHost FileSystem => inner.FileSystem;

    /// <inheritdoc />
    public IClipboard Clipboard => inner.Clipboard;

    /// <inheritdoc />
    public INativeDialogs Dialogs => inner.Dialogs;

    /// <inheritdoc />
    public ILifecycle Lifecycle => inner.Lifecycle;

    /// <inheritdoc />
    public IInputSource Input => inner.Input;

    /// <inheritdoc />
    public ITextInput TextInput => inner.TextInput;

    /// <inheritdoc />
    public IPowerInfo Power => inner.Power;

    /// <inheritdoc />
    public IProcessorTopology Processors => inner.Processors;

    /// <inheritdoc />
    public IWindow CreateWindow(in WindowOptions options) => inner.CreateWindow(options);

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) => inner.TryGetWindow(id, out window);

    /// <inheritdoc />
    public bool TryOpenUrl(string url) => inner.TryOpenUrl(url);

    /// <summary>
    ///     ⚠ Posted <em>before</em> the drain rather than after, so the event the flip queues is in
    ///     the span this same call returns. Posting after it would hand the host an empty pump and
    ///     leave the event for the next frame, which works only while the test asks for more frames
    ///     than it needs to.
    /// </summary>
    /// <returns>Whatever the inner platform has to say.</returns>
    public ReadOnlySpan<PlatformEvent> PumpEvents() {
        if (++pumps == after && !Switched) {
            Switched = true;
            inner.Post(PlatformEvent.Application(PlatformEventKind.SystemColorSchemeChanged, Stopwatch.GetTimestamp()));
        }

        return inner.PumpEvents();
    }

    /// <summary>Disposes nothing: the inner platform is owned by whoever made it.</summary>
    public void Dispose() { }
}
