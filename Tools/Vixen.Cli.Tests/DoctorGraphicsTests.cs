// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Cli.Tests;

/// <summary>
///     The one thing <c>vixen doctor</c> says about the <i>machine</i> rather than about the project —
///     <a href="https://github.com/Rikarin/Vixen/issues/1094">#1094</a>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The two outcomes are asked of the formatter and not of the probe, because only one of
///         them is reachable on any given machine.</b> A test that opened a device here would assert
///         whichever branch this box happens to take and would be blind to whether the other one says
///         anything different — and "a line that reports success on the day it stops running" is the
///         failure the issue names in as many words. Both are asked, and they are asked to differ.
///     </para>
///     <para>
///         <b>The exit code is the decision the issue was actually about</b>, and it is asserted
///         rather than described: <c>Report</c> fails the command on <see cref="Health.Broken" />
///         alone, so a machine with no device has to be reported below that or every container image
///         reads as a broken project.
///     </para>
/// </remarks>
public sealed class DoctorGraphicsTests {
    [Fact]
    public void An_adapter_and_a_refusal_are_two_different_sentences_at_two_different_healths() {
        var opened = DoctorRunner.Machine("Apple M4 Pro", null);
        var refused = DoctorRunner.Machine(null, "libvulkan was not on the dynamic linker's search path.");

        Assert.Equal(Health.Fine, opened.Health);
        Assert.Contains("Apple M4 Pro", opened.Detail, StringComparison.Ordinal);

        // ⚠ The driver's own words, kept and led with. `HeadlessGraphics.Refusal`'s remarks record
        // what happened when this file's ancestor guessed instead: "there is no GPU here" followed by
        // the loader saying the package was missing, two sentences that contradicted each other.
        Assert.Equal(Health.Concerning, refused.Health);
        Assert.StartsWith(
            "libvulkan was not on the dynamic linker's search path.",
            refused.Detail,
            StringComparison.Ordinal
        );

        // The pair, which is the claim: same subject, different health, different words.
        Assert.Equal(opened.Subject, refused.Subject);
        Assert.NotEqual(opened.Detail, refused.Detail);
    }

    /// <summary>⚠ A machine with no device is not a broken project, so the command still succeeds.</summary>
    /// <remarks>
    ///     <b>Read off <c>Report</c> rather than asserted about <c>Health</c></b>, because the
    ///     contract being protected is the exit code and the mapping from health to exit code lives
    ///     there. A later change that made <c>Concerning</c> fail the command would be invisible to a
    ///     test that only compared enum values.
    /// </remarks>
    [Fact]
    public void A_machine_with_no_device_does_not_fail_the_command() {
        using StringWriter writer = new();

        var usable = DoctorRunner.Report([DoctorRunner.Machine(null, "no Vulkan-capable device.")], writer);

        Assert.True(usable, writer.ToString());
        Assert.Contains("GPU", writer.ToString(), StringComparison.Ordinal);

        // And the instrument: `Report` does fail on something, so the `true` above is a decision about
        // this finding rather than a function that always says yes.
        using StringWriter broken = new();

        Assert.False(DoctorRunner.Report([new Finding(Health.Broken, "Assets/", "there is no directory.")], broken));
    }

    /// <summary>The probe runs for real and says one of exactly those two things about this machine.</summary>
    /// <remarks>
    ///     <para>
    ///     ⚠ <b>Branching on the machine, and each branch asserts something only that branch can
    ///     produce</b> — deliberately not an assertion about which answer is right, because that is a
    ///     fact about the box the suite is running on.
    ///     </para>
    ///     <para>
    ///     ⚠ <b>And it is not the case that catches the probe never being called</b>, whatever an
    ///     earlier version of this remark claimed: it wires <c>DoctorRunner.Machine</c> up itself
    ///     rather than going through <c>Examine</c>, so deleting <c>findings.Add(Machine())</c> from
    ///     production leaves it green. What asserts the wiring is
    ///     <c>VixenCommandTests</c>'s <c>Assert.Contains("GPU:", output)</c> over a real
    ///     <c>doctor</c> run — and that file's own comment said so about this one, in the same
    ///     commit.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_doctor_asks_this_machine_and_prints_whichever_answer_it_gets() {
        using StringWriter writer = new();

        var findings = new List<Finding>();
        var probed = HeadlessGraphics.TryOpen(out var device, out _, out var driver);

        try {
            findings.Add(DoctorRunner.Machine(probed ? HeadlessGraphics.Adapter(device!) : null, driver));
        } finally {
            device?.Dispose();
        }

        Assert.True(DoctorRunner.Report(findings, writer));

        var printed = writer.ToString();

        Assert.Contains("GPU", printed, StringComparison.Ordinal);

        if (probed) {
            Assert.Contains("through Vulkan", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing in a project can fix that", printed, StringComparison.Ordinal);
        } else {
            Assert.Contains("Nothing in a project can fix that", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("through Vulkan", printed, StringComparison.Ordinal);
        }
    }
}
