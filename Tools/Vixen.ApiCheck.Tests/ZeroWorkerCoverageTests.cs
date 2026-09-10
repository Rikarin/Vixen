// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.RegularExpressions;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ Every subsystem that dispatches job work owns a fixture that runs it with <b>no workers at
///     all</b>, because that count is the browser rather than a slow desktop.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/10</c> § Web asks for a mode that runs the suite single-threaded, and
///         <see href="https://github.com/Rikarin/Vixen/issues/328" /> has corrected itself twice about
///         whether one exists. ⚠ <b>A leg is the wrong shape for it and this is the right one.</b>
///         Every nought-worker fixture in this tree is an ordinary <c>[Fact]</c> or <c>[Theory]</c>
///         row, so `Test` already runs all of them on all three runners on every pull request — a
///         separate leg selecting a subset of what the suite just ran would buy no coverage and spend
///         runner time. What is not held anywhere is the other half: that a subsystem which *grows* a
///         dispatch also grows the row. That is a coverage question, and a coverage question is a
///         walk over the tree.
///     </para>
///     <para>
///         ⚠ <b>Two behaviours change at nought and both are silent.</b>
///         <c>Core/Vixen.Core.Threading/README.md</c>: work that is scheduled and never completed
///         never runs, and an automatic parallel-for batch is one batch rather than four per
///         participant. So a subsystem that schedules and polls is correct on every developer machine
///         and does nothing in the browser — which is
///         <see href="https://github.com/Rikarin/Vixen/issues/1214" />, found by asking exactly this
///         question of the one subsystem that had a fixture for it.
///     </para>
///     <para>
///         <b>The marker is a trait rather than a name or a scheduler literal.</b> The rows are spelled
///         four different ways in the tree — <c>new JobScheduler(0)</c>, an <c>[InlineData(0)]</c>
///         against a <c>workers</c> parameter, a nought in a <c>TheoryData</c> generator's worker
///         list, and a named tuple in the remesh sweep — and a regex over those is a false green
///         waiting to happen. <c>[Trait("Workers", "0")]</c> says the one thing that matters and says
///         it in one shape, and it is also what a <c>--filter-trait</c> would select if anybody ever
///         does want to run them alone.
///     </para>
///     <para>
///         ⚠ <b>It reads text and not behaviour.</b> A trait on a row that constructs a threaded
///         scheduler would satisfy this and prove nothing; the assertion is that the fixture exists
///         and is declared, not that it is honest. The honesty of each row is its own sabotage, which
///         the commit that added it records.
///     </para>
///     <para>
///         ⚠ <b>And what counts as a dispatch is read off the scheduler rather than remembered.</b>
///         This walked for two calls and looked their declarations up by those same two names, so it
///         agreed with itself — and <c>Schedule</c>, one job run once by somebody else, had been a
///         third the whole time. That is exactly the shape of the one real defect this question has
///         turned up: <see href="https://github.com/Rikarin/Vixen/issues/1214" />, scheduled, polled,
///         never waited on, and therefore never run at all where there are no workers. See
///         <see cref="DispatchNames" />.
///     </para>
/// </remarks>
public sealed class ZeroWorkerCoverageTests {
    /// <summary>Where production code lives. Benchmarks are deliberately not here — they measure.</summary>
    static readonly string[] Roots = ["Core", "Editor", "Gameplay", "Platform", "Raven", "Samples", "Tools"];

    /// <summary>
    ///     The calls that hand work to a <c>JobScheduler</c> for somebody else to run, <b>discovered
    ///     from the type</b> rather than listed here.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A dispatch is a public generic method whose job type is constrained to one of the job
    ///         interfaces. <c>Complete</c> is not one — it is the wait, and a wait is what makes
    ///         nought workers safe; nor is <c>IsCompleted</c>, which is a pure query and the reason
    ///         <see href="https://github.com/Rikarin/Vixen/issues/1214" /> exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This was a hard-coded pair, and the pair was already incomplete.</b>
    ///         <c>Schedule</c> — one job, run once, by somebody else — is a third dispatch and has
    ///         been public the whole time, and the instrument that was meant to notice could not:
    ///         it looked the declarations up <em>by the same two names</em>, so it confirmed a
    ///         tautology and its remarks' claim to catch a renamed "or added" call was true of half
    ///         of that. Reflection cannot be fooled that way — a call added tomorrow widens this set
    ///         with no edit here, and a call constrained to a job interface nobody has taught this
    ///         file about is a red rather than a silence.
    ///     </para>
    /// </remarks>
    static readonly string[] DispatchNames = DispatchesOf(typeof(JobScheduler));

    /// <summary>The call pattern, built from the names above.</summary>
    /// <remarks>
    ///     ⚠ <c>[(&lt;]</c> and not <c>(</c>: a call may name its job type — <c>Schedule&lt;Foo&gt;(…)</c>
    ///     — and the old pattern would have walked past it. None in the tree does today, which is
    ///     exactly why it would have gone unnoticed.
    /// </remarks>
    static readonly Regex Dispatch =
        new(@"\.(?:" + string.Join("|", DispatchNames) + @")\s*[(<]", RegexOptions.Compiled);

    /// <summary>The declaration a fixture makes about itself.</summary>
    static readonly Regex Marker = new(@"Trait\(""Workers"",\s*""0""\)", RegexOptions.Compiled);

    /// <summary>
    ///     The assembly that defines the two calls, which therefore names them everywhere and is not a
    ///     consumer of them.
    /// </summary>
    const string Scheduler = "Vixen.Core.Threading";

    /// <summary>
    ///     ⚠ The instrument, first and separately: every job interface the scheduler will accept work
    ///     through is one this file knows how to find a dispatch for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A walk that finds nothing agrees with every claim below it, so what the walk looks for
    ///         has to be derived from the scheduler rather than remembered. <see cref="DispatchNames" />
    ///         does the deriving; this is the one thing reflection cannot do on its own — decide that
    ///         a <em>new</em> job interface is a dispatch. A method constrained to one would be
    ///         invisible to the filter and the walk would go on reporting full coverage, which is the
    ///         exact failure the old version of this test had and could not see.
    ///     </para>
    ///     <para>
    ///         So the constraint set is enumerated and compared against the two this file understands.
    ///         Adding a third is a red with a sentence saying what to do about it, and that is the
    ///         only kind of instrument worth having here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryJobInterfaceTheSchedulerAcceptsIsOneThisWalkKnowsAbout() {
        var constraints = JobConstraintsOf(typeof(JobScheduler));

        Assert.True(
            constraints.Count > 0,
            $"No public generic method on {nameof(JobScheduler)} constrains anything to an interface, "
            + "so this file's idea of a dispatch matches nothing and the walk below reports that every "
            + "subsystem is covered. The scheduler did not stop scheduling; the reflection did."
        );

        var unknown = constraints
            .Where(type => type != typeof(IJob) && type != typeof(IJobParallelFor))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"{nameof(JobScheduler)} now takes work through {string.Join(", ", unknown)}, which this "
            + "file has never heard of — so a subsystem dispatching through it is invisible to the "
            + "coverage walk below and reads as covered. Add the interface to this test and to "
            + $"{nameof(DispatchesOf)}, then check whether its consumers have a nought-worker row."
        );

        // And the names actually reached the pattern: an empty set builds `\.(?:)\s*[(<]`, which
        // matches a bare `.` and would call the whole tree a dispatcher rather than none of it.
        Assert.NotEmpty(DispatchNames);
    }

    /// <summary>
    ///     Every production project that dispatches job work has a fixture declaring it runs at nought
    ///     workers.
    /// </summary>
    /// <remarks>
    ///     The fixture is looked for in the sibling test project, which is where this repository keeps
    ///     them (<c>Vixen.Ecs</c> / <c>Vixen.Ecs.Tests</c>). A subsystem whose nought-worker row
    ///     honestly belongs somewhere else moves the trait there and gains a line here saying so;
    ///     inventing an exemption file for a set this small would be worse than naming it.
    /// </remarks>
    [Fact]
    public void EveryDispatchingProjectHasANoughtWorkerFixture() {
        var root = RepositoryRoot();
        var dispatchers = Dispatchers(root);

        // The second half of the instrument, and the half that catches a broken pattern: the one
        // above proves the two names still exist, this proves the walk reaches the code that calls
        // them. A Roots list that stopped naming the directory the engine is in, or a regex that
        // stopped matching a call, passes the first and finds nothing here.
        Assert.True(
            dispatchers.Count > 0,
            "No production project outside the scheduler's own assembly dispatches job work, which "
            + "would mean the engine stopped scheduling anything. Far likelier: this walk no longer "
            + "reaches the tree it is asking about."
        );

        var uncovered = dispatchers
            .Where(project => !Covered(project.Key))
            .Select(project => $"{Path.GetFileName(project.Key)} ({string.Join(", ", project.Value)})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "These projects hand work to a JobScheduler and no fixture of theirs declares a run with "
            + $"none: {string.Join("; ", uncovered)}. A scheduler with no workers is what the browser "
            + "head builds by construction, and work that is scheduled and never waited on does "
            + "nothing there while passing everywhere else (#328). Add the nought row beside the "
            + "threaded one and mark it [Trait(\"Workers\", \"0\")]."
        );
    }

    /// <summary>
    ///     And the list only shrinks: a marker whose subject has stopped dispatching is a fixture
    ///     asserting about nothing.
    /// </summary>
    /// <remarks>
    ///     The scheduler's own tests are the exception, because they are the subject rather than a
    ///     consumer — <c>SingleThreadedJobSchedulerTests</c> is what proves nought workers works at
    ///     all.
    /// </remarks>
    [Fact]
    public void EveryNoughtWorkerMarkerBelongsToAProjectThatDispatches() {
        var root = RepositoryRoot();
        var dispatchers = Dispatchers(root)
            .Keys.Select(project => Path.GetFileName(project) + ".Tests")
            .ToHashSet(StringComparer.Ordinal);

        var stale = Roots
            .Select(name => Path.Combine(root, name))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file => !Ignored(file) && !Own(file) && Marker.IsMatch(File.ReadAllText(file)))
            .Select(ProjectOf)
            .Where(project => project is not null)
            .Select(project => Path.GetFileName(project)!)
            .Distinct(StringComparer.Ordinal)
            .Where(project => project != Scheduler + ".Tests" && !dispatchers.Contains(project))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"These test projects declare a nought-worker fixture and their subject dispatches no job "
            + $"work: {string.Join(", ", stale)}. Either the dispatch moved and the fixture is now "
            + "measuring a serial path, or the trait outlived what it covered."
        );
    }

    /// <summary>Which production projects dispatch, and from which files.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Project directory to the file names that dispatch, ordered.</returns>
    /// <remarks>
    ///     ⚠ <b>A project qualifies only if it names <c>JobScheduler</c> somewhere</b>, and that guard
    ///     is what makes <c>Schedule</c> safe to walk for. <c>ScheduleParallel</c> and
    ///     <c>ParallelFor</c> are distinctive; <c>Schedule</c> is the commonest verb in the language,
    ///     and <c>Core/Vixen.Audio/AudioEngine.cs:671</c> calls it on a deferred-play queue that has
    ///     nothing to do with jobs. Asked of the <em>project</em> rather than the file because a
    ///     method may dispatch through a scheduler it received as a parameter, and the type is then
    ///     named in the signature next door. Every file in this tree that calls the two distinctive
    ///     names is in a project that names the type, so the guard costs no coverage today.
    /// </remarks>
    static Dictionary<string, List<string>> Dispatchers(string root) {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var namesScheduler = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in Roots) {
            var directory = Path.Combine(root, name);

            if (!Directory.Exists(directory)) {
                continue;
            }

            foreach (var file in Sources(directory)) {
                var project = ProjectOf(file);

                // ⚠ A fixture is not a dispatcher. Every nought-worker row below dispatches work
                // itself, so counting test projects here would ask each of them for a `.Tests.Tests`
                // sibling and fail on the very fixtures this is checking for.
                if (project is null
                    || Path.GetFileName(project) == Scheduler
                    || Path.GetFileName(project).EndsWith(".Tests", StringComparison.Ordinal)) {
                    continue;
                }

                var text = File.ReadAllText(file);

                if (text.Contains(nameof(JobScheduler), StringComparison.Ordinal)) {
                    namesScheduler.Add(project);
                }

                if (!Dispatch.IsMatch(text)) {
                    continue;
                }

                if (!found.TryGetValue(project, out var files)) {
                    found[project] = files = [];
                }

                files.Add(Path.GetFileName(file));
            }
        }

        foreach (var project in found.Keys.Where(project => !namesScheduler.Contains(project)).ToList()) {
            found.Remove(project);
        }

        return found;
    }

    /// <summary>
    ///     The names of the scheduler's dispatch calls, read off the type: a public generic method
    ///     whose job type is constrained to one of the job interfaces.
    /// </summary>
    /// <param name="scheduler">The scheduler type.</param>
    /// <returns>The distinct names, ordered.</returns>
    static string[] DispatchesOf(Type scheduler) =>
        scheduler.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.IsGenericMethodDefinition)
            .Where(method => method.GetGenericArguments()
                .SelectMany(argument => argument.GetGenericParameterConstraints())
                .Any(constraint => constraint == typeof(IJob) || constraint == typeof(IJobParallelFor))
            )
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every interface a public generic method on the scheduler constrains a type to.</summary>
    /// <param name="scheduler">The scheduler type.</param>
    /// <returns>The distinct interfaces.</returns>
    static IReadOnlyCollection<Type> JobConstraintsOf(Type scheduler) =>
        scheduler.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.IsGenericMethodDefinition)
            .SelectMany(method => method.GetGenericArguments())
            .SelectMany(argument => argument.GetGenericParameterConstraints())
            .Where(constraint => constraint.IsInterface)
            .Distinct()
            .ToList();

    /// <summary>Whether the sibling test project declares a nought-worker fixture.</summary>
    /// <param name="project">The production project's directory.</param>
    /// <returns>Whether one was found.</returns>
    static bool Covered(string project) {
        var tests = project + ".Tests";

        return Directory.Exists(tests)
            && Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
                .Any(file => !Ignored(file) && !Own(file) && Marker.IsMatch(File.ReadAllText(file)));
    }

    /// <summary>This file, which quotes the marker in prose and would otherwise declare one.</summary>
    /// <param name="file">The path to judge.</param>
    /// <returns>Whether it is this test's own source.</returns>
    /// <remarks>
    ///     ⚠ A walk for a literal is a walk that finds itself. Named rather than dodged by spelling
    ///     the pattern differently in the remarks above: prose that cannot say what it is looking for
    ///     is worse documentation, and the next person to quote the trait in a comment here would
    ///     re-break it.
    /// </remarks>
    static bool Own(string file) =>
        Path.GetFileName(file).Equals(nameof(ZeroWorkerCoverageTests) + ".cs", StringComparison.Ordinal);

    /// <summary>
    ///     ⚠ Both extensions, because a view's <c>&lt;code&gt;</c> block is production C# and a sweep
    ///     that reads only <c>.cs</c> reports a gap that is not there.
    /// </summary>
    /// <param name="directory">Where to look.</param>
    /// <returns>Every committed-shaped source file under it.</returns>
    static IEnumerable<string> Sources(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal)
                || file.EndsWith(".vxml", StringComparison.Ordinal)
            )
            .Where(file => !Ignored(file));

    /// <summary>Build output, which is not this tree's source.</summary>
    /// <param name="file">The path to judge.</param>
    /// <returns>Whether to skip it.</returns>
    /// <remarks>
    ///     ⚠ <b>No <c>.claude</c> clause, deliberately.</b> Agent worktrees are whole checkouts under
    ///     <c>.claude/worktrees</c> and a walk from the repository root would read them — but this
    ///     walk starts at the named source directories instead, and a path under <c>.claude</c> is
    ///     never under one of those. Excluding it by substring is what breaks the moment the test is
    ///     run <em>inside</em> an agent worktree, where every path contains it: the walk then finds
    ///     nothing and every assertion below passes.
    /// </remarks>
    static bool Ignored(string file) {
        var path = file.Replace('\\', '/');

        return path.Contains("/obj/", StringComparison.Ordinal) || path.Contains("/bin/", StringComparison.Ordinal);
    }

    /// <summary>The nearest directory above a file that owns a project.</summary>
    /// <param name="file">The file.</param>
    /// <returns>Its project's directory, or null if it is in none.</returns>
    static string? ProjectOf(string file) {
        var directory = Path.GetDirectoryName(file);

        while (directory is not null) {
            if (Directory.EnumerateFiles(directory, "*.csproj").Any()) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    ///     ⚠ Walked up from the assembly rather than from <c>[CallerFilePath]</c>, which CI rewrites
    ///     to <c>/_/</c> — see <see cref="SourceAnchoredTestTests" />.
    /// </summary>
    /// <returns>The repository root.</returns>
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
