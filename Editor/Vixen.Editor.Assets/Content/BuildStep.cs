// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Assets.Content;

/// <summary>Where in a player build a contributed step runs.</summary>
/// <remarks>
///     ⚠ <b>Two moments rather than a DAG, and the difference is what a step can be told.</b> Doc 08
///     describes a build-step graph over <i>assets</i> — that is <see cref="BuildPlanner" />, and it
///     already exists. This is the other thing the same words were used for in doc 36 § D4 and doc 11:
///     a hook in the <i>player</i> build, which is a fixed sequence of import, pack, publish and
///     launch. A step that ran between the import and the pack would be one that could see neither the
///     project it came from nor the artefact it is meant to touch, so the two useful moments are the
///     two ends.
/// </remarks>
public enum BuildStage {
    /// <summary>Before anything is imported, which is the last moment a build can be refused cheaply.</summary>
    BeforeBuild,

    /// <summary>After the publish produced an artefact and before it is launched.</summary>
    /// <remarks>
    ///     ⚠ <b>Before the launch and not after it.</b> A launch is awaited for as long as the game is
    ///     up — see <c>ContentTasks.BuildPlayer</c> — so a step scheduled after it would be a
    ///     packaging step that ran when the player was closed, which is never for a build that was
    ///     only built.
    /// </remarks>
    AfterBuild
}

/// <summary>What a build step is told about the build it is running in.</summary>
/// <param name="Target">The platform, as <see cref="PlayerBuild.Targets" /> spells it.</param>
/// <param name="Variant">Which of doc 17's variants.</param>
/// <param name="ProjectFile">The <c>.csproj</c> that is the game.</param>
/// <param name="Output">Where the artefact goes, which for <see cref="BuildStage.AfterBuild" /> is where it is.</param>
/// <param name="Log">The build log, which in the editor is the Console panel's ring.</param>
/// <remarks>
///     ⚠ <b>Resolved values, not the settings object.</b> The panel stays editable while a build runs,
///     so a step handed <c>PlayerBuildSettings</c> would be a step reading a target the person changed
///     after pressing the button — the same reason <c>PlayerBuildRequest</c> is a snapshot.
/// </remarks>
public sealed record BuildStepContext(string Target, string Variant, string ProjectFile, string Output, TextWriter Log);

/// <summary>Something extra a player build does, contributed by a module, a plugin or a project script.</summary>
/// <param name="Id">What it is called, which is what a refusal is reported against.</param>
/// <param name="Stage">When it runs.</param>
/// <param name="Run">The work, returning why the build must stop or <see langword="null" />.</param>
/// <remarks>
///     <para>
///         <b>Doc 36 § D4's build-step row and doc 11's second unreachable extension point, on P2's
///         terms.</b> A contribution kind is a record in the assembly that owns it and
///         <c>IEditorRegistry.Add</c> is the whole surface — there is no method on
///         <c>PluginContext</c> for this and deliberately is not.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Editor.Core</c>, because what a step is handed is
///         <see cref="PlayerBuild" />'s vocabulary.</b> A plugin that wants to write one references
///         this assembly, which is the same bargain <see cref="ImporterContributions" /> struck: the
///         contract assembly does not drag a two-dozen-format model importer into every plugin that
///         only adds a menu item.
///     </para>
///     <para>
///         ⚠ <b>A refusal is a string, like every other refusal in this subsystem.</b>
///         <c>BuildRefusal</c>, <c>DeployRefusal</c> and <c>SceneTrouble</c> all answer "why not, or
///         null", and a step that threw instead would take the whole task down with a stack trace
///         where a sentence belongs. An exception out of a step is still an exception — this is the
///         way to stop a build, not the way to survive a bug.
///     </para>
/// </remarks>
public sealed record BuildStep(
    string Id,
    BuildStage Stage,
    Func<BuildStepContext, CancellationToken, ValueTask<string?>> Run
) {
    /// <summary>What order steps of one stage run in; equal orders keep the order they were added in.</summary>
    /// <remarks>
    ///     ⚠ <b>A number rather than a dependency edge.</b> Doc 08's asset graph needs edges because an
    ///     asset's inputs are discovered; a player build's steps are a handful of things somebody
    ///     wrote down, and a <c>Depends</c> list would be a cycle detector and a topological sort for a
    ///     list that is usually one long.
    /// </remarks>
    public int Order { get; init; }
}

/// <summary>Running the contributed steps of one stage.</summary>
/// <remarks>
///     ⚠ <b>Separate from the sequence that calls it, so both stages have a test.</b> The
///     after-build stage of a real player build is only reachable through a <c>dotnet publish</c>,
///     which the editor's suites deliberately do not shell out to — so the arm that runs it would
///     otherwise be a call site nothing ever executed, which is this repository's commonest defect
///     shape.
/// </remarks>
public static class BuildSteps {
    /// <summary>Runs every step of a stage, in order, and stops at the first refusal.</summary>
    /// <param name="steps">Everything contributed, of every stage.</param>
    /// <param name="stage">The stage to run.</param>
    /// <param name="context">What the steps are told.</param>
    /// <param name="cancellationToken">Cancels between steps and inside one that honours it.</param>
    /// <returns>Why the build must stop, or <see langword="null" /> when every step was happy.</returns>
    /// <remarks>
    ///     ⚠ <b>The refusal names the step.</b> A build that stopped saying "not signed" with nothing
    ///     saying who said it is a build whose reason is in a plugin the person may not know is
    ///     loaded — which is the failure mode of every anonymous hook.
    /// </remarks>
    public static async ValueTask<string?> RunAsync(
        IEnumerable<BuildStep> steps,
        BuildStage stage,
        BuildStepContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var step in Of(steps, stage)) {
            cancellationToken.ThrowIfCancellationRequested();

            if (await step.Run(context, cancellationToken).ConfigureAwait(false) is { } refusal) {
                return $"{step.Id}: {refusal}";
            }
        }

        return null;
    }

    /// <summary>The steps of one stage, in the order they will run.</summary>
    /// <param name="steps">Everything contributed, of every stage.</param>
    /// <param name="stage">The stage.</param>
    /// <returns>The steps, ordered.</returns>
    /// <remarks>
    ///     <c>OrderBy</c> is stable, which is what makes "equal orders keep the order they were added
    ///     in" true rather than a sentence in a doc comment.
    /// </remarks>
    public static IEnumerable<BuildStep> Of(IEnumerable<BuildStep> steps, BuildStage stage) {
        ArgumentNullException.ThrowIfNull(steps);

        return steps.Where(step => step.Stage == stage).OrderBy(static step => step.Order);
    }
}
