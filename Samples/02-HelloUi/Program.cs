// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Desktop;

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
///         ⚠ <b>And there is nothing here about hot reload, which is the point of how it is wired.</b>
///         Editing a <c>.vxml</c> or a <c>.vcss</c> while this is running updates the window, and what
///         turns that on is the <c>Vixen.Ui.Desktop.HotReload</c> reference in the project file —
///         conditioned on <c>Debug</c>, so a Release build does not resolve it and nothing in this
///         file changes. There is no flag to set and no <c>#if</c> to write. It used to be thirty
///         lines here, and thirty lines every application would have had to copy.
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

                // ⚠ **A factory, and in a development build it is also what a hot reload rebuilds
                // from.** An edit the runtime cannot patch makes the reload host construct a
                // replacement, and this lambda is the only thing that knows the shell takes a model.
                // Handed the instance alone a host falls back to the parameterless constructor, and
                // the shell comes up bound to a `ShellModel` nothing else holds — with the reload
                // still reporting success, because it did reload.
                Content = () => new Shell { Model = model },

                Stopping = _ => Report(model)
            },
            arguments
        );
    }

    /// <summary>Prints the arrangement the user left the window in.</summary>
    /// <remarks>
    ///     Written where an application would persist it, and printed rather than saved because a
    ///     sample that writes to somebody's home directory is a sample that has to be cleaned up.
    ///     What is being demonstrated is that the round trip exists.
    /// </remarks>
    static void Report(ShellModel model) => Console.WriteLine(model.Arrangement());
}
