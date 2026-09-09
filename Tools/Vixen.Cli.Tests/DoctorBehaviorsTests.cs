// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Engine.Behaviors;
using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>`vixen doctor behaviors`, run against this assembly and scenes written for it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The registrations these read were emitted by <c>BehaviorRegistrationGenerator</c>,
///         not written here</b> — the same arrangement <c>DoctorSystemsTests</c> uses and for the
///         same reason: a fixture that called <c>SceneBehaviorRegistry.Register</c> by hand would
///         prove the registry works and say nothing about whether a real project's behaviours reach
///         the command.
///     </para>
///     <para>
///         <b>Two of the fixtures below are deliberately different in kind</b>, because the two
///         answers this command gives are "a scene may name it" and "nothing here can count it":
///         <see cref="DoctorPatrol" /> carries <c>[DataContract]</c> and
///         <see cref="DoctorCodeOnly" /> does not.
///     </para>
/// </remarks>
public sealed class DoctorBehaviorsTests : IDisposable {
    readonly StringWriter output = new();
    readonly List<string> scratch = [];

    /// <summary>This assembly, which is the built game assembly under examination.</summary>
    static string ThisAssembly => Assembly.GetExecutingAssembly().Location;

    public void Dispose() {
        output.Dispose();

        foreach (var path in scratch) {
            File.Delete(path);
        }
    }

    async Task<int> RunAsync(params string[] args) =>
        await VixenCommand.Create(output, output).Parse(args).InvokeAsync();

    string Scene(string yaml) {
        var path = Path.Combine(Path.GetTempPath(), "doctor-behaviors-" + Guid.NewGuid().ToString("N") + ".vxscene");

        File.WriteAllText(path, yaml);
        scratch.Add(path);

        return path;
    }

    [Fact]
    public async Task It_names_every_behaviour_type_and_whether_a_scene_can_reach_it() {
        await RunAsync("doctor", "behaviors", "--assembly", ThisAssembly);
        var text = output.ToString();

        Assert.Contains("a scene may name it as !DoctorPatrol", text, StringComparison.Ordinal);
        Assert.Contains("carries no [DataContract]", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The count itself, which is the number doc 04's authoring rule is written about and which
    ///     no public surface could produce before <c>BehaviorStore.Population</c> existed.
    /// </summary>
    [Fact]
    public async Task It_counts_the_instances_a_scene_authors_per_type() {
        var scene = Scene(
            """
            version: 1
            name: Counted
            roots:
              - name: A
                components:
                  - !DoctorPatrol { }
              - name: B
                components:
                  - !DoctorPatrol { }
                children:
                  - name: C
                    components:
                      - !DoctorPatrol { }
                      - !DoctorSpinner { }
            """
        );

        await RunAsync("doctor", "behaviors", "--assembly", ThisAssembly, "--scene", scene);
        var text = output.ToString();

        // Three of one and one of the other, and the child's two are counted — a walk that stopped
        // at the roots would say two and one.
        Assert.Contains("DoctorPatrol: 3 authored", text, StringComparison.Ordinal);
        Assert.Contains("DoctorSpinner: 1 authored", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ The instrument check, and the finding that matters most in this repository: not one of
    ///     the fourteen committed <c>.vxscene</c> files names a behaviour, because every behaviour
    ///     instance in the samples is attached from code. A command that printed a clean report over
    ///     those would be saying "no behaviour is over the threshold" when what happened is that it
    ///     counted nothing.
    /// </summary>
    [Fact]
    public async Task A_scene_that_authors_no_behaviour_says_so_rather_than_reporting_nothing() {
        var scene = Scene(
            """
            version: 1
            name: Empty
            roots:
              - name: A
            """
        );

        var code = await RunAsync("doctor", "behaviors", "--assembly", ThisAssembly, "--scene", scene);

        Assert.Contains("names no behaviour at all", output.ToString(), StringComparison.Ordinal);

        // ⚠ Concerning and not Broken, which is `Report`'s exit-code rule and the right one here: a
        // level that authors no behaviour is not a wrong project, it is a project this command
        // cannot see the number for.
        Assert.Equal((int)ExitCode.Success, code);
    }

    /// <summary>And the same refusal one level up: no scene at all counted nothing either.</summary>
    [Fact]
    public async Task Naming_no_scene_says_the_count_was_not_taken() {
        var code = await RunAsync("doctor", "behaviors", "--assembly", ThisAssembly);

        Assert.Contains("no --scene was named", output.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.Success, code);
    }

    [Fact]
    public async Task A_scene_that_is_not_there_is_broken_rather_than_skipped() {
        var absent = Path.Combine(Path.GetTempPath(), "no-such-scene-" + Guid.NewGuid().ToString("N") + ".vxscene");
        var code = await RunAsync("doctor", "behaviors", "--assembly", ThisAssembly, "--scene", absent);

        Assert.Contains("there is nothing at", output.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.Failed, code);
    }

    [Fact]
    public async Task Naming_no_assembly_at_all_is_a_usage_error() {
        var code = await RunAsync("doctor", "behaviors");

        Assert.Contains("Name at least one built game assembly", output.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.UsageError, code);
    }

    /// <summary>
    ///     A scene naming something no build claims is a real answer to "what is wrong with my
    ///     project", so it is reported rather than swallowed.
    /// </summary>
    [Fact]
    public async Task A_scene_naming_an_unknown_type_is_reported() {
        var scene = Scene(
            """
            version: 1
            name: Stale
            roots:
              - name: A
                components:
                  - !NoSuchBehaviorAnywhere { }
            """
        );

        var code = await RunAsync("doctor", "behaviors", "--assembly", ThisAssembly, "--scene", scene);

        Assert.Contains("could not be read", output.ToString(), StringComparison.Ordinal);
        Assert.Equal((int)ExitCode.Failed, code);
    }
}

/// <summary>A behaviour a scene may name, because it carries the attribute that makes it nameable.</summary>
[DataContract("DoctorPatrol")]
public sealed class DoctorPatrol : Behavior {
    /// <summary>Something for the file to carry, so the fixture is not an empty node.</summary>
    public float Speed { get; set; } = 1;
}

/// <summary>A second one, so that the report has to keep two types apart rather than sum them.</summary>
[DataContract("DoctorSpinner")]
public sealed class DoctorSpinner : Behavior {
    /// <summary>Turns per second.</summary>
    public float Rate { get; set; } = 1;
}

/// <summary>
///     ⚠ No <c>[DataContract]</c>, so nothing registers it and no scene can name it. Attaching it
///     from <c>Game.OnInitialise</c> is supported and invisible, which is what the report says about
///     it rather than complaining.
/// </summary>
public sealed class DoctorCodeOnly : Behavior;
