// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>The hand-written Engine API client, against a real Docker daemon.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything else in this project is asserted against a fake <c>HttpMessageHandler</c>,
///         and that is right</b> — the placement logic is what changes, and a fake makes it assertable
///         on every push. What a fake cannot answer is whether the six calls this client makes are the
///         calls the Engine API actually has: whether the socket path is right, whether
///         <c>v1.43</c> is a version a modern daemon still serves, whether the container spec is
///         accepted, and whether the eight-byte log framing is what a TTY-less container really
///         produces. ADR-019 bet that this surface was small enough to hand-write; this is the test
///         that says the bet is still paying.
///     </para>
///     <para>
///         ⚠ <b>Skipped without <c>VIXEN_DOCKER</c>.</b> A laptop may have no daemon, or one the
///         developer does not want a test starting containers on. doc 27 § Testing puts this on the
///         nightly leg for exactly that reason.
///     </para>
/// </remarks>
public class DockerEngineTests {
    /// <summary>What the nightly leg sets to opt in.</summary>
    const string Variable = "VIXEN_DOCKER";

    /// <summary>
    ///     An image every runner already has and which exits immediately, so a failed clean-up leaves
    ///     nothing running.
    /// </summary>
    const string Image = "hello-world";

    [Fact]
    public async Task The_daemon_answers_the_version_probe() {
        using var engine = Open();

        var probe = await engine.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Available, probe.Detail);
        Assert.NotEmpty(probe.Detail);
    }

    /// <summary>
    ///     The whole round trip ADR-019 argued was six calls: create, start, list by label, read the
    ///     log stream, stop, and be gone.
    /// </summary>
    [Fact]
    public async Task A_container_is_created_found_by_its_label_and_removed() {
        using var engine = Open();

        var shard = ShardId.New();

        var spec = new RealmSpec {
            Shard = shard,
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = new("127.0.0.1", 0),
            Capacity = new(10, 12),
            TickRate = 30,
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["image"] = Image }
        };

        var instance = await engine.StartAsync(spec, TestContext.Current.CancellationToken);

        try {
            Assert.True(instance.Id.IsValid);

            // ADR-019's labels are how the next orchestrator finds what this one started, so a
            // listing that cannot see its own container is the reconciliation story broken.
            var listed = await engine.ListAsync(TestContext.Current.CancellationToken);

            Assert.Contains(listed, found => found.Shard == shard);
        } finally {
            await engine.StopAsync(instance.Id, StopMode.Immediate, CancellationToken.None);
        }

        // Disposing deliberately leaves containers running (a container that outlives its
        // orchestrator is a shard still serving players), so the stop above is what must have
        // removed it.
        var remaining = await engine.ListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(remaining, found => found.Shard == shard);
    }

    static DockerPlacement Open() {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal),
            $"{Variable} is not set, so no Docker daemon is being driven. The nightly leg sets it; a "
            + "laptop is expected not to, because this starts containers."
        );

        return new(new() { Image = Image });
    }
}
