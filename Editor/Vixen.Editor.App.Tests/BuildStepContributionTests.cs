// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § D4's build-step row, asserted on a build rather than on the registry.</summary>
/// <remarks>
///     <para>
///         <b>P2's rule about how to test a contribution, applied.</b> The first <c>[Overlay]</c> test
///         asserted the record was in the registry — "which passes with <c>ViewportChrome</c> never
///         reading it". So nothing here reads <c>IEditorRegistry</c>: what is asserted is that
///         pressing Build ran the contributed step, that its refusal stopped the build before anything
///         was spent, and that withdrawing the contribution lets the same build through.
///     </para>
///     <para>
///         ⚠ <b>The publish is substituted and nothing else is.</b> This suite does not shell out —
///         see <c>BuildSettingsTests</c> — and the after-build stage runs between a
///         <c>dotnet publish</c> and a launch, so without the seam that call site could never once
///         have executed. The import, the pack, the two stages and their order are the production
///         ones; see <see cref="ContentTasks.Publisher" />.
///     </para>
/// </remarks>
public class BuildStepContributionTests {
    /// <summary>
    ///     A step contributed before the build runs during it, and its refusal stops the build with
    ///     the step's own id in the message.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The publish counter is the half that says "stopped".</b> A build whose step ran and
    ///     which then published anyway would pass an assertion about the step alone — and that is the
    ///     failure a pre-build gate exists to prevent, since the whole point of refusing before the
    ///     import is that nothing is spent.
    /// </remarks>
    [Fact]
    public void A_contributed_step_runs_in_a_build_and_its_refusal_stops_it() {
        var registry = new EditorRegistry();
        using var session = EditorSession.Start(new() { Extensions = registry });

        var published = Publishable(session);
        var ran = 0;

        using var scope = registry.Add(
            new BuildStep("sample.gate", BuildStage.BeforeBuild, (_, _) => {
                Interlocked.Increment(ref ran);
                return ValueTask.FromResult<string?>("the licence file is missing");
            })
        );

        session.Frames(2);
        Assert.True(session.CanRun("build.run"), "the project has a .csproj and Build and Run is still refused.");

        session.Run("build.run");
        Until(session, () => Volatile.Read(ref ran) > 0, "the contributed build step never ran.");

        Settled(session);

        Assert.Equal(1, Volatile.Read(ref ran));

        // Nothing was spent: the refusal arrived before the import, so no publish was attempted.
        Assert.Equal(0, published());

        var said = session.Shell.Notifications.History
            .Select(message => message.Message + " " + (message.Detail ?? string.Empty))
            .ToList();

        Assert.Contains(said, text => text.Contains("sample.gate", StringComparison.Ordinal));
        Assert.Contains(said, text => text.Contains("the licence file is missing", StringComparison.Ordinal));
    }

    /// <summary>
    ///     And withdrawing the contribution lets the same build through, which is the half that says
    ///     the registry is read per build rather than once.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one failure the plugin arrangement exists to prevent, in this kind.</b> A step is
    ///     a delegate the plugin supplied, so one kept after an unload is a build calling into an
    ///     assembly nothing else refers to.
    /// </remarks>
    [Fact]
    public void Withdrawing_the_step_lets_the_next_build_past_it() {
        var registry = new EditorRegistry();
        using var session = EditorSession.Start(new() { Extensions = registry });

        var published = Publishable(session);
        var ran = 0;

        var scope = registry.Add(
            new BuildStep("sample.gate", BuildStage.BeforeBuild, (_, _) => {
                Interlocked.Increment(ref ran);
                return ValueTask.FromResult<string?>("not yet");
            })
        );

        session.Frames(2);
        session.Run("build.run");
        Until(session, () => Volatile.Read(ref ran) > 0, "the contributed build step never ran.");
        Settled(session);

        scope.Dispose();

        session.Run("build.run");
        Until(session, () => published() > 0, "the build never reached the publish after the step was withdrawn.");
        Settled(session);

        // Still once: the withdrawn step did not run a second time.
        Assert.Equal(1, Volatile.Read(ref ran));
    }

    /// <summary>
    ///     An after-build step runs once there is an artefact — after the publish and before the
    ///     launch — and is told where it is.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The order is the assertion, not the fact that both happened.</b> A packaging or
    ///     signing step is the work this stage is for, and one that ran before the publish would be
    ///     handed an output directory that is empty — which is not an exception and not a build error.
    /// </remarks>
    [Fact]
    public void An_after_build_step_runs_after_the_publish_and_is_told_where_the_artefact_is() {
        var registry = new EditorRegistry();
        using var session = EditorSession.Start(new() { Extensions = registry });

        List<string> order = [];
        BuildStepContext? seen = null;

        File.WriteAllText(Path.Combine(session.ProjectRoot, "Game.csproj"), "<Project />");

        session.Editor.Content.Publisher = (_, _, _) => {
            lock (order) {
                order.Add("publish");
            }

            return Task.FromResult(true);
        };

        using var scope = registry.Add(
            new BuildStep("sample.package", BuildStage.AfterBuild, (context, _) => {
                lock (order) {
                    order.Add("sample.package");
                }

                seen = context;
                return ValueTask.FromResult<string?>(null);
            })
        );

        session.Frames(2);

        // The window's Build button rather than Build and Run, so nothing is launched: the stage
        // under test is the one between the publish and a launch that does not happen.
        var view = session.Control<BuildSettingsView>(EditorApplication.BuildPanel);

        view.Rebuild();
        Assert.False(view.BuildButton.Disabled, "Build is greyed on a project that has a .csproj.");

        session.Click(view.BuildButton);

        Until(
            session,
            () => {
                lock (order) {
                    return order.Contains("sample.package");
                }
            },
            "the after-build step never ran."
        );

        Settled(session);

        lock (order) {
            Assert.Equal(["publish", "sample.package"], order);
        }

        Assert.NotNull(seen);
        Assert.Equal(session.Project.Settings.Get<PlayerBuildSettings>().Variant, seen.Variant);
        Assert.NotEmpty(seen.Output);
        Assert.EndsWith("Game.csproj", seen.ProjectFile, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Gives the project something to publish, and something that is not <c>dotnet</c> to publish
    ///     it — and hands back how many times the publish has been reached.
    /// </summary>
    static Func<int> Publishable(EditorSession session) {
        File.WriteAllText(Path.Combine(session.ProjectRoot, "Game.csproj"), "<Project />");

        var published = 0;

        session.Editor.Content.Publisher = (_, _, _) => {
            Interlocked.Increment(ref published);
            return Task.FromResult(true);
        };

        return () => Volatile.Read(ref published);
    }

    /// <summary>Pumps frames until something a pool thread does has happened, or gives up.</summary>
    /// <remarks>
    ///     ⚠ <b>A frame count, and the ceiling is a hang check rather than a bound.</b> What is
    ///     awaited is a step running on the pool, so there is no counter on the frame thread to watch
    ///     — and a wall-clock budget calibrated on an idle machine is this repository's largest flake
    ///     source. Four hundred frames of the editor is not a claim about how long a build step takes;
    ///     it is a number a working build reaches in tens and a broken one never reaches at all.
    /// </remarks>
    static void Until(EditorSession session, Func<bool> done, string what) {
        for (var frame = 0; frame < 400 && !done(); frame++) {
            session.Frame();
            Thread.Sleep(1);
        }

        Assert.True(done(), what);
    }

    /// <summary>Runs the frames the notification and the task centre need to catch up.</summary>
    static void Settled(EditorSession session) {
        Until(session, () => !session.Editor.Content.IsBusy, "the build task never finished.");
        session.Settle();
    }
}
