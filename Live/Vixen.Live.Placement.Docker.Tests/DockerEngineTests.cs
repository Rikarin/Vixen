// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Placement.Tests;

/// <summary>The hand-written Engine API client, against a real Docker daemon.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything else in this project is asserted against a fake <c>HttpMessageHandler</c>,
///         and that is right</b> — the placement logic is what changes, and a fake makes it assertable
///         on every push. What a fake cannot answer is whether the calls this client makes are the
///         calls the Engine API actually has: whether the socket path is right, whether
///         <c>v1.43</c> is a version a modern daemon still serves, whether the container spec is
///         accepted, whether an image really reports its entrypoint where this client looks for it,
///         and whether the eight-byte log framing is what a TTY-less container really produces.
///         ADR-019 bet that this surface was small enough to hand-write; this is the test that says
///         the bet is still paying.
///     </para>
///     <para>
///         ⚠ <b>The suite pulls the images it needs.</b> A fake cannot be wrong about what an image
///         contains and a real daemon can, so which image is used is part of what is under test —
///         and a suite that depended on whatever a workflow happened to have pulled would be one
///         where that could quietly stop being true.
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
    ///     A stand-in for a realm image: small, pulled in a second, and long-lived enough to be found
    ///     by its label before it is stopped.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A realm image is one that stays up and tolerates its arguments, and almost no
    ///     off-the-shelf image is both.</b> Nearly every published entrypoint ends in
    ///     <c>exec "$@"</c>, so a stock image handed <c>--realm-spec …</c> exits before this test can
    ///     look at it — which is why <see cref="Entrypoint" /> supplies the one thing a real realm
    ///     image carries in its own <c>ENTRYPOINT</c>.
    /// </remarks>
    const string Image = "alpine:3.20";

    /// <summary>
    ///     What the image is made to run: a shell that ignores the arguments after it and stays alive.
    /// </summary>
    static readonly IReadOnlyList<string> Entrypoint = ["/bin/sh", "-c", "sleep 600"];

    /// <summary>An image with no <c>ENTRYPOINT</c> at all, which is the failure this suite watches for.</summary>
    const string EntrypointlessImage = "hello-world";

    [Fact]
    public async Task The_daemon_answers_the_version_probe() {
        using var engine = await OpenAsync();

        var probe = await engine.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(probe.Available, probe.Detail);
        Assert.NotEmpty(probe.Detail);

        // The probe reads the image as well as the daemon, so it can say what a realm would be
        // launched as — and a real daemon is the only thing that can confirm the inspect call is real.
        Assert.Contains(RealmSpec.ArgumentName, probe.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The regression this suite exists for. An image with no entrypoint makes
    ///     <c>--realm-spec</c> the program, and the daemon's answer — <c>executable file not found in
    ///     $PATH</c> — is a 400 from a container that already exists. A backend that only found that
    ///     out from the daemon would be one nightly failure away from silence.
    /// </summary>
    [Fact]
    public async Task An_image_with_no_entrypoint_is_refused_rather_than_discovered() {
        RequireDaemon();

        using var engine = new DockerEngine();

        await engine.PullAsync(EntrypointlessImage, TestContext.Current.CancellationToken);

        using var placement = new DockerPlacement(
            new() { Image = EntrypointlessImage },
            engine,
            ownsEngine: false
        );

        var probe = await placement.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(probe.Available);
        Assert.Contains(RealmSpec.ArgumentName, probe.Detail, StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await placement.StartAsync(Realm(), TestContext.Current.CancellationToken)
        );

        Assert.Contains(EntrypointlessImage, failure.Message, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The whole round trip ADR-019 argued was a handful of calls: inspect, create, start, list by
    ///     label, stop, and be gone.
    /// </summary>
    [Fact]
    public async Task A_container_is_created_found_by_its_label_and_removed() {
        using var engine = await OpenAsync();

        var spec = Realm();
        var shard = spec.Shard;

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

        // ⚠ Polled, because StopAsync returns before the container has gone: stopping is fire and
        // forget by design — an orchestrator asks a shard to stop and hears about it through
        // WatchAsync, and only then is the container removed. Asserting once here would be asserting
        // on the daemon's reaction time.
        //
        // Disposing deliberately leaves containers running (a container that outlives its
        // orchestrator is a shard still serving players), so the stop above is what must remove it.
        await Eventually(
            async () => {
                var remaining = await engine.ListAsync(TestContext.Current.CancellationToken);

                return remaining.All(found => found.Shard != shard);
            },
            $"shard {shard} was still listed"
        );
    }

    static RealmSpec Realm() =>
        new() {
            Shard = ShardId.New(),
            Key = new("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE)),
            Endpoint = new("127.0.0.1", 0),
            Capacity = new(10, 12),
            TickRate = 30
        };

    static async Task<DockerPlacement> OpenAsync() {
        RequireDaemon();

        var engine = new DockerEngine();

        try {
            // Pulled here rather than by the workflow, so the suite says which image it needs and a
            // laptop running it with VIXEN_DOCKER=1 gets the same answer a runner does.
            await engine.PullAsync(Image, TestContext.Current.CancellationToken);
        } catch {
            engine.Dispose();

            throw;
        }

        return new(
            new() {
                Image = Image,
                Entrypoint = Entrypoint,

                // A second, not the default fifteen: nothing in this container handles SIGTERM, so
                // the grace is time the test would spend waiting to be allowed to kill it.
                StopGrace = TimeSpan.FromSeconds(1)
            },
            engine,
            ownsEngine: true
        );
    }

    static async Task Eventually(Func<Task<bool>> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline) {
            if (await condition()) {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"After thirty seconds, {what}.");
    }

    static void RequireDaemon() =>
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal),
            $"{Variable} is not set, so no Docker daemon is being driven. The nightly leg sets it; a "
            + "laptop is expected not to, because this starts containers."
        );
}
