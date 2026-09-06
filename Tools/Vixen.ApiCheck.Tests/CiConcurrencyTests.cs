// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ The one line in <c>ci.yml</c> that can stop CI from ever answering, held by evaluating it
///     rather than by matching its text.
/// </summary>
/// <remarks>
///     <para>
///         <c>cancel-in-progress: true</c> under a group keyed on <c>github.ref</c> means every push
///         to the default branch kills the run verifying the push before it. Between 2026-09-01 and
///         2026-09-06 that produced twelve consecutive <c>cancelled</c> conclusions on <c>master</c>
///         and no completed run at all — and a cancelled run is neither red nor green, so nothing
///         anywhere said master was unverified.
///     </para>
///     <para>
///         ⚠ That failure is invisible to every other check in this repository. The workflow is valid
///         YAML, the expression is a legal expression, and the runs it produces are not failures. The
///         only way to see it is to ask what the policy evaluates to for a push to the default
///         branch, which is what this does.
///     </para>
///     <para>
///         Here rather than in a suite of its own for the same reason <c>TestParallelismTests</c> is:
///         this is the assembly that already walks the repository to ask what the build reads. The
///         default branch name is read out of the workflow's own <c>on: push: branches:</c> list, so
///         renaming the branch cannot leave this asserting about a ref that no longer exists.
///     </para>
/// </remarks>
public sealed class CiConcurrencyTests {
    /// <summary>
    ///     A push to the default branch must not cancel the run already verifying the merge before
    ///     it. This is the assertion <c>cancel-in-progress: true</c> fails.
    /// </summary>
    [Fact]
    public void APushToTheDefaultBranchDoesNotCancelItsPredecessor() {
        var workflow = Workflow();
        var expression = Setting(workflow, "cancel-in-progress");
        var branch = DefaultBranch(workflow);

        Assert.False(
            Evaluate(expression, $"refs/heads/{branch}"),
            $".github/workflows/ci.yml says cancel-in-progress: {expression}, which is true for a push "
            + $"to {branch}. Every merge then cancels the run verifying the merge before it, and a "
            + "cancelled run is neither red nor green — so the default branch goes unverified while "
            + "reading as fine. Let master runs queue instead."
        );
    }

    /// <summary>
    ///     ⚠ The other half, without which the fix above is satisfied by switching cancellation off
    ///     everywhere: on a pull request only the tip is worth an answer, and superseded runs there
    ///     should still be cancelled.
    /// </summary>
    [Fact]
    public void APushToAPullRequestStillCancelsItsPredecessor() {
        var workflow = Workflow();
        var expression = Setting(workflow, "cancel-in-progress");

        Assert.True(
            Evaluate(expression, "refs/pull/17/merge"),
            $".github/workflows/ci.yml says cancel-in-progress: {expression}, which is false for a "
            + "pull request. Only the tip of a pull request is worth an answer; superseded runs there "
            + "are runner time spent on a diff nobody will read."
        );
    }

    /// <summary>
    ///     The group has to separate the refs, or the two tests above are asking about a policy that
    ///     one shared slot for the whole workflow makes meaningless.
    /// </summary>
    [Fact]
    public void TheConcurrencyGroupIsPerRef() {
        var group = Setting(Workflow(), "group");

        Assert.Contains("github.ref", group, StringComparison.Ordinal);
    }

    /// <summary>
    ///     GitHub's expression language, restricted to the shapes a concurrency policy is written in:
    ///     a bare boolean, or <c>${{ github.ref &lt;op&gt; '&lt;literal&gt;' }}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Anything else fails rather than being treated as either value. An instrument that cannot
    ///     read its subject must say so — the alternative is a test that goes quiet on exactly the
    ///     rewrite worth checking.
    /// </remarks>
    static bool Evaluate(string expression, string reference) {
        if (bool.TryParse(expression, out var constant)) {
            return constant;
        }

        var wrapper = Regex.Match(expression, @"^\$\{\{(?<body>.*)\}\}$", RegexOptions.Singleline);

        Assert.True(
            wrapper.Success,
            $"cancel-in-progress is '{expression}', which is neither a boolean nor a ${{{{ }}}} "
            + "expression. This test evaluates the policy rather than matching it; widen Evaluate to "
            + "cover the new shape."
        );

        var comparison = Regex.Match(
            wrapper.Groups["body"].Value.Trim(),
            @"^github\.ref\s*(?<operator>==|!=)\s*'(?<literal>[^']*)'$"
        );

        Assert.True(
            comparison.Success,
            $"cancel-in-progress is '{expression}'. This test understands a single comparison of "
            + "github.ref against a quoted ref; widen Evaluate to cover the new shape rather than "
            + "leaving the policy unasserted."
        );

        var equal = string.Equals(reference, comparison.Groups["literal"].Value, StringComparison.Ordinal);

        return comparison.Groups["operator"].Value == "==" ? equal : !equal;
    }

    /// <summary>
    ///     Reads one <c>key: value</c> out of the workflow's top-level <c>concurrency:</c> block.
    /// </summary>
    /// <remarks>
    ///     Two spaces of indentation and a plain scalar is the whole grammar a concurrency block uses,
    ///     so this reads it directly rather than adding a YAML parser to a project that has none. A
    ///     block that stops looking like that fails to find the key, which is red.
    /// </remarks>
    static string Setting(string workflow, string key) {
        var block = Regex.Match(
            workflow,
            @"^concurrency:\s*$(?<body>(?:\n(?:[ \t].*)?)*)",
            RegexOptions.Multiline
        );

        Assert.True(
            block.Success,
            ".github/workflows/ci.yml has no top-level concurrency: block. Without one every push "
            + "starts a run that nothing supersedes, which is a different policy from the one this "
            + "asserts — say which it is here."
        );

        var setting = Regex.Match(
            block.Groups["body"].Value,
            $@"^\s+{Regex.Escape(key)}:\s*(?<value>\S.*?)\s*$",
            RegexOptions.Multiline
        );

        Assert.True(setting.Success, $"The concurrency: block in .github/workflows/ci.yml declares no {key}.");

        return setting.Groups["value"].Value;
    }

    /// <summary>
    ///     The branch <c>ci.yml</c> itself says a push runs on, so the ref this asserts about is the
    ///     one CI really receives.
    /// </summary>
    static string DefaultBranch(string workflow) {
        var branches = Regex.Match(workflow, @"^\s+branches:\s*\[(?<names>[^\]]+)\]\s*$", RegexOptions.Multiline);

        Assert.True(
            branches.Success,
            ".github/workflows/ci.yml declares no push branches, so which branch is the default one "
            + "this policy protects is no longer written down anywhere CI can read."
        );

        return branches.Groups["names"].Value.Split(',')[0].Trim().Trim('\'', '"');
    }

    static string Workflow() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
