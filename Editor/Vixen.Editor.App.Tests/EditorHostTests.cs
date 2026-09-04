// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Platform;
using Vixen.Platform.Headless;
using Vixen.Platform.Headless.Tests;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The host's own frame loop, run headless.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing built an <c>EditorHost</c> before this file.</b> <c>grep -rn "new
///         EditorHost"</c> had one hit — <c>Program.cs</c> — so every step of <c>Run</c> was
///         uncovered: pump, resize coalescing, tick, document update, <c>PlatformCursor.Apply</c>,
///         editor update, draw, sync, geometry, present. <see cref="EditorSession" /> is not that
///         coverage and does not claim to be: it runs the same four steps in the same order out of
///         its own <c>Frame</c>, which is a copy of the loop rather than the loop.
///     </para>
///     <para>
///         ⚠ <b>A smoke test, and the word is meant.</b> What it proves is that the host assembles
///         over a platform and a window, that a frame goes all the way through, and that it comes
///         back down again writing its layout and its window placement where it was told to. It
///         asserts nothing about what is on the screen — the startup Project Browser is what a first
///         run renders, and a test asserting its contents under this name would go red for any
///         greeter change while wearing a frame loop's name.
///     </para>
///     <para>
///         ⚠ <b>No GPU is needed and that is the design rather than the harness being lenient.</b>
///         <c>EnsureDevice</c> asks <c>window.Surface.Handle.CanPresent</c>, which a headless
///         window answers no, so <c>Present</c> returns before touching Vulkan and the other nine
///         steps run exactly as they do on a desktop. It is the same property that makes
///         <c>--frames N</c> a smoke test on a machine with no driver.
///     </para>
/// </remarks>
public class EditorHostTests {
    static (TemporaryFileSystemHost Files, HeadlessPlatform Platform, IWindow Window) Open() {
        var files = new TemporaryFileSystemHost();
        var platform = new HeadlessPlatform(new HeadlessPlatformOptions { FileSystem = files });

        var window = platform.CreateWindow(
            new WindowOptions { Title = "Vixen Editor", Size = new Int2(1280, 800) }
        );

        return (files, platform, window);
    }

    [Fact]
    public void The_host_assembles_over_a_window_and_runs_a_frame() {
        var (files, platform, window) = Open();

        using (files) {
            using (platform) {
                int code;

                using (var host = new EditorHost(platform, window)) {
                    code = host.Run(1);

                    // Nothing asked for another project, so `Program` would have stopped here.
                    Assert.Null(host.NextProject);
                }

                Assert.Equal(0, code);

                // The shell composed a title and the constructor pushed it, which is the one thing
                // the window can be asked that no other test can ask of this host.
                Assert.Contains("Vixen", window.Title, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The one assertion that a frame actually happened, rather than that the loop
    ///     returned.</b> Everything else here is true of a host that drew nothing: the title is
    ///     pushed by the constructor, and the layout and the placement are written after the loop
    ///     whether or not it went round. <c>Command</c> runs on the first drawn frame and nowhere
    ///     else, and an unknown one is exit code 2 — so this is red for a loop whose body never ran,
    ///     and it costs the frame nothing on the runs that pass no command.
    /// </summary>
    [Fact]
    public void The_one_shot_command_runs_on_the_first_drawn_frame() {
        var (files, platform, window) = Open();

        using (files) {
            using (platform) {
                using var host = new EditorHost(platform, window) { Command = "no.such.command" };
                Assert.Equal(2, host.Run(1));
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>Two frames rather than one, because the second is where a frame that only works
    ///     once fails.</b> The loop reuses the pane geometry it built, retires thumbnails between
    ///     frames and runs its one-shot command on the first pass only; a step that leaves state
    ///     behind is invisible in a single frame.
    /// </summary>
    [Fact]
    public void The_loop_runs_more_than_one_frame() {
        var (files, platform, window) = Open();

        using (files) {
            using (platform) {
                using var host = new EditorHost(platform, window);
                Assert.Equal(0, host.Run(3));
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The way down is part of the loop and writes two files.</b> <c>Persist</c> reads the
    ///     arrangement out of the docking host — which is why it is in <c>Loop</c> and not in
    ///     <c>Dispose</c>, where the document would already be gone — and <c>WindowPlacement.Save</c>
    ///     writes the geometry the next launch opens at. Both land in the data directory, so this is
    ///     also what proves the host wrote nowhere else: the assertion is over a throwaway one.
    /// </summary>
    [Fact]
    public void A_run_leaves_the_users_layout_and_window_placement_behind() {
        var (files, platform, window) = Open();

        using (files) {
            using (platform) {
                using var host = new EditorHost(platform, window);
                Assert.Equal(0, host.Run(1));
            }

            // Named files rather than "the directory is not empty": the editor writes into this
            // directory from the moment it opens a project, so a count would be green with the
            // whole of the way down removed.
            Assert.True(File.Exists(Path.Combine(files.DataDirectory, "window.yaml")), "window.yaml");
            Assert.True(File.Exists(Path.Combine(files.DataDirectory, "keybindings.yaml")), "keybindings.yaml");
        }
    }
}
