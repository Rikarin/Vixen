// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The capability guard's own rules, asserted where a driver is not needed to assert them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test in this file runs on a machine with no GPU at all, and that is the
///         design.</b> #143's second leg is a rule about when a device test may stand aside; a rule
///         that could only be checked on a device would skip on exactly the runners whose skips it
///         governs. So <c>Capability.Decide</c> is pure and this asserts it directly, while
///         <c>Capability.Require</c> is a thin actor over it.
///     </para>
///     <para>
///         The census at the bottom is the other half. A decision function nothing calls is the
///         commonest defect in this repository, and eleven call sites that each kept their own
///         <c>Assert.Skip</c> is what leg 2 was filed about.
///     </para>
/// </remarks>
public sealed class CapabilityGuardTests {
    /// <summary>An undescribed device is a failure, not a skip — whatever it claims to lack.</summary>
    /// <remarks>
    ///     The whole of the leg. <c>GraphicsDeviceFeatures.Minimum</c> reports every capability
    ///     absent and <c>VulkanFeatures.Describe</c> sets <c>HasCompute</c> unconditionally, so a
    ///     Vulkan device reporting no compute is one whose description never ran — and its "no" to
    ///     bindless is a default rather than a measurement.
    /// </remarks>
    [Fact]
    public void ADeviceWhoseDescriptionNeverRanFailsRatherThanSkipping() {
        Assert.Equal(
            CapabilityVerdict.DescriptionMissing,
            Capability.Decide(described: false, available: false, Capability.Bindless, null, named: false)
        );
    }

    /// <summary>And it fails even when the promise says nothing, because nothing asked.</summary>
    /// <remarks>
    ///     ⚠ The case the environment cannot cover. A leg that names no capability at all is the
    ///     default configuration, and it is the configuration under which a silently unasked device
    ///     would otherwise skip eleven tests and report green.
    /// </remarks>
    [Fact]
    public void AnUndescribedDeviceFailsUnderNoPromiseAtAll() {
        foreach (var capability in Capability.Known) {
            Assert.Equal(
                CapabilityVerdict.DescriptionMissing,
                Capability.Decide(described: false, available: false, capability, promised: "", named: false)
            );
        }

        // ⚠ The loop asserts inside itself, so the count is part of what it must assert.
        Assert.Equal(5, Capability.Known.Count);
    }

    /// <summary>A described device that genuinely lacks the capability stands aside.</summary>
    /// <remarks>
    ///     The half that must stay true. Several of these skips are correct on MoltenVK and on
    ///     lavapipe, and a guard that turned an honest no into a red leg would be worse than the
    ///     ambiguity it replaced.
    /// </remarks>
    [Fact]
    public void ADescribedDeviceThatLacksItSkips() {
        Assert.Equal(
            CapabilityVerdict.Skip,
            Capability.Decide(described: true, available: false, Capability.RayQuery, null, named: false)
        );
    }

    /// <summary>A described device that has it runs.</summary>
    /// <remarks>
    ///     ⚠ Without this the file is satisfied by a <c>Decide</c> that never returns
    ///     <c>Available</c> — every gated test would skip on every machine and every assertion here
    ///     would still hold.
    /// </remarks>
    [Fact]
    public void ADescribedDeviceThatHasItRuns() {
        foreach (var capability in Capability.Known) {
            Assert.Equal(
                CapabilityVerdict.Available,
                Capability.Decide(described: true, available: true, capability, null, named: false)
            );
        }

        Assert.Equal(5, Capability.Known.Count);
    }

    /// <summary>A capability the run promised and the device declines is a failure.</summary>
    /// <remarks>
    ///     Three spellings of the same promise: the capability's own variable, its name in the list,
    ///     and <c>all</c>. The list is what a leg with several capabilities writes; the per-capability
    ///     variable is what <c>VIXEN_REQUIRE_RAY_QUERY</c> already was before this existed.
    /// </remarks>
    [Theory]
    [InlineData(null, true)]
    [InlineData("ray-query", false)]
    [InlineData("bindless,ray-query", false)]
    [InlineData("bindless ray-query", false)]
    [InlineData("all", false)]
    [InlineData("RAY-QUERY", false)]
    public void APromisedCapabilityTheDeviceDeclinesFails(string? promised, bool named) {
        Assert.Equal(
            CapabilityVerdict.Promised,
            Capability.Decide(described: true, available: false, Capability.RayQuery, promised, named)
        );
    }

    /// <summary>A promise naming something nobody recognises fails rather than doing nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument's own instrument.</b> A leg configured with <c>bindles</c> would
    ///     otherwise assert exactly nothing while looking configured, and would go on looking
    ///     configured for as long as the device happened to support everything. Loud, on the first
    ///     capability-gated test that runs.
    /// </remarks>
    [Theory]
    [InlineData("bindles")]
    [InlineData("bindless,rayquery")]
    [InlineData("everything")]
    public void APromiseNamingSomethingUnknownFails(string promised) {
        Assert.Equal(
            CapabilityVerdict.Unknown,
            Capability.Decide(described: true, available: false, Capability.Bindless, promised, named: false)
        );
    }

    /// <summary>And so does a call site that invented a capability name.</summary>
    [Fact]
    public void ACallSiteNamingSomethingUnknownFails() {
        Assert.Equal(
            CapabilityVerdict.Unknown,
            Capability.Decide(described: true, available: true, "wave-intrinsics", null, named: false)
        );
    }

    /// <summary>The per-capability variable is derived, so a new capability cannot arrive without one.</summary>
    /// <remarks>
    ///     ⚠ The ray-query case is not an example, it is the compatibility assertion:
    ///     <c>VIXEN_REQUIRE_RAY_QUERY</c> was written by hand at the one site that had thought about
    ///     this, and anything already setting it must keep working.
    /// </remarks>
    [Theory]
    [InlineData("ray-query", "VIXEN_REQUIRE_RAY_QUERY")]
    [InlineData("bindless", "VIXEN_REQUIRE_BINDLESS")]
    [InlineData("int64-atomics", "VIXEN_REQUIRE_INT64_ATOMICS")]
    [InlineData("msaa", "VIXEN_REQUIRE_MSAA")]
    [InlineData("depth-resolve-min-max", "VIXEN_REQUIRE_DEPTH_RESOLVE_MIN_MAX")]
    public void EachCapabilityHasItsOwnVariable(string capability, string variable) =>
        Assert.Equal(variable, Capability.Variable(capability));

    /// <summary>Every capability constant is in the known set, and the set holds nothing else.</summary>
    /// <remarks>
    ///     ⚠ Reflection rather than a written list. A sixth constant added above and forgotten here
    ///     would be a capability whose every promise was rejected as unknown — a call site that
    ///     failed for the wrong reason, which is only marginally better than one that failed for
    ///     none.
    /// </remarks>
    [Fact]
    public void TheKnownSetIsExactlyTheConstants() {
        var reserved = new HashSet<string>(StringComparer.Ordinal) { nameof(Capability.Promise) };

        var constants = typeof(Capability)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field is { IsLiteral: true, FieldType.FullName: "System.String" })
            .Where(field => !reserved.Contains(field.Name))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value != Capability.Everything)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Capability.Known.OrderBy(name => name, StringComparer.Ordinal), constants.OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>Every capability gate in this assembly goes through the guard.</summary>
    /// <remarks>
    ///     <para>
    ///         The census, and the reason it exists: a decision function nothing calls proves
    ///         nothing at all, and the eleven sites leg 2 names each carried their own
    ///         <c>Assert.Skip</c> before this. Sources rather than reflection, because what is being
    ///         checked is a call and not a type.
    ///     </para>
    ///     <para>
    ///         ⚠ It also refuses a call site that passes a string literal. The whole value of a
    ///         closed set is lost the moment a name can be spelled at a call site, and the compiler
    ///         will not notice.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryCapabilityGateNamesAConstant() {
        var sources = Directory.GetFiles(ProjectDirectory(), "*.cs", SearchOption.TopDirectoryOnly);

        Assert.True(sources.Length >= 50, $"{sources.Length} sources under '{ProjectDirectory()}' is not this project.");

        var calls = new Regex(@"Capability\.Require\(\s*(?<device>[^,]+),\s*(?<capability>[^,\r\n]+),", RegexOptions.Singleline);
        var seen = 0;

        foreach (var path in sources) {
            if (string.Equals(Path.GetFileName(path), "Capability.cs", StringComparison.Ordinal)) {
                continue;
            }

            foreach (Match match in calls.Matches(File.ReadAllText(path))) {
                var capability = match.Groups["capability"].Value.Trim();
                seen++;

                Assert.True(
                    capability.StartsWith("Capability.", StringComparison.Ordinal),
                    $"{Path.GetFileName(path)} gates on '{capability}' rather than one of the constants. "
                    + "A name spelled at a call site is outside the closed set, and nothing would "
                    + "notice it drift."
                );
            }
        }

        // ⚠ The instrument, first: a census that matched nothing agrees with every assertion above.
        //
        // ⚠ Nine, not eleven. #143 says "the capability skips are eleven, in seven files" and then
        // lists ten sites in six files — the count is off by one and always was. Of those ten, nine
        // reach Require; the tenth is DepthResolveImageTests' second gate, which stands aside when
        // the device HAS both modes and so takes Capability.Described instead: a promise about a
        // capability being present is meaningless to a test that wants it absent.
        Assert.True(
            seen >= 9,
            $"only {seen} capability gates were found and there are nine. Either the gate has been "
            + "renamed and this test now checks nothing, or sites have gone back to skipping alone."
        );
    }

    /// <summary>This project's own directory, found by walking up rather than by counting directories.</summary>
    /// <remarks>
    ///     ⚠ Never a walk from the repository root, and never <c>[CallerFilePath]</c>. CI turns on
    ///     <c>DeterministicSourcePaths</c>, which rewrites a compiled path to <c>/_/…</c>; and
    ///     <c>.claude/worktrees</c> holds a full checkout per parallel agent, so a search for these
    ///     file names from above reports on a tree nobody is editing.
    /// </remarks>
    static string ProjectDirectory() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Platform", "Vixen.Graphics.Golden.Tests");

            if (File.Exists(Path.Combine(candidate, "Capability.cs"))) {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"this project's sources were not found above '{AppContext.BaseDirectory}'.");
    }
}
