// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Vixen.Graphics;
using Vixen.ShaderCompiler;
using Vixen.ShaderCompilerService;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A device asking a dev machine for a shader — docs/plan/06 § Effect permutations, the remote
///     compiler.
/// </summary>
public class ShaderCompilerServiceTests {
    static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nested.rvn");

    static RavenEffectCompiler Compiler() => new([Fixture]);

    static ShaderCompilerServer Serving(Func<string, IEffectSource?>? open = null) {
        var server = new ShaderCompilerServer(IPAddress.Loopback, 0, open ?? (_ => Compiler()));
        server.Start();
        return server;
    }

    static EffectKey Key(bool outer, bool inner) =>
        EffectKey.Of("Nested", [new("Nested.Outer", outer ? "true" : "false"), new("Nested.Inner", inner ? "true" : "false")]);

    // --- The round trip -----------------------------------------------------

    /// <summary>
    ///     A variant compiled here arrives there with its bytes intact.
    /// </summary>
    /// <remarks>
    ///     The whole claim of the remote tier, and it is a claim about the wire: the record crosses a
    ///     socket in the engine's own serialisation, so what a device loads is byte for byte what the
    ///     compiler produced. A protocol that lost the modules would still look like it worked
    ///     everywhere except on the device.
    /// </remarks>
    [Fact]
    public void A_device_gets_the_variant_it_asked_for() {
        using var server = Serving();
        using var source = new RemoteEffectSource("localhost", server.Port);

        var remote = source.TryGet(Key(outer: true, inner: true));
        var local = Compiler().TryGet(Key(outer: true, inner: true));

        Assert.NotNull(remote);
        Assert.NotNull(local);

        Assert.Equal(local.ToKey(), remote.ToKey());
        Assert.Equal(
            local.Stages.Single(stage => stage.Stage == ShaderStage.Fragment).Bytecode,
            remote.Stages.Single(stage => stage.Stage == ShaderStage.Fragment).Bytecode
        );

        Assert.Equal(1, server.Served);
        Assert.Equal(1, source.Served);
    }

    /// <summary>Two variants over one connection are two different modules.</summary>
    /// <remarks>
    ///     The connection is kept open between requests, so this also asserts the framing: a reader
    ///     that treated one read as one message would work for the first request and put the second
    ///     one's answer somewhere unreadable.
    /// </remarks>
    [Fact]
    public void One_connection_serves_many_requests() {
        using var server = Serving();
        using var source = new RemoteEffectSource("localhost", server.Port);

        var on = source.TryGet(Key(outer: true, inner: true))!;
        var off = source.TryGet(Key(outer: true, inner: false))!;

        Assert.NotEqual(on.ToKey(), off.ToKey());
        Assert.NotEqual(
            on.Stages.Single(stage => stage.Stage == ShaderStage.Fragment).Bytecode,
            off.Stages.Single(stage => stage.Stage == ShaderStage.Fragment).Bytecode
        );

        Assert.Equal(2, server.Served);
    }

    /// <summary>A shader the server does not have is a miss, and the tier below gets its turn.</summary>
    [Fact]
    public void An_unknown_shader_comes_back_as_a_miss() {
        using var server = Serving();
        using var source = new RemoteEffectSource("localhost", server.Port);

        Assert.Null(source.TryGet(EffectKey.Of("Nonexistent")));
        Assert.Equal(1, server.Missed);
        Assert.NotEmpty(source.Diagnostics);
    }

    /// <summary>A target this machine cannot produce is said so rather than answered wrongly.</summary>
    [Fact]
    public void An_unavailable_target_is_refused() {
        using var server = Serving(target => target is "spirv" or "" ? Compiler() : null);
        using var source = new RemoteEffectSource("localhost", server.Port) { Target = "metal" };

        Assert.Null(source.TryGet(Key(outer: true, inner: true)));
        Assert.Contains("metal", string.Join(" ", source.Diagnostics), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A machine that is not there is a miss, not a crash.
    /// </summary>
    /// <remarks>
    ///     The laptop is asleep, the cable came out, the port moved. All of those mean this tier has
    ///     no answer and the tier below should get its turn — a frame drawn with a placeholder beats
    ///     a frame that throws.
    /// </remarks>
    [Fact]
    public void A_compiler_that_is_not_there_is_a_miss() {
        // A port nothing is listening on: bound and released, so it is free and almost certainly
        // still free a millisecond later.
        int port;

        using (var server = Serving()) {
            port = server.Port;
        }

        using var source = new RemoteEffectSource("localhost", port) { Timeout = TimeSpan.FromSeconds(2) };

        Assert.Null(source.TryGet(Key(outer: true, inner: true)));
        Assert.NotEmpty(source.Diagnostics);
    }

    /// <summary>A broken shader sends back what the compiler said.</summary>
    /// <remarks>
    ///     The service outlives every device that connects to it, so a shader somebody is halfway
    ///     through editing must not be able to take it down — and the person editing wants the
    ///     diagnostics, which is the one thing a closed socket cannot carry.
    /// </remarks>
    [Fact]
    public void A_broken_shader_sends_back_its_diagnostics() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".rvn");
        File.WriteAllText(path, "package Vixen.Fixtures\n\nshader Broken {\n    var tint: float3\n\n    [PixelShader]\n    [Semantic(\"SV_Target\")]\n    func Pixel(): float4 { return nonsense }\n}\n");

        using var server = Serving(_ => new RavenEffectCompiler([path]));
        using var source = new RemoteEffectSource("localhost", server.Port);

        Assert.Null(source.TryGet(EffectKey.Of("Broken")));
        Assert.NotEmpty(source.Diagnostics);
        Assert.Equal(1, server.Missed);
    }

    // --- The tiers, stacked -------------------------------------------------

    /// <summary>
    ///     A device that has asked once does not ask again.
    /// </summary>
    /// <remarks>
    ///     What makes the remote compiler tolerable rather than merely possible: the round trip is
    ///     paid once per variant per device, and the disk cache on the device makes it once per
    ///     variant per <em>install</em>. The two caches are for different things — the one on the
    ///     server saves the compilation, this one saves the wire.
    /// </remarks>
    [Fact]
    public void A_cached_variant_does_not_cross_the_wire_twice() {
        var directory = Path.Combine(Path.GetTempPath(), "vixen-remote-cache", Guid.NewGuid().ToString("n"));

        using var server = Serving();
        using var remote = new RemoteEffectSource("localhost", server.Port);

        var device = new EffectDiskCache(directory, "spirv", remote);

        Assert.NotNull(device.TryGet(Key(outer: true, inner: true)));
        Assert.NotNull(device.TryGet(Key(outer: true, inner: true)));

        Assert.Equal(1, server.Served);
        Assert.Equal(1, device.Hits);
    }

}
