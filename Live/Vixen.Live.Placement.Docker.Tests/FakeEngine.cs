// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Vixen.Live.Placement.Tests;

/// <summary>A Docker daemon that is a dictionary and a handler.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What is under test is the client's dialect and the backend's behaviour, not the
///         daemon.</b> Whether a real Engine accepts these requests is a question only a real Engine
///         answers, and doc 27 § Testing puts that on the nightly leg alongside Kubernetes. What runs
///         on every push is: does the client send the right body, does it read the framed log stream
///         correctly, does the backend label containers so a restart can find them, and is an exit
///         nobody asked for reported as <c>Lost</c>.
///     </para>
///     <para>
///         The log stream is genuinely framed here — eight-byte headers and all — because the framing
///         is the one piece of this client that is not ordinary HTTP, and a fake that returned plain
///         text would test the demultiplexer by not exercising it.
///     </para>
/// </remarks>
sealed class FakeEngine : HttpMessageHandler {
    readonly ConcurrentDictionary<string, Container> containers = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<string> requests = new();

    int nextId = 1000;

    /// <summary>What the daemon says it is, or null to refuse the ping.</summary>
    public string? Version { get; set; } = "24.0.7";

    /// <summary>
    ///     What the image says its entrypoint is, or null for an image that has none — which is what
    ///     a stock <c>hello-world</c> is, and what turns the first argument into the program.
    /// </summary>
    public IReadOnlyList<string>? Entrypoint { get; set; } = ["./YourGame.Realm"];

    /// <summary>Every path the client asked for, in order.</summary>
    public IReadOnlyCollection<string> Requests => requests;

    /// <summary>The bodies the client posted, by path.</summary>
    public List<(string Path, string Body)> Posts { get; } = [];

    /// <summary>Every container the daemon holds.</summary>
    public IReadOnlyCollection<Container> Containers => [.. containers.Values];

    /// <summary>The most recently created one.</summary>
    public Container Last => containers.Values.OrderBy(entry => entry.Created).Last();

    /// <summary>Says a line as a container would.</summary>
    /// <param name="id">Which container.</param>
    /// <param name="line">What it wrote.</param>
    public void Say(string id, string line) => containers[id].Output.Writer.TryWrite(line);

    /// <summary>Ends a container, as the daemon would.</summary>
    /// <param name="id">Which container.</param>
    /// <param name="code">Its exit code.</param>
    public void Exit(string id, int code) {
        var container = containers[id];

        container.State = "exited";
        container.ExitCode = code;
        container.Output.Writer.TryComplete();
        container.Exited.TrySetResult(code);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;

        requests.Enqueue(path);

        if (request.Content is not null) {
            Posts.Add((path, await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)));
        }

        if (path.EndsWith("/version", StringComparison.Ordinal)) {
            return Version is null
                ? new(HttpStatusCode.ServiceUnavailable)
                : Json($$"""{"Version":"{{Version}}","Os":"linux"}""");
        }

        if (path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal)) {
            return Json(JsonSerializer.Serialize(new { Config = new { Entrypoint } }));
        }

        if (path.EndsWith("/containers/create", StringComparison.Ordinal)) {
            var id = Interlocked.Increment(ref nextId).ToString(CultureInfo.InvariantCulture);
            var body = JsonDocument.Parse(Posts[^1].Body).RootElement;

            var labels = body.TryGetProperty("labels", out var declared)
                ? declared.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString() ?? "", StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            containers[id] = new(id, labels, Posts[^1].Body);

            return Json($$"""{"Id":"{{id}}"}""");
        }

        if (path.EndsWith("/start", StringComparison.Ordinal)) {
            containers[Id(path)].State = "running";

            return new(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/stop", StringComparison.Ordinal)) {
            Exit(Id(path), 0);

            return new(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/wait", StringComparison.Ordinal)) {
            var code = await containers[Id(path)].Exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return Json($$"""{"StatusCode":{{code}}}""");
        }

        if (path.EndsWith("/logs", StringComparison.Ordinal)) {
            return new(HttpStatusCode.OK) { Content = new StreamContent(new FrameStream(containers[Id(path)])) };
        }

        if (path.EndsWith("/containers/json", StringComparison.Ordinal)) {
            var wanted = Uri.UnescapeDataString(query);

            var listed = containers.Values
                .Where(entry => entry.Labels.Any(label => wanted.Contains($"{label.Key}={label.Value}", StringComparison.Ordinal)))
                .Select(entry => new {
                        Id = entry.Id,
                        Labels = entry.Labels,
                        State = entry.State
                    }
                );

            return Json(JsonSerializer.Serialize(listed));
        }

        if (request.Method == HttpMethod.Delete) {
            containers.TryRemove(Id(path), out _);

            return new(HttpStatusCode.NoContent);
        }

        return new(HttpStatusCode.NotFound);
    }

    static string Id(string path) {
        var parts = path.Split('/');

        return parts[Array.IndexOf(parts, "containers") + 1];
    }

    static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed class Container(string id, Dictionary<string, string> labels, string body) {
        public string Id { get; } = id;

        public Dictionary<string, string> Labels { get; } = labels;

        public string Body { get; } = body;

        public DateTime Created { get; } = DateTime.UtcNow;

        public string State { get; set; } = "created";

        public int ExitCode { get; set; }

        public Channel<string> Output { get; } = Channel.CreateUnbounded<string>();

        public TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The framed log stream, written the way the daemon writes it.</summary>
    sealed class FrameStream(Container container) : Stream {
        byte[] pending = [];
        int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            if (offset >= pending.Length) {
                string line;

                try {
                    line = await container.Output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                } catch (ChannelClosedException) {
                    return 0;
                }

                var payload = Encoding.UTF8.GetBytes(line + "\n");

                pending = new byte[8 + payload.Length];
                pending[0] = 1;
                BinaryPrimitives.WriteInt32BigEndian(pending.AsSpan(4), payload.Length);
                payload.CopyTo(pending.AsSpan(8));
                offset = 0;
            }

            var take = Math.Min(buffer.Length, pending.Length - offset);

            pending.AsSpan(offset, take).CopyTo(buffer.Span);
            offset += take;

            return take;
        }

        public override int Read(byte[] buffer, int start, int count) =>
            ReadAsync(buffer.AsMemory(start, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
