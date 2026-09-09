// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>What a capability gate decided, and why.</summary>
/// <remarks>
///     Separated from the acting on it so that the decision can be tested on a machine with no
///     driver at all. A rule about when a device test may stand aside that could itself only be
///     checked on a device would be the same joke one level up.
/// </remarks>
enum CapabilityVerdict {
    /// <summary>The device has it. Run the test.</summary>
    Available,

    /// <summary>The device genuinely does not have it, and nothing promised otherwise. Stand aside.</summary>
    Skip,

    /// <summary>The feature description never ran, so "absent" is not an answer about this device.</summary>
    DescriptionMissing,

    /// <summary>The leg named this capability as one its device has, and the device says no.</summary>
    Promised,

    /// <summary>A capability name nobody recognises — at a call site, or in the promise.</summary>
    Unknown
}

/// <summary>The one place a device test is allowed to stand aside for a missing capability.</summary>
/// <remarks>
///     <para>
///         <c>VIXEN_REQUIRE_VULKAN</c> turns a missing <b>device</b> into a failure and says nothing
///         about a missing <b>feature</b> (#143, leg 2). Eleven tests across six files skip on a
///         capability, and every one of those skips was correct on this machine and
///         indistinguishable from the failure mode underneath it: a capability query that returned
///         <c>false</c> because nothing had asked.
///     </para>
///     <para>
///         ⚠ <b>The canary is <c>HasCompute</c>, and it costs no driver knowledge whatsoever.</b>
///         <c>GraphicsDeviceFeatures.Minimum</c> reports every capability absent, and
///         <c>VulkanFeatures.Describe</c> sets <c>HasCompute = true</c> unconditionally — Vulkan has
///         no device without compute. So a Vulkan device whose features say
///         <c>HasCompute == false</c> is a device whose description never ran, and every capability
///         answer taken from it is a default rather than a measurement. That is the difference
///         between "this device cannot" and "nobody asked", and it is decidable without knowing
///         anything about the driver.
///     </para>
///     <para>
///         The second half does need driver knowledge, so it lives in the environment where the
///         driver is chosen rather than in the tests. Two spellings, one rule: a per-capability
///         <c>VIXEN_REQUIRE_&lt;NAME&gt;</c> — which is what the existing
///         <c>VIXEN_REQUIRE_RAY_QUERY</c> becomes, a documented instance rather than a one-off — and
///         <see cref="Promise" />, which names several at once. A capability promised and declined
///         is a failure rather than a skip.
///     </para>
///     <para>
///         ⚠ <b>Neither is set in <c>ci.yml</c>, and that is the honest state rather than an
///         omission.</b> Nobody has yet watched a lavapipe run and recorded which of these five it
///         actually offers, and a promise written from guesswork turns honest skips red for reasons
///         unrelated to the change that tripped them. Watch one run, read the skip reasons — they
///         name the adapter and the variable — then name the capabilities that ran.
///     </para>
///     <para>
///         ⚠ <b>A misspelled entry in <see cref="Promise" /> is a failure, not a no-op.</b> A
///         variable naming <c>bindles</c> would otherwise leave the leg asserting exactly nothing
///         while looking configured, which is this repository's oldest failure class wearing a new
///         hat. The per-capability spelling cannot be checked that way — an unset variable and a
///         misspelt one are the same observation — which is why the list exists at all.
///     </para>
/// </remarks>
static class Capability {
    /// <summary>Descriptor indexing, as the engine's bindless table needs it (ADR-011).</summary>
    public const string Bindless = "bindless";

    /// <summary>64-bit buffer atomics, which the software rasteriser is gated on.</summary>
    public const string Int64Atomics = "int64-atomics";

    /// <summary>Ray query, for the acceleration-structure kernel.</summary>
    public const string RayQuery = "ray-query";

    /// <summary>Multisample rasterisation above one sample.</summary>
    public const string Msaa = "msaa";

    /// <summary>The <c>Min</c> and <c>Max</c> depth resolve modes, both optional in core Vulkan.</summary>
    public const string DepthResolveMinMax = "depth-resolve-min-max";

    /// <summary>The environment variable that names several capabilities at once.</summary>
    public const string Promise = "VIXEN_REQUIRE_CAPABILITIES";

    /// <summary>The promise token that names every capability at once.</summary>
    public const string Everything = "all";

    /// <summary>Every capability name this assembly gates on.</summary>
    /// <remarks>
    ///     A closed set on purpose. It is what lets a promise naming something else be a failure
    ///     rather than silence, and <c>CapabilityGuardTests</c> holds every call site inside it.
    /// </remarks>
    public static readonly IReadOnlySet<string> Known =
        new HashSet<string>(StringComparer.Ordinal) { Bindless, Int64Atomics, RayQuery, Msaa, DepthResolveMinMax };

    /// <summary>What the separators between capability names in the promise are.</summary>
    static readonly char[] Separators = [',', ';', ' ', '\t'];

    /// <summary>The per-capability environment variable that promises one capability.</summary>
    /// <param name="capability">The capability name.</param>
    /// <returns>The variable's name, in this repository's <c>VIXEN_REQUIRE_*</c> family.</returns>
    /// <remarks>
    ///     Derived rather than listed, so a capability added above cannot arrive without one. ⚠ It
    ///     reproduces <c>VIXEN_REQUIRE_RAY_QUERY</c> exactly, which is the point: that variable was
    ///     written by hand at the one site that had thought about this, and is now the rule.
    /// </remarks>
    public static string Variable(string capability) {
        ArgumentNullException.ThrowIfNull(capability);

        return "VIXEN_REQUIRE_" + capability.Replace('-', '_').ToUpperInvariant();
    }

    /// <summary>Decides what a capability gate should do, given everything it can observe.</summary>
    /// <param name="described">
    ///     Whether the device's feature description demonstrably ran — <c>HasCompute</c> on a Vulkan
    ///     device. See the remarks on this class for why that is the canary.
    /// </param>
    /// <param name="available">Whether the device reports the capability.</param>
    /// <param name="capability">Which capability, spelled as one of the constants here.</param>
    /// <param name="promised">The raw value of <see cref="Promise" />, or <see langword="null" />.</param>
    /// <param name="named">Whether this capability's own <see cref="Variable" /> is set.</param>
    /// <returns>What to do.</returns>
    /// <remarks>
    ///     Pure, and that is the point: this is the half that can be asserted on every runner rather
    ///     than only on the one with a GPU.
    /// </remarks>
    public static CapabilityVerdict Decide(
        bool described,
        bool available,
        string capability,
        string? promised,
        bool named
    ) {
        var wanted = Parse(promised);

        // The configuration error first, and unconditionally. A promise nobody can honour is worth
        // saying whatever the device turns out to report, because the alternative is a leg that
        // looks configured and checks nothing.
        if (!Known.Contains(capability) || wanted.Any(name => name != Everything && !Known.Contains(name))) {
            return CapabilityVerdict.Unknown;
        }

        // Then the instrument. An undescribed device answers "no" to everything, so believing its
        // "no" here is believing a default.
        if (!described) {
            return CapabilityVerdict.DescriptionMissing;
        }

        if (available) {
            return CapabilityVerdict.Available;
        }

        return named || wanted.Contains(Everything) || wanted.Contains(capability)
            ? CapabilityVerdict.Promised
            : CapabilityVerdict.Skip;
    }

    /// <summary>Runs on, skips, or fails — the gate every capability-gated test calls.</summary>
    /// <param name="device">The open device, for its features and for naming the adapter.</param>
    /// <param name="capability">Which capability, spelled as one of the constants here.</param>
    /// <param name="available">Whether the device reports it.</param>
    /// <param name="gated">What is gated on it, for the message — "phase 6's software raster".</param>
    /// <remarks>
    ///     ⚠ It skips rather than returning. A bare <c>return</c> is recorded by xUnit as a pass, so
    ///     a capability gate that returned would read as a device test that had run and been
    ///     satisfied on every runner whose device says no. That is the eighteen-passing-goldens
    ///     failure, and two files here had already been corrected for it once.
    /// </remarks>
    public static void Require(VulkanDevice device, string capability, bool available, string gated) {
        ArgumentNullException.ThrowIfNull(device);

        var promised = Environment.GetEnvironmentVariable(Promise);
        var variable = Known.Contains(capability) ? Variable(capability) : null;
        var named = variable is not null && Truthy(Environment.GetEnvironmentVariable(variable));
        var adapter = device.Adapter.Name;

        switch (Decide(device.Features.HasCompute, available, capability, promised, named)) {
            case CapabilityVerdict.Available:
                return;

            case CapabilityVerdict.Unknown:
                Assert.Fail(
                    $"'{capability}' or something in {Promise}='{promised}' is not a capability this "
                    + $"assembly knows about. Known: {string.Join(", ", Known.Order(StringComparer.Ordinal))} "
                    + $"(or '{Everything}'). A promise nobody can honour asserts nothing."
                );

                return;

            case CapabilityVerdict.DescriptionMissing:
                Assert.Fail(DescriptionMissing(adapter, capability));

                return;

            case CapabilityVerdict.Promised:
                Assert.Fail(
                    $"'{adapter}' declines '{capability}' and this run promised it, so {gated} is "
                    + "UNEXECUTED on a run that said it would execute it."
                );

                return;

            default:
                Assert.Skip(
                    $"'{adapter}' offers no '{capability}', which {gated} is gated on — so it is "
                    + $"UNEXECUTED on this run. Set {variable}=1 on a runner whose device has it to "
                    + "make this a failure rather than a skip."
                );

                return;
        }
    }

    /// <summary>Fails unless the device's feature description demonstrably ran.</summary>
    /// <param name="device">The open device.</param>
    /// <remarks>
    ///     ⚠ <b>For the gates that read a capability and branch the other way</b> — a test that
    ///     stands aside because the device <em>has</em> something, or picks whichever of two modes a
    ///     device declines. <see cref="Require" /> cannot express those: absence is what they want,
    ///     so a promise about absence is meaningless. The canary still applies, and applies harder,
    ///     because an undescribed device declines everything and such a test would run its whole
    ///     body against a device nobody had asked a single question of.
    /// </remarks>
    public static void Described(VulkanDevice device) {
        ArgumentNullException.ThrowIfNull(device);

        Assert.True(device.Features.HasCompute, DescriptionMissing(device.Adapter.Name, "any capability"));
    }

    /// <summary>What to say when the feature description never ran.</summary>
    static string DescriptionMissing(string adapter, string capability) =>
        $"'{adapter}' reports no compute, and there is no Vulkan device without compute. Its feature "
        + $"description never ran, so its answer about '{capability}' is "
        + "GraphicsDeviceFeatures.Minimum's default rather than a fact about this device — and every "
        + "capability gate in this assembly would take the absent branch.";

    /// <summary>Whether an environment variable reads as set.</summary>
    /// <remarks>The spelling every <c>VIXEN_REQUIRE_*</c> reader in the tree already uses.</remarks>
    static bool Truthy(string? value) => value is "1" or "true" or "TRUE";

    /// <summary>The capability names a promise contains.</summary>
    static HashSet<string> Parse(string? promised) {
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(promised)) {
            return wanted;
        }

        foreach (var token in promised.Split(Separators, StringSplitOptions.RemoveEmptyEntries)) {
            wanted.Add(token.Trim().ToLowerInvariant());
        }

        return wanted;
    }
}
