// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets.Content;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>Doc 36 § D4's build-step contribution: which steps run, in what order, and what stops one.</summary>
/// <remarks>
///     ⚠ <b>The stage filter is the half that fails silently.</b> A runner that ignored
///     <see cref="BuildStage" /> would run a packaging step before the artefact existed, which is not
///     a build error and not an exception — it is a signing step handed an empty directory. So every
///     case here asserts <i>which</i> steps ran and not merely that something did, and the ones that
///     enumerate assert the count as well, because a loop over an empty list passes every assertion
///     inside it.
/// </remarks>
public class BuildStepTests {
    static readonly BuildStepContext Anywhere =
        new("Linux", "Release", "/p/Game.csproj", "/p/Build/Linux", TextWriter.Null);

    /// <summary>A step of the stage being run runs; one of the other stage does not.</summary>
    [Fact]
    public async Task Only_the_steps_of_the_stage_being_run_are_run() {
        List<string> ran = [];

        BuildStep[] steps = [Step("before", BuildStage.BeforeBuild, ran), Step("after", BuildStage.AfterBuild, ran)];

        Assert.Null(await BuildSteps.RunAsync(steps, BuildStage.BeforeBuild, Anywhere, TestContext.Current.CancellationToken));
        Assert.Equal(["before"], ran);

        Assert.Null(await BuildSteps.RunAsync(steps, BuildStage.AfterBuild, Anywhere, TestContext.Current.CancellationToken));
        Assert.Equal(["before", "after"], ran);
    }

    /// <summary>
    ///     <c>Order</c> decides, and equal orders keep the order they were added in — which is what
    ///     makes a plugin's two steps run the way it wrote them.
    /// </summary>
    [Fact]
    public async Task Steps_run_in_order_and_ties_keep_their_registration_order() {
        List<string> ran = [];

        BuildStep[] steps = [
            Step("second", BuildStage.BeforeBuild, ran),
            Step("third", BuildStage.BeforeBuild, ran),
            Step("first", BuildStage.BeforeBuild, ran) with { Order = -10 }
        ];

        Assert.Null(await BuildSteps.RunAsync(steps, BuildStage.BeforeBuild, Anywhere, TestContext.Current.CancellationToken));

        Assert.Equal(["first", "second", "third"], ran);
    }

    /// <summary>
    ///     A refusal stops the build, names the step that refused, and nothing after it runs.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The id is in the message because a build that stopped anonymously is a reason nobody
    ///     can act on.</b> The step is in a plugin the person may not know is loaded.
    /// </remarks>
    [Fact]
    public async Task A_refusal_names_the_step_and_nothing_after_it_runs() {
        List<string> ran = [];

        BuildStep[] steps = [
            Step("sample.sign", BuildStage.BeforeBuild, ran, "there is no signing identity"),
            Step("sample.later", BuildStage.BeforeBuild, ran)
        ];

        var refusal = await BuildSteps.RunAsync(
            steps,
            BuildStage.BeforeBuild,
            Anywhere,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(refusal);
        Assert.Contains("sample.sign", refusal, StringComparison.Ordinal);
        Assert.Contains("there is no signing identity", refusal, StringComparison.Ordinal);

        Assert.Equal(["sample.sign"], ran);
    }

    /// <summary>A step is told the build it is in, rather than having to find one.</summary>
    [Fact]
    public async Task A_step_is_handed_the_target_the_variant_and_where_the_artefact_goes() {
        BuildStepContext? seen = null;

        BuildStep[] steps = [
            new("sample.read", BuildStage.AfterBuild, (context, _) => {
                seen = context;
                return ValueTask.FromResult<string?>(null);
            })
        ];

        Assert.Null(await BuildSteps.RunAsync(steps, BuildStage.AfterBuild, Anywhere, TestContext.Current.CancellationToken));

        Assert.NotNull(seen);
        Assert.Equal("Linux", seen.Target);
        Assert.Equal("Release", seen.Variant);
        Assert.Equal("/p/Game.csproj", seen.ProjectFile);
        Assert.Equal("/p/Build/Linux", seen.Output);
    }

    /// <summary>Cancelling between steps stops, rather than running the rest of the list.</summary>
    [Fact]
    public async Task Cancelling_stops_before_the_next_step() {
        List<string> ran = [];
        using var cancellation = new CancellationTokenSource();

        BuildStep[] steps = [
            new("sample.first", BuildStage.BeforeBuild, (_, _) => {
                ran.Add("sample.first");
                cancellation.Cancel();

                return ValueTask.FromResult<string?>(null);
            }),
            Step("sample.second", BuildStage.BeforeBuild, ran)
        ];

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await BuildSteps.RunAsync(steps, BuildStage.BeforeBuild, Anywhere, cancellation.Token)
        );

        Assert.Equal(["sample.first"], ran);
    }

    /// <summary>
    ///     ⚠ The instrument: with nothing contributed the answer is "nothing to say", not "refused".
    ///     A runner that returned a refusal for an empty list would stop every build in every project
    ///     that has no plugins, which is all of them.
    /// </summary>
    [Fact]
    public async Task Nothing_contributed_is_not_a_refusal() {
        Assert.Null(await BuildSteps.RunAsync([], BuildStage.BeforeBuild, Anywhere, TestContext.Current.CancellationToken));
        Assert.Empty(BuildSteps.Of([], BuildStage.AfterBuild));
    }

    static BuildStep Step(string id, BuildStage stage, List<string> ran, string? refusal = null) =>
        new(id, stage, (_, _) => {
            ran.Add(id);
            return ValueTask.FromResult(refusal);
        });
}
