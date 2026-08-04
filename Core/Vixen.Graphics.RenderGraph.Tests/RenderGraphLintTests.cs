// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>What the graph warns about: frames that run and quietly waste work.</summary>
public sealed class RenderGraphLintTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly TransientResourcePool pool;
    readonly RenderGraph graph;

    public RenderGraphLintTests() {
        pool = new(device);
        graph = new(device, pool);
    }

    public void Dispose() {
        pool.Dispose();
        device.Dispose();
    }

    static TextureDescription Target(string name) =>
        new(PixelFormat.Rgba8UNorm, 256, 256, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: name);

    /// <summary>
    ///     A stored result cleared by the next pass before anything reads it is work discarded every
    ///     frame — the shape sample 13's visibility resolve had, drawing a whole pass into a colour
    ///     the sky overwrote. It draws, so it must warn rather than throw, and it must name both
    ///     passes and the resource.
    /// </summary>
    [Fact]
    public void ADiscardedWriteIsWarnedAboutOnce() {
        var shared = graph.CreateTexture(Target("shared"));

        graph.AddPass("producer", pass => {
            pass.ColourAttachment(shared);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.AddPass("overwriter", pass => {
            pass.ColourAttachment(shared, LoadAction.Clear);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        var warning = Assert.Single(graph.Warnings);

        Assert.Contains("VX2101", warning, StringComparison.Ordinal);
        Assert.Contains("producer", warning, StringComparison.Ordinal);
        Assert.Contains("overwriter", warning, StringComparison.Ordinal);
        Assert.Contains("shared", warning, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A loaded attachment is a read-modify-write: the second pass consumes the first's result,
    ///     so there is nothing to warn about. This is the distinction that makes the lint quiet on
    ///     every legitimate accumulation chain — sky into scene colour, sparks onto the lit frame.
    /// </summary>
    [Fact]
    public void ALoadedOverwriteIsNotAWarning() {
        var shared = graph.CreateTexture(Target("shared"));

        graph.AddPass("producer", pass => {
            pass.ColourAttachment(shared);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.AddPass("accumulator", pass => {
            pass.ColourAttachment(shared, LoadAction.Load);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Empty(graph.Warnings);
    }

    /// <summary>
    ///     A read between the write and the clear consumes the result, so a ping-pong that clears a
    ///     target it has already been read out of is the ordinary shape of a frame, not a finding.
    /// </summary>
    [Fact]
    public void AConsumedWriteMayBeCleared() {
        var shared = graph.CreateTexture(Target("shared"));
        var derived = graph.CreateTexture(Target("derived"));

        graph.AddPass("producer", pass => {
            pass.ColourAttachment(shared);
            pass.Execute(_ => { });
        });

        graph.AddPass("reader", pass => {
            pass.Reads(shared);
            pass.ColourAttachment(derived);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.AddPass("recycler", pass => {
            pass.ColourAttachment(shared, LoadAction.Clear);
            pass.SideEffect();
            pass.Execute(_ => { });
        });

        graph.Compile();

        Assert.Empty(graph.Warnings);
    }

    /// <summary>
    ///     The same finding across two frames is one log line, or the reader mutes the channel. The
    ///     list is append-only across resets and deduplicated by message.
    /// </summary>
    [Fact]
    public void AFindingIsReportedOnceAcrossRebuilds() {
        for (var frame = 0; frame < 3; frame++) {
            graph.Reset();

            var shared = graph.CreateTexture(Target("shared"));

            graph.AddPass("producer", pass => {
                pass.ColourAttachment(shared);
                pass.SideEffect();
                pass.Execute(_ => { });
            });

            graph.AddPass("overwriter", pass => {
                pass.ColourAttachment(shared, LoadAction.Clear);
                pass.SideEffect();
                pass.Execute(_ => { });
            });

            graph.Compile();
        }

        Assert.Single(graph.Warnings);
    }
}
