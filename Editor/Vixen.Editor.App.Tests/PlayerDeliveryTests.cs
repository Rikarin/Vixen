// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Debugger;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 50 § P2f: a player build is a use case on <see cref="IPlayerDelivery" />, and Deploy is a name for it.</summary>
/// <remarks>
///     <para>
///         <b>Two kinds of proof, as doc 50 § D2 asks.</b> The service is driven the way a plugin would
///         drive it — <c>Require</c>, then call — with no window, no menu and no panel open; and Deploy
///         is driven the way a person would, through the device panel's button, because that is the
///         wiring a service test cannot see.
///     </para>
///     <para>
///         ⚠ <b>The publish is substituted and nothing else is</b>, for the reason
///         <c>BuildStepContributionTests</c> gives: this suite does not shell out, and
///         <see cref="ContentTasks.Publisher" /> is the seam doc 36 built for exactly this.
///     </para>
/// </remarks>
public class PlayerDeliveryTests {
    /// <summary>A plugin builds a player through the service, and hears when it has finished.</summary>
    [Fact]
    public void A_player_build_is_a_use_case_a_plugin_can_drive() {
        using var session = EditorSession.Start();

        var published = Publishable(session);
        var delivery = session.Plugins.Services.Require<IPlayerDelivery>();

        Assert.Null(delivery.Refusal());

        var finished = 0;

        Assert.True(delivery.Build(launch: false, () => finished++));

        Until(session, () => finished == 1, "the build never reported that it had ended.");

        Assert.Equal(1, published());
        Assert.Equal(1, finished);
    }

    /// <summary>A refused build says so through its return value and never calls back.</summary>
    /// <remarks>
    ///     ⚠ <b>The half of the contract the diagnostics module leans on.</b> Deploy marks the row
    ///     before asking and puts it back when the answer is no; a refusal that also called back — or
    ///     one that returned true having started nothing — would leave a row reading Deploying.
    /// </remarks>
    [Fact]
    public void A_refused_build_starts_nothing_and_says_so() {
        using var session = EditorSession.Start();

        var delivery = session.Plugins.Services.Require<IPlayerDelivery>();

        // A scratch project has no .csproj, which is the ordinary first-run state.
        Assert.Contains(".csproj", delivery.Refusal(), StringComparison.Ordinal);

        var finished = 0;

        Assert.False(delivery.Build(launch: false, () => finished++));

        session.Frames(4);

        Assert.Equal(0, finished);
        Assert.False(session.Editor.Content.IsBusy);
    }

    /// <summary>
    ///     ⚠ Deploy is offered for this machine on a project that can be built — which it was not.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The defect this caught</b> (#1295). The module's refusal was
    ///     <c>deployer?.Refuse(device) ?? "This editor cannot build a player…"</c>, and a null refusal
    ///     is the deployer saying <i>yes</i> — so on exactly the projects where a build could run,
    ///     Deploy was greyed with a sentence saying the editor could not build one. The existing test
    ///     asked only about a project with no <c>.csproj</c>, where the two readings agree.
    /// </remarks>
    [Fact]
    public void Deploy_is_offered_for_this_machine_once_there_is_something_to_build() {
        using var session = EditorSession.Start();

        Publishable(session);

        var view = session.Control<DeviceManagerView>("devices");
        var local = Assert.Single(view.Devices.Items.Cast<DeviceEntry>(), device => device.Kind is DeviceKind.Local);

        Assert.Null(view.CanDeploy!(local));

        view.Devices.Select(0);
        session.Frames(2);

        Assert.False(view.DeployButton.Disabled, "Deploy is greyed on a project a build could publish.");
    }

    /// <summary>Pressing Deploy builds, marks the row while it does, and puts the row back when it ends.</summary>
    /// <remarks>
    ///     ⚠ <b>The publish is held open by the test</b>, so "Deploying" is a state the assertion can
    ///     see rather than one a fast pool thread might have left before the next frame — a property
    ///     of order, not of time.
    /// </remarks>
    [Fact]
    public void Pressing_Deploy_builds_and_marks_the_device_for_as_long_as_the_build_runs() {
        using var session = EditorSession.Start();

        File.WriteAllText(Path.Combine(session.ProjectRoot, "Game.csproj"), "<Project />");

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = 0;

        session.Editor.Content.Publisher = (_, _, _) => {
            Interlocked.Increment(ref published);
            return release.Task;
        };

        // ⚠ Deploy always launches, and the held publish is released as a failure below, so nothing
        // is ever started: a launch follows a publish that worked and only that.
        var view = session.Control<DeviceManagerView>("devices");

        view.Devices.Select(0);
        session.Frames(2);

        session.Click(view.DeployButton);

        Until(session, () => Volatile.Read(ref published) == 1, "Deploy never reached the publish.");

        var row = Assert.Single(view.Devices.Items.Cast<DeviceEntry>(), device => device.Kind is DeviceKind.Local);

        Assert.Equal(DeviceStatus.Deploying, row.Status);

        release.SetResult(false);

        Until(
            session,
            () => view.Devices.Items.Cast<DeviceEntry>().Single(device => device.Kind is DeviceKind.Local).Status
                is DeviceStatus.Available,
            "the row was left reading Deploying after the build ended."
        );
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
    ///     ⚠ <b>A frame count, and the ceiling is a hang check rather than a bound</b> — see
    ///     <c>BuildStepContributionTests.Until</c>, whose argument this is.
    /// </remarks>
    static void Until(EditorSession session, Func<bool> done, string what) {
        for (var frame = 0; frame < 400 && !done(); frame++) {
            session.Frame();
            Thread.Sleep(1);
        }

        Assert.True(done(), what);
    }
}
