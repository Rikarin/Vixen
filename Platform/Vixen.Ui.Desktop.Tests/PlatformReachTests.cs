// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>That an application can reach the operating system at all.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One missing property is what made three finished capabilities unreachable.</b>
///         <c>UiApplication.Run(options)</c> is the only public way to start an application and the
///         constructor is internal by design, so application code had the window and the document and
///         no route to the clipboard, the pickers, the displays or the lifecycle — all of which are
///         complete and tested in <c>Vixen.Platform</c>, and none of which <c>Vixen.Ui</c> is allowed
///         to name.
///     </para>
///     <para>
///         <b>Asserted through <see cref="UiApplicationOptions.Started" />, because that is the reach
///         an application actually has.</b> A test that read <c>application.Platform</c> off an object
///         it constructed itself would prove the property compiles and nothing about whether the
///         supported entry point can get to it.
///     </para>
/// </remarks>
[Collection(SerialUiDevelopment.Name)]
public class PlatformReachTests {
    sealed class Probe : Component {
        protected override void Build(BuildContext ctx) => ctx.Element(Root, "probe-panel");
    }

    /// <summary>The start hook is handed the platform the window was opened on.</summary>
    /// <remarks>
    ///     ⚠ <b>Reference equality with the platform the host was given, not merely non-null.</b> A
    ///     property that answered a freshly-constructed platform would compile, read correctly and
    ///     hand back a second SDL session whose windows are not this application's — and the
    ///     assertions below about the clipboard would still pass.
    /// </remarks>
    [Fact]
    public void An_application_reaches_the_platform_it_is_running_on() {
        IPlatform? reached = null;

        var platform = new HeadlessPlatform();

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(400, 300),
            Frames = 1,
            InstallSystemFont = false,
            Content = () => new Probe(),
            Started = app => reached = app.Platform
        };

        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(400, 300) });

        using var application = new UiApplication(options, platform, window);
        application.Run();

        Assert.Same(platform, reached);
        Assert.Same(window, application.Window);
    }

    /// <summary>And through it, the four services doc 49 named as unreachable.</summary>
    /// <remarks>
    ///     ⚠ <b>Each one is asked a question and its answer checked, not merely fetched for
    ///     non-nullness.</b> A property returning a fresh object of the right type would pass a
    ///     null check and hand the application a clipboard that is not the platform's — so what is
    ///     asserted is identity with the objects the host was given, plus the honest answer each
    ///     gives on a headless run: no clipboard, no pickers, no displays, no quit pending. Those
    ///     four "no"s are the point — they are what a real application's fallback path is written
    ///     against, and a mock that said yes to all of them would test nothing.
    /// </remarks>
    [Fact]
    public void The_clipboard_dialogs_displays_and_lifecycle_are_all_reachable() {
        var platform = new HeadlessPlatform();

        IClipboard? clipboard = null;
        var copied = true;
        var pickers = default(INativeDialogs?);
        var displays = -1;
        ILifecycle? lifecycle = null;
        var quitting = true;

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(400, 300),
            Frames = 1,
            InstallSystemFont = false,
            Content = () => new Probe(),
            Started = app => {
                clipboard = app.Platform.Clipboard;

                // False, and truthfully: a headless run has no clipboard to write to. The value that
                // matters is that the call was reachable and answered rather than throwing.
                copied = clipboard.SetText("copied");

                // Null, which is the answer: a headless platform reports no
                // `PlatformCapabilities.NativeDialogs`, so there is nothing to pick with — as opposed
                // to a picker that would answer every request with a cancellation.
                pickers = app.Platform.Pickers();

                displays = app.Platform.Displays.Displays.Count;

                lifecycle = app.Platform.Lifecycle;
                quitting = lifecycle.IsQuitRequested;
            }
        };

        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(400, 300) });

        using var application = new UiApplication(options, platform, window);
        application.Run();

        Assert.Same(platform.Clipboard, clipboard);
        Assert.False(copied);
        Assert.Null(pickers);
        Assert.Equal(0, displays);
        Assert.Same(platform.Lifecycle, lifecycle);
        Assert.False(quitting);
    }
}
