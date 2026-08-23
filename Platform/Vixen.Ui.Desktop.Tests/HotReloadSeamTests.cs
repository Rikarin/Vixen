// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.HotReload;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>That a development build can put its interface under a reload host.</summary>
/// <remarks>
///     <para>
///         <b>Two channels, two mechanisms, and only one of them used to work.</b> A <c>.vcss</c>
///         save is a file watcher; a <c>.vxml</c> save is a *recompile* — the file has to become a
///         different <c>Build</c> method before there is anything new to run — so the markup channel
///         is driven by the runtime's metadata update and rebuilds whatever a <c>HotReloadHost</c>
///         was tracking at the moment of mounting.
///     </para>
///     <para>
///         ⚠ <b>Which is why the failure was silent.</b> The sample mounted its shell through
///         <c>BuildContext.BuildInto</c>, so the host tracked nothing, <c>ReloadComponents</c> walked
///         an empty list and reported success — over zero components. Saving a <c>.vxml</c> did
///         nothing at all, with no diagnostic anywhere. The assertion that catches that is a count,
///         and it is the reason this file exists.
///     </para>
/// </remarks>
public class HotReloadSeamTests {
    sealed class Probe : Component {
        public int Builds { get; private set; }

        protected override void Build(BuildContext ctx) {
            Builds++;
            ctx.Element(Root, "probe-panel").Add<Button>().Label = "x";
        }
    }

    static (UiApplication Application, HotReloadHost Reload, Probe[] Probes) Run(int frames = 2) {
        var probes = new List<Probe>();
        HotReloadHost? reload = null;

        var options = new UiApplicationOptions {
            Title = "test",
            Size = new Int2(800, 600),
            Frames = frames,
            InstallSystemFont = false,

            Mount = (document, root) => {
                reload = new HotReloadHost(document);

                var probe = reload.Mount<Probe>(root);
                probes.Add(probe);

                return probe;
            }
        };

        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(800, 600) });

        var application = new UiApplication(options, platform, window);
        application.Run();

        return (application, reload!, [.. probes]);
    }

    [Fact]
    public void The_mount_hook_is_what_builds_the_interface() {
        var (application, _, probes) = Run();

        Assert.Single(probes);
        Assert.Same(probes[0], application.Content);
        Assert.Same(application.Document.Root, probes[0].Root.Parent);
    }

    /// <summary>And the host is tracking it, which is the whole of what a markup reload needs.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is the assertion.</b> A host with no components reloads successfully and
    ///     changes nothing — `ReloadComponents` walks the list it has, reports what it did, and an
    ///     empty list is a clean report. So "did it reload" is the wrong question and "how many did
    ///     it have to reload" is the right one.
    /// </remarks>
    [Fact]
    public void The_mounted_component_is_tracked_and_can_be_rebuilt() {
        var (_, reload, probes) = Run();

        Assert.Single(reload.Components);
        Assert.Same(probes[0], reload.Components[0]);

        var built = probes[0].Builds;
        var report = reload.ReloadComponents();

        Assert.True(report.Succeeded, string.Join("; ", report.Errors));
        Assert.Equal(1, report.Components);
        Assert.True(probes[0].Builds > built, "Build did not run again, so nothing was reloaded.");
    }

    /// <summary>A component's own object survives a markup reload, and so do its fields.</summary>
    /// <remarks>
    ///     That is most of what "state was preserved" means in practice — a component's signals are
    ///     fields, and the object holding them is the same one. ⚠ The *elements* do not survive and
    ///     cannot: two <c>Build</c> bodies are two different programs, and reconciling on position
    ///     alone would move state onto whatever happened to be in the same slot.
    /// </remarks>
    [Fact]
    public void A_markup_reload_keeps_the_component_and_replaces_its_elements() {
        var (_, reload, probes) = Run();

        var before = probes[0].Root;
        reload.ReloadComponents();

        Assert.Same(probes[0], reload.Components[0]);
        Assert.Same(before, probes[0].Root);
        Assert.NotEmpty(probes[0].Root.Children);
    }

    /// <summary>Without the hook the host is empty, which is the state this whole seam fixed.</summary>
    /// <remarks>
    ///     ⚠ <b>A characterisation test, kept deliberately.</b> It asserts the *old* behaviour — that
    ///     a component mounted the ordinary way is invisible to a reload host — so that the
    ///     difference the hook makes is written down rather than remembered, and so that anyone who
    ///     makes `UiApplication` track components itself has to come here and delete this.
    /// </remarks>
    [Fact]
    public void Content_alone_leaves_the_reload_host_with_nothing_to_do() {
        var probe = new Probe();

        var options = new UiApplicationOptions {
            Title = "test",
            Frames = 1,
            InstallSystemFont = false,
            Content = () => probe
        };

        var platform = new HeadlessPlatform();
        var window = platform.CreateWindow(new WindowOptions { Title = "test", Size = new Int2(800, 600) });

        var application = new UiApplication(options, platform, window);
        application.Run();

        var reload = new HotReloadHost(application.Document);
        var report = reload.ReloadComponents();

        Assert.Empty(reload.Components);

        // ⚠ Succeeded, over nothing. This is the report the sample was printing before the hook
        // existed, and it is why the bug survived being looked at.
        Assert.True(report.Succeeded);
        Assert.Equal(0, report.Components);
    }
}
