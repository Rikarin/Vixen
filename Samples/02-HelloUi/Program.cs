// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Desktop;
using Vixen.Ui.HotReload;

namespace Vixen.Samples.HelloUi;

/// <summary>A user interface, on a window, with no engine underneath it.</summary>
/// <remarks>
///     <para>
///         <b>What this proves is an absence.</b> docs/plan/02 § Samples describes 02-HelloUi as
///         "Vixen.Ui only, no engine — proves the UI/Engine boundary", and doc 15 makes it the thing
///         that proves the framework standalone before the editor is allowed to depend on it. There
///         is no <c>Vixen.App</c> here and there cannot be — <c>CheckArchitecture</c> fails the build
///         if anybody adds one — because <c>Vixen.App</c> references <c>Vixen.Engine</c> and the
///         sample would then prove nothing.
///     </para>
///     <para>
///         ⚠ <b>This file used to be five hundred lines, and the absence above is why.</b> It carried
///         a Vulkan device, a swapchain, a render graph, an atlas upload, resize coalescing and a
///         suboptimal-present rule, because the alternative was referencing the host that drags an
///         engine behind it. All of that is <c>Vixen.Ui.Desktop</c> now — a Platform/ assembly with a
///         window, a device and four steps of a frame in it and no scene anywhere — so the boundary
///         costs nothing to keep, and this is a bootstrap again.
///     </para>
///     <para>
///         <b>Start in <c>Shell.vxml</c>.</b> The interface is markup, a stylesheet and a model of
///         signals. This file says what the window is called and nothing about what is in it.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        // The interface's state, made here so that the sample can print what became of it. An
        // application with nothing to report constructs it in `Content` and never names it again.
        var model = new ShellModel();

        return UiApplication.Run(
            new UiApplicationOptions {
                Title = "Vixen — Hello UI",
                Organisation = "Vixen",
                Application = "HelloUi",
                Size = new Int2(1280, 800),

                // ⚠ **The generated sheet, and there is no code behind that name.**
                // `Theme/vixen.ui.vcss` is the tokens; every `.vxml` and every `.cs` in this project
                // is scanned for class names at build time; the rules for the ones actually used are
                // compiled into `VixenUtilityStyles` before the compiler runs. `Theme/shell.vcss` is
                // ahead of them in the same string, because the project file hands it in as the base
                // sheet. Nothing here walks a manifest or runs a scanner.
                //
                // ⚠ It is also the cheapest check that the wiring is there at all: a project whose
                // build step did not run compiles perfectly and produces an *empty* sheet, and every
                // class name in the markup then quietly does nothing.
                Styles = { VixenUtilityStyles.Css },

                // The root is the one element no markup owns. `shell.vcss` paints it; this says it
                // adds no padding of its own, so the menu bar sits against the window's edge.
                RootClasses = { "p-0" },

                // Docking, trees and property grids are a second package — see the project file — so
                // their theme is installed by the assembly that references them, which is this one.
                Configure = document => AdvancedTheme.Install(document),

                Content = () => new Shell { Model = model },

                Started = Watch,
                Stopping = _ => Report(model)
            },
            arguments
        );
    }

    /// <summary>Reloads <c>Theme/shell.vcss</c> while the window is open.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Six lines, and they are the authoring loop this sample exists to demonstrate.</b>
    ///         Save the stylesheet and the interface repaints with every element's identity intact —
    ///         the focus, the scroll offset, the docking arrangement and the tree's place in its
    ///         thousand rows all survive, because a style reload replaces rules rather than rebuilding
    ///         elements.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A reload <i>replaces</i>, it does not overlay, and that is what makes a deleted
    ///         rule stop applying.</b> Rules are appended and never removed — an index, a layer order
    ///         and a declaration arena all assume it — so the engine keeps the text of every sheet and
    ///         rebuilds from them. <c>Load</c> binds this path to the sheet the document already holds
    ///         where the text matches, so a save is a replacement at that sheet's own origin rather
    ///         than a second copy on top that says nothing where a rule was taken out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Development only.</b> A shipping application drops this method, the
    ///         <c>Vixen.Ui.HotReload</c> reference and nothing else — see that project's own file,
    ///         which says why it is neither trimmable nor AOT-compatible.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The source directory, not the output one.</b> <c>AppContext.BaseDirectory</c> is
    ///         <c>bin/Debug/net10.0</c>, where nobody edits anything: a watcher pointed there is
    ///         wired, correct at every step, and silent for ever.
    ///     </para>
    /// </remarks>
    static void Watch(UiApplication application) {
        if (Sources() is not { } directory) {
            return;
        }

        var sheet = Path.Combine(directory, "shell.vcss");

        var watcher = new HotReloadWatcher(new HotReloadHost(application.Document), directory);
        watcher.Load(sheet);

        // ⚠ **Which of the two things happened, said out loud, because the difference is invisible
        // until it bites.** `Load` binds a path to the sheet the document already holds when the two
        // texts match, and a save then *replaces* that sheet — so a rule taken out of the file stops
        // applying. When nothing matches it layers the file on top instead: changing a value still
        // works, and deleting a rule leaves whatever was underneath still applying, with nothing to
        // say so.
        //
        // ⚠ This sample gets the overlay, and the reason is worth knowing rather than working
        // around: `shell.vcss` is handed to the build as `VixenStyleBase`, so what the document holds
        // is the *generated* sheet with this file concatenated into the front of it — there is no
        // separate sheet whose text could match. That is the right arrangement for shipping (it is
        // what fixes the layer order and expands `@apply`) and the wrong one for deleting a rule at
        // run time, and `Replaces` is the API that lets a caller tell a developer which they have.
        Console.WriteLine(
            watcher.Replaces(sheet)
                ? $"watching {sheet} — saves replace the sheet, so deleting a rule takes effect."
                : $"watching {sheet} — saves layer over the generated sheet, so a *deleted* rule keeps applying "
                + "until the next build."
        );

        // ⚠ Applied on the frame loop's own thread rather than in the `FileSystemWatcher` callback.
        // The element tree has no lock and that callback is on a pool thread; `Poll` is also what
        // coalesces the three events one save raises — save-to-temp-then-rename, a truncate followed
        // by a write, a tool that touches the timestamp — into one reload.
        application.Frame += (_, _) => {
            foreach (var report in watcher.Poll()) {
                Console.WriteLine($"reloaded {report}");
            }
        };
    }

    /// <summary>Where this project's stylesheets are, if the sample is running from its own tree.</summary>
    static string? Sources() {
        for (var walk = new DirectoryInfo(AppContext.BaseDirectory); walk is not null; walk = walk.Parent) {
            var theme = Path.Combine(walk.FullName, "Theme");

            if (File.Exists(Path.Combine(theme, "shell.vcss"))) {
                return theme;
            }
        }

        return null;
    }

    /// <summary>Prints the arrangement the user left the window in.</summary>
    /// <remarks>
    ///     Written where an application would persist it, and printed rather than saved because a
    ///     sample that writes to somebody's home directory is a sample that has to be cleaned up.
    ///     What is being demonstrated is that the round trip exists.
    /// </remarks>
    static void Report(ShellModel model) => Console.WriteLine(model.Arrangement());
}
