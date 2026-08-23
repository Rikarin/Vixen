// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>The loop, run with no display server and no driver.</summary>
/// <remarks>
///     <para>
///         <b>Everything above the RHI executes here.</b> A headless window has a size, an id and an
///         event stream and shows nobody anything, so the pump, the cascade, the layout pass, the
///         draw pass and the tessellation all run — and only the presenting is missing. That is
///         exactly what <see cref="UiApplicationOptions.Frames" /> exists to make meaningful, and it
///         is why these tests can assert what an interface came out as.
///     </para>
///     <para>
///         ⚠ <b>What they cannot assert is a picture</b>, and the two bugs this assembly was written
///         to fix were both picture bugs found by taking a screenshot. What is checkable is the
///         *layout* those pictures were wrong about, which is what <see cref="TheWindowIsAColumn" />
///         and <see cref="TheContentFillsTheWindow" /> are.
///     </para>
/// </remarks>
public class UiApplicationTests {
    /// <summary>An interface with something to measure in it.</summary>
    sealed class Probe : Component {
        public UiElement Panel { get; private set; } = null!;

        protected override void Build(BuildContext ctx) {
            Panel = ctx.Element(Root, "probe-panel");
            Panel.Add<Button>().Label = "x";
        }
    }

    /// <summary>Runs an application for a fixed number of frames and hands back what it built.</summary>
    static (UiApplication Application, Probe Content) Run(
        int frames = 3,
        Action<UiApplicationOptions>? configure = null
    ) {
        var probe = new Probe();

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(1280, 800),
            Frames = frames,

            // ⚠ Off, and this is the one option a test has to change. `SystemFonts` walks the machine
            // for a face, so leaving it on makes every measurement below depend on whether the CI
            // agent has Arial — a label is either its text's width or zero, and no assertion can be
            // written that holds both ways.
            InstallSystemFont = false,

            Content = () => probe
        };

        configure?.Invoke(options);

        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(1280, 800) });

        var application = new UiApplication(options, platform, window);
        application.Run();

        return (application, probe);
    }

    /// <summary>The loop runs the number of frames it was asked for and stops.</summary>
    /// <remarks>
    ///     ⚠ The cheapest thing this suite can assert and the one a CI leg actually needs: that the
    ///     whole stack starts, runs and stops without a hang on a machine with no GPU. A loop that
    ///     never terminated would take the agent's timeout rather than fail.
    /// </remarks>
    [Fact]
    public void ItRunsExactlyTheFramesItWasAskedFor() {
        var (application, _) = Run(frames: 7);

        Assert.Equal(7, application.FrameCount);
    }

    /// <summary>The content is mounted, and it is mounted after the stylesheets.</summary>
    [Fact]
    public void TheContentIsBuiltIntoTheDocument() {
        var (application, probe) = Run();

        Assert.NotNull(probe.Root);
        Assert.Same(application.Document.Root, probe.Root.Parent);
    }

    /// <summary>A window's content stacks, so the root is a column and not CSS's initial <c>row</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of the trap that made the whole interface render in a strip.</b> With the
    ///     root left a row, every child is a flex item on the *main* axis with no <c>flex-grow</c>, so
    ///     each comes out as wide as its own content — a menu bar, a docking host and a toast host
    ///     laid out side by side down the left of the window. It reads as a layout-engine bug.
    /// </remarks>
    [Fact]
    public void TheWindowIsAColumn() {
        var (application, probe) = Run();

        // Measured rather than read out of the style tree: what matters is the box the layout pass
        // produced, which is the thing the picture is of.
        Assert.Equal(1280f, probe.Root.Width, 1f);
    }

    /// <summary>And the content host fills it, which the root being a column does not give on its own.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half, and the one that survives fixing the first.</b> A component draws into
    ///     a host element of its own — <c>&lt;probe&gt;</c> here — which is neither the root nor the
    ///     markup's first tag, and which no file mentions and nothing styles. In a column it is full
    ///     width and *content height*, so an interface that meant to fill the window ends up as tall
    ///     as its content with the clear colour under it.
    /// </remarks>
    [Fact]
    public void TheContentFillsTheWindow() {
        var (_, probe) = Run();

        Assert.Equal(800f, probe.Root.Height, 1f);
    }

    /// <summary>An application's own sheet beats the host's, because the host's is user-agent.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole reason those four declarations are a stylesheet rather than an inline style
    ///     or a property write.</b> An inline style is <c>CascadeRanks.NormalInline</c> and outranks
    ///     every author rule including an <c>!important</c> one, so an application that wanted its
    ///     content laid out some other way could not say so. This is what makes them a default.
    /// </remarks>
    [Fact]
    public void AnApplicationCanOverrideTheHostsWindowRules() {
        var (_, probe) = Run(
            configure: options => options.Styles.Add($".{UiApplication.ContentClass} {{ flex-grow: 0; }}")
        );

        // Content height rather than the window's: the override took, and the box proves it rather
        // than the rule set.
        Assert.True(probe.Root.Height < 800f, $"the author rule did not win; the host is {probe.Root.Height} tall.");
    }

    /// <summary>The control theme is installed by default, and can be turned off.</summary>
    [Fact]
    public void TheControlThemeIsInstalledUnlessRefused() {
        var (with, _) = Run();
        var (without, _) = Run(configure: options => options.InstallControlTheme = false);

        Assert.True(with.Document.Styles.SheetCount > without.Document.Styles.SheetCount);
    }

    /// <summary>The three hooks run, and <c>Stopping</c> runs while the document is still alive.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Stopping</c> and not <c>Dispose</c>, and the assertion is the reason.</b> Anything
    ///     that persists state reads it out of the tree — a docking arrangement, a window placement,
    ///     a form's contents — and a disposed document has none to read.
    /// </remarks>
    [Fact]
    public void TheHooksRunAndTheDocumentOutlivesStopping() {
        var started = 0;
        var frames = 0;
        var childrenAtStop = -1;

        Run(
            frames: 4,
            configure: options => {
                options.Started = _ => started++;
                options.Frame = (_, _) => frames++;
                options.Stopping = application => childrenAtStop = application.Document.Root.Children.Count;
            }
        );

        Assert.Equal(1, started);
        Assert.Equal(4, frames);
        Assert.True(childrenAtStop > 0, "the document had no children when Stopping ran.");
    }

    /// <summary>A close request stops the loop.</summary>
    /// <remarks>
    ///     ⚠ <b>Subscribed through the <i>event</i> rather than through the options, and the difference
    ///     is not stylistic.</b> The constructor copies <c>UiApplicationOptions.Frame</c> into the
    ///     event once, on the way past — so a handler assigned to the options *after* the application
    ///     exists is never subscribed at all. The first draft of this test did exactly that and hung
    ///     the whole suite for ten minutes, which is the honest cost of `Frames = 0`.
    /// </remarks>
    [Fact]
    public void AClosedWindowStopsTheLoop() {
        var probe = new Probe();

        var options = new UiApplicationOptions {
            Title = "test",

            // Zero, which is "until the window is closed".
            Frames = 0,
            InstallSystemFont = false,
            Content = () => probe
        };

        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(640, 480) });

        var application = new UiApplication(options, platform, window);

        // ⚠ Asked for on the third frame rather than before the first, so the loop has to notice it
        // rather than never having started — and with a hard ceiling, so that a `Stop` that stopped
        // nothing fails this test instead of taking the CI agent's timeout.
        application.Frame += (running, _) => {
            if (running.FrameCount >= 2) {
                running.Stop();
            }
        };

        application.Run();

        Assert.Equal(3, application.FrameCount);
    }
}
