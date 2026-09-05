// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>That the operating system's appearance reaches <c>@media (prefers-color-scheme: …)</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The query has worked since doc 43's F11 and until this file existed nothing outside a
///         test had ever written to it.</b> <c>MediaQuery</c> evaluates <c>prefers-color-scheme</c>,
///         <c>UiSurface.ColorScheme</c> holds it per surface, and the <i>only</i> two writers in the
///         whole repository were <c>MediaContextTests</c> and <c>PerSurfaceMediaTests</c>. F11 fed
///         width, height, resolution and gamut and left this one behind, so every application
///         shipped its light palette to a dark desktop — silently, and invisibly to the editor,
///         whose theme uses the class-based dark strategy and never asks the query at all.
///     </para>
///     <para>
///         <b>Asserted on a width rather than on <c>UiSurface.ColorScheme</c>.</b> A wire could set
///         the property while the cascade ignored it; a declaration that lives inside the media block
///         is the thing an application author actually observes. It is the same instrument
///         <c>MediaContextTests</c> uses one layer down, for the same reason.
///     </para>
///     <para>
///         ⚠ <b>What this prints on the day the wire is gone.</b> Red in both directions, and both
///         halves are needed: <see cref="A_dark_system_applies_the_dark_block" /> fails when nothing
///         feeds the scheme, and
///         <see cref="An_appearance_the_platform_never_answered_matches_neither_rule" /> fails when
///         something feeds it a guess — a host that flattened
///         <see cref="SystemColorScheme.Unknown" /> into light, or one that hard-coded dark, passes
///         exactly one of them.
///     </para>
/// </remarks>
[Collection(SerialUiDevelopment.Name)]
public class ColorSchemeWiringTests {
    /// <summary>Three widths that cannot both apply, so the measured one names the branch taken.</summary>
    /// <remarks>
    ///     The unconditional rule is the fallback CSS itself guarantees: with no preference expressed
    ///     <i>both</i> blocks are false and the element keeps 10px. That is what makes "no
    ///     preference" observable rather than indistinguishable from light.
    /// </remarks>
    const string Sheet = """
        .probe-panel { width: 10px; height: 20px; }

        @media (prefers-color-scheme: dark) {
            .probe-panel { width: 300px; }
        }

        @media (prefers-color-scheme: light) {
            .probe-panel { width: 200px; }
        }
        """;

    sealed class Probe : Component {
        public UiElement Panel { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Panel = ctx.Element(Root, "probe-panel");
            Panel.AddClass("probe-panel");
        }
    }

    static (UiApplication Application, Probe Content) Run(
        SystemColorScheme scheme,
        int frames = 2,
        Action<HeadlessPlatform, int>? each = null,
        Action<UiApplication>? started = null
    ) {
        var probe = new Probe();
        var platform = new HeadlessPlatform { ColorScheme = scheme };
        var frame = 0;

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(400, 300),
            Frames = frames,
            InstallSystemFont = false,
            InstallControlTheme = false,
            Styles = { Sheet },
            Content = () => probe,
            Started = started,
            Frame = each is null ? null : (_, _) => each(platform, frame++)
        };

        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(400, 300) });

        // ⚠ The platform's scheme is set before the loop starts, because the seed is read before the
        // first frame. A test that set it afterwards would be exercising the event path while
        // believing it exercised the seed.
        var application = new UiApplication(options, platform, window);
        application.Run();

        return (application, probe);
    }

    /// <summary>A dark system makes the dark block apply, from the first frame.</summary>
    [Fact]
    public void A_dark_system_applies_the_dark_block() {
        var (application, probe) = Run(SystemColorScheme.Dark);

        Assert.Equal(ColorSchemePreference.Dark, application.Document.ColorScheme);
        Assert.Equal(300f, probe.Panel.Width);
    }

    /// <summary>And a light one makes the light block apply.</summary>
    [Fact]
    public void A_light_system_applies_the_light_block() {
        var (application, probe) = Run(SystemColorScheme.Light);

        Assert.Equal(ColorSchemePreference.Light, application.Document.ColorScheme);
        Assert.Equal(200f, probe.Panel.Width);
    }

    /// <summary>A platform that could not answer matches neither block.</summary>
    /// <remarks>
    ///     ⚠ <b>The half a "just default to light" wire fails.</b> CSS says both queries are false
    ///     when the user has expressed nothing, and a headless run — or a Linux session with no
    ///     settings daemon — genuinely has expressed nothing. Answering light there is a stylesheet
    ///     taking a branch nobody asked for.
    /// </remarks>
    [Fact]
    public void An_appearance_the_platform_never_answered_matches_neither_rule() {
        var (application, probe) = Run(SystemColorScheme.Unknown);

        Assert.Equal(ColorSchemePreference.NoPreference, application.Document.ColorScheme);
        Assert.Equal(10f, probe.Panel.Width);
    }

    /// <summary>Changing the system's appearance mid-run restyles the interface.</summary>
    /// <remarks>
    ///     ⚠ <b>Expressed in frames rather than in milliseconds.</b> The platform queues the change
    ///     when it is set and the loop consumes it on its next pump, so "by the end of the run" is a
    ///     statement about order and is the same on an idle machine and a loaded one. A test that
    ///     slept would be this repository's largest flake source wearing a new hat.
    /// </remarks>
    [Fact]
    public void A_change_of_appearance_restyles_a_running_application() {
        var switched = false;

        var (application, probe) = Run(
            SystemColorScheme.Light,
            frames: 4,
            each: (platform, frame) => {
                if (frame != 0) {
                    return;
                }

                switched = true;
                platform.ColorScheme = SystemColorScheme.Dark;
            }
        );

        Assert.True(switched, "the per-frame hook never ran, so nothing was changed under the loop.");
        Assert.Equal(ColorSchemePreference.Dark, application.Document.ColorScheme);
        Assert.Equal(300f, probe.Panel.Width);
    }

    /// <summary>Every surface moves, not only the primary one.</summary>
    /// <remarks>
    ///     ⚠ <b>A torn-off panel is a second surface with a media context of its own.</b> It inherits
    ///     the scheme when it is created and would keep the old one for ever if the host wrote
    ///     <c>UiDocument.ColorScheme</c>, which is the primary surface's alone. An appearance is a
    ///     setting of the machine, so all of them move together; a gamut is negotiated per swapchain,
    ///     and that one deliberately does not follow.
    /// </remarks>
    [Fact]
    public void A_second_surface_follows_the_system_too() {
        UiSurface? second = null;

        var (application, _) = Run(
            SystemColorScheme.Light,
            frames: 4,
            started: app => second = app.Document.CreateSurface(200f, 150f),
            each: (platform, frame) => {
                if (frame == 0) {
                    platform.ColorScheme = SystemColorScheme.Dark;
                }
            }
        );

        Assert.NotNull(second);
        Assert.Equal(ColorSchemePreference.Dark, application.Document.Primary.ColorScheme);
        Assert.Equal(ColorSchemePreference.Dark, second.ColorScheme);
    }
}
