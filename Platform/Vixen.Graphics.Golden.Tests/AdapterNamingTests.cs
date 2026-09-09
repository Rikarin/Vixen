// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Opening a device in this suite names the adapter, even when the test does not ask.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nineteen device files here named no adapter at all</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/795" />). This is the suite whose numbers
///         are least attributable and the one where eighteen files <em>passed</em> rather than skipped
///         without a device until 2026-08-21 — so a picture that differed, a timing that regressed or
///         a leak that appeared could not be pinned to a machine, and the thing a golden failure is
///         mostly made of is its message.
///     </para>
///     <para>
///         ✅ <b>The refusal that made #795 look unfixable has expired.</b> It looked for a
///         <c>build/</c> rule with a scope: every derivation from a project graph pulled this suite
///         into doc 48's, and a written list of project names is the exact-equality roll call that
///         has gone red on a merge five times. It needs neither, because
///         <see cref="DeviceGuardTests.OnlyTheFixtureOpensADevice" /> has since held the door at one
///         — <see cref="Fixture.TryOpen" /> is the only thing in the assembly that creates a device,
///         so naming the adapter there names it for every device test here including the next one.
///     </para>
///     <para>
///         ⚠ <b>The instrument is the assertion before the finding: this test writes nothing
///         itself.</b> Its own output is empty until <c>TryOpen</c> is called, so the adapter's name
///         being there afterwards is the fixture's doing and cannot be this test's. Without a device
///         it skips, loudly, like every other device test here.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class AdapterNamingTests {
    [Fact]
    public void OpeningADeviceNamesTheAdapterWithoutBeingAsked() {
        var output = TestContext.Current.TestOutputHelper;

        Assert.NotNull(output);
        Assert.DoesNotContain("adapter:", output!.Output, StringComparison.Ordinal);

        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;

        // The whole line the fixture writes, so this is a claim about the name, the kind and the
        // driver version rather than about the word "adapter" appearing.
        Assert.Contains("adapter:", output.Output, StringComparison.Ordinal);
        Assert.Contains(Fixture.Adapter(owned.Device), output.Output, StringComparison.Ordinal);
    }

    /// <summary>The suite's guard, spelled as every other device class here spells it.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
