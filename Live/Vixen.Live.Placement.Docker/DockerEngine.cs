// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vixen.Live.Placement;

/// <summary>What the daemon said when asked whether it was there.</summary>
/// <param name="Reachable">Whether it answered.</param>
/// <param name="Version">Its version, when it did.</param>
/// <param name="Detail">What was seen, for a log.</param>
public readonly record struct DockerPing(bool Reachable, string Version, string Detail);

/// <summary>One container, as much of it as this backend cares about.</summary>
/// <param name="Id">The daemon's id.</param>
/// <param name="Labels">Its labels, which is where a shard id is kept.</param>
/// <param name="State">Its state — <c>running</c>, <c>exited</c>.</param>
public sealed record DockerContainer(string Id, IReadOnlyDictionary<string, string> Labels, string State);

/// <summary>What to create.</summary>
/// <param name="Image">The realm's image.</param>
/// <param name="Arguments">Its command, which carries the encoded <see cref="RealmSpec" />.</param>
/// <param name="Labels">Labels to put on it, including the shard id.</param>
/// <param name="Environment">Variables to set.</param>
/// <param name="Port">The UDP port to publish, host and container alike.</param>
/// <remarks>
///     ⚠ <b><paramref name="Arguments" /> is the container's <c>Cmd</c>, which is not the whole
///     command.</b> Docker execs the image's <c>ENTRYPOINT</c> with <c>Cmd</c> appended, so these are
///     the realm's arguments only — and only when the image has an entrypoint to append them to. An
///     image without one turns <c>Arguments[0]</c> into the program, which is how a container comes up
///     dead saying <c>--realm-spec: executable file not found</c>.
/// </remarks>
public sealed record DockerCreate(
    string Image,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Environment,
    int Port
) {
    /// <summary>The program to run, or empty for the image's own entrypoint.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty means "leave the image's alone", and it is sent as nothing at all.</b> An
    ///     entrypoint of <c>[]</c> is a different instruction to the daemon — it <em>clears</em> the
    ///     image's — and clearing it is what turns the first argument into the program.
    /// </remarks>
    public IReadOnlyList<string> Entrypoint { get; init; } = [];
}

/// <summary>The eight calls, hand-written. ADR-019.</summary>
/// <remarks>
///     <para>
///         <b>A <c>SocketsHttpHandler</c> with a <c>ConnectCallback</c>, and the rest is JSON.</b> The
///         Engine API is an ordinary HTTP API that happens to live on a unix socket, so the only
///         unusual line in this file is the one that dials the socket instead of a TCP endpoint.
///     </para>
///     <para>
///         ⚠ <b>The handler is injectable, and that is what makes this testable.</b> A test supplies
///         one that answers with recorded Engine responses, so the placement backend's behaviour —
///         labels, ports, ready detection, exit classification — is asserted without a daemon. What a
///         test cannot assert is that the daemon really speaks this dialect, and that is what the
///         nightly Docker leg is for.
///     </para>
/// </remarks>
public sealed class DockerEngine : IDisposable {
    /// <summary>Where the daemon listens on Linux and macOS.</summary>
    public const string DefaultSocket = "/var/run/docker.sock";

    /// <summary>Where it listens on Windows.</summary>
    public const string DefaultPipe = @"\\.\pipe\docker_engine";

    /// <summary>
    ///     The API version this client speaks. Pinned, because the Engine negotiates by URL prefix
    ///     and an unpinned client silently changes dialect when the host is upgraded.
    /// </summary>
    public const string ApiVersion = "v1.43";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly HttpClient client;
    readonly bool ownsClient;

    bool disposed;

    /// <summary>Opens a client on the platform's default endpoint.</summary>
    /// <param name="endpoint">
    ///     A socket path, a <c>npipe://</c> pipe name, or null for the platform's default.
    /// </param>
    public DockerEngine(string? endpoint = null) : this(Connect(endpoint), ownsClient: true) { }

    /// <summary>Opens a client on a handler the caller built.</summary>
    /// <param name="handler">What to send over. Disposed with this.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handler" /> is null.</exception>
    public DockerEngine(HttpMessageHandler handler)
        : this(new HttpClient(handler) { BaseAddress = new("http://docker/") }, ownsClient: true) { }

    DockerEngine(HttpClient client, bool ownsClient) {
        ArgumentNullException.ThrowIfNull(client);

        this.client = client;
        this.ownsClient = ownsClient;
    }

    /// <summary>Asks whether the daemon is there.</summary>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What it said, or why it did not.</returns>
    /// <remarks>
    ///     Never throws. A probe is asked of every backend at startup and the first that answers
    ///     wins, so "Docker is not installed" has to be an answer rather than an exception.
    /// </remarks>
    public async Task<DockerPing> PingAsync(CancellationToken cancellation) {
        try {
            using var response = await client.GetAsync(Url("version"), cancellation).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                return new(false, "", $"the daemon answered {(int)response.StatusCode}");
            }

            var version = await response.Content
                .ReadFromJsonAsync<VersionResponse>(Json, cancellation)
                .ConfigureAwait(false);

            return new(true, version?.Version ?? "", $"Engine {version?.Version} on {version?.Os}");
        } catch (Exception failure) when (failure is HttpRequestException or SocketException or IOException or JsonException) {
            return new(false, "", failure.Message);
        }
    }

    /// <summary>Creates a container.</summary>
    /// <param name="create">What to create.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Its id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="create" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The daemon refused.</exception>
    public async Task<string> CreateAsync(DockerCreate create, CancellationToken cancellation) {
        ArgumentNullException.ThrowIfNull(create);

        var port = create.Port.ToString(CultureInfo.InvariantCulture);

        var body = new CreateRequest {
            Image = create.Image,

            // Null, not empty, when nothing was asked for: an entrypoint of `[]` clears the image's
            // rather than leaving it alone, and a cleared entrypoint is what promotes the first
            // argument to the program. `WhenWritingNull` is what drops the field entirely.
            Entrypoint = create.Entrypoint.Count > 0 ? [.. create.Entrypoint] : null,
            Cmd = [.. create.Arguments],
            Labels = create.Labels.ToDictionary(StringComparer.Ordinal),
            Env = [.. create.Environment.Select(entry => $"{entry.Key}={entry.Value}")],

            // ⚠ Not a TTY. Without one the log stream is framed, which this client demultiplexes —
            // and with one, stdout and stderr are merged into a single stream with no way to tell a
            // realm's ready line from something it printed to stderr. Doc 27's lifecycle travels on
            // stdout, and a merged stream would make "ready" a substring match on everything.
            Tty = false,
            ExposedPorts = new Dictionary<string, object> { [$"{port}/udp"] = new() },
            HostConfig = new() {
                PortBindings = new Dictionary<string, List<PortBinding>> {
                    [$"{port}/udp"] = [new() { HostPort = port }]
                },

                // A realm that exits has exited. Doc 27 § Health: a shard whose process is gone is
                // Lost and its replacement is a *placement* — a container the daemon restarted
                // underneath the orchestrator would be a shard the cluster had written off, coming
                // back with no players and no map.
                RestartPolicy = new() { Name = "no" }
            }
        };

        using var response = await client
            .PostAsJsonAsync(Url("containers/create"), body, Json, cancellation)
            .ConfigureAwait(false);

        await Refuse(response, "create a container", cancellation).ConfigureAwait(false);

        var created = await response.Content
            .ReadFromJsonAsync<CreateResponse>(Json, cancellation)
            .ConfigureAwait(false);

        return created?.Id
            ?? throw new InvalidOperationException("The daemon created a container and did not say which.");
    }

    /// <summary>Asks an image what program it runs.</summary>
    /// <param name="image">The image, by name and tag or by digest.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>Its <c>ENTRYPOINT</c>, or empty when it has none.</returns>
    /// <exception cref="InvalidOperationException">The daemon refused, or has no such image.</exception>
    /// <remarks>
    ///     ⚠ <b>The call that makes a dead container diagnosable.</b> A realm's arguments are appended
    ///     to the image's entrypoint, so an image with no entrypoint runs
    ///     <c><see cref="RealmSpec.ArgumentName" /></c> as a program and the daemon's complaint is
    ///     about a missing executable rather than a missing entrypoint. Asking beforehand is one round
    ///     trip against a container start, and it turns that into a sentence naming the image.
    /// </remarks>
    public async Task<IReadOnlyList<string>> InspectEntrypointAsync(string image, CancellationToken cancellation) {
        using var response = await client.GetAsync(Url($"images/{image}/json"), cancellation).ConfigureAwait(false);

        await Refuse(response, $"inspect {image}", cancellation).ConfigureAwait(false);

        var inspected = await response.Content
            .ReadFromJsonAsync<InspectResponse>(Json, cancellation)
            .ConfigureAwait(false);

        return inspected?.Config?.Entrypoint ?? [];
    }

    /// <summary>Fetches an image the daemon does not have yet.</summary>
    /// <param name="image">The image, by name and tag or by digest.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When the image is there.</returns>
    /// <exception cref="InvalidOperationException">The daemon refused, or the pull failed.</exception>
    /// <remarks>
    ///     ⚠ <b>Not called by <see cref="DockerPlacement" />, and that is a policy decision rather than
    ///     an omission.</b> Whether a node pulls on every placement, only when the image is absent, or
    ///     never — because a deployment pre-warms its nodes — is the orchestrator's to make. What this
    ///     backend owes it is the ability to.
    /// </remarks>
    public async Task PullAsync(string image, CancellationToken cancellation) {
        var (name, tag) = Reference(image);

        using var response = await client
            .PostAsync(
                Url($"images/create?fromImage={Uri.EscapeDataString(name)}&tag={Uri.EscapeDataString(tag)}"),
                content: null,
                cancellation
            )
            .ConfigureAwait(false);

        await Refuse(response, $"pull {image}", cancellation).ConfigureAwait(false);

        // ⚠ Reading the body to the end is what makes this call mean "the image is here" rather than
        // "the pull has begun" — the daemon answers 200 and then streams progress, and a layer it
        // could not fetch is reported inside that stream and not by the status code.
        var progress = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        if (progress.Contains("\"errorDetail\"", StringComparison.Ordinal)) {
            throw new InvalidOperationException($"Docker would not pull {image}: {progress.Trim()}");
        }
    }

    /// <summary>Starts one.</summary>
    /// <param name="id">Which container.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When started.</returns>
    /// <exception cref="InvalidOperationException">The daemon refused.</exception>
    public async Task StartAsync(string id, CancellationToken cancellation) {
        using var response = await client
            .PostAsync(Url($"containers/{id}/start"), content: null, cancellation)
            .ConfigureAwait(false);

        await Refuse(response, $"start {id}", cancellation).ConfigureAwait(false);
    }

    /// <summary>Stops one, politely and then not.</summary>
    /// <param name="id">Which container.</param>
    /// <param name="patience">How long the process gets after its signal.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When stopped, or when it was already gone.</returns>
    /// <remarks>
    ///     A container that is not there is not an error: every backend races the thing it manages,
    ///     and a caller that had to tell "gone" from "would not go" would write the same retry loop
    ///     three times.
    /// </remarks>
    public async Task StopAsync(string id, TimeSpan patience, CancellationToken cancellation) {
        var seconds = Math.Max(0, (int)patience.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        using var response = await client
            .PostAsync(Url($"containers/{id}/stop?t={seconds}"), content: null, cancellation)
            .ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotModified) {
            return;
        }

        await Refuse(response, $"stop {id}", cancellation).ConfigureAwait(false);
    }

    /// <summary>Removes one.</summary>
    /// <param name="id">Which container.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>When removed, or when it was already gone.</returns>
    public async Task RemoveAsync(string id, CancellationToken cancellation) {
        using var response = await client
            .DeleteAsync(Url($"containers/{id}?force=true"), cancellation)
            .ConfigureAwait(false);

        if (response.StatusCode != System.Net.HttpStatusCode.NotFound) {
            await Refuse(response, $"remove {id}", cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Lists the containers carrying a label.</summary>
    /// <param name="label">The label, as <c>key=value</c> or just <c>key</c>.</param>
    /// <param name="cancellation">Gives up.</param>
    /// <returns>What the daemon has.</returns>
    /// <remarks>
    ///     The reconciliation half. An orchestrator that restarts has grain state saying which shards
    ///     should exist and no memory of which containers do — and a label is how a container says
    ///     whose it is.
    /// </remarks>
    public async Task<IReadOnlyList<DockerContainer>> ListAsync(string label, CancellationToken cancellation) {
        var filters = Uri.EscapeDataString($"{{\"label\":[\"{label}\"]}}");

        using var response = await client
            .GetAsync(Url($"containers/json?all=true&filters={filters}"), cancellation)
            .ConfigureAwait(false);

        await Refuse(response, "list containers", cancellation).ConfigureAwait(false);

        var listed = await response.Content
            .ReadFromJsonAsync<List<ListResponse>>(Json, cancellation)
            .ConfigureAwait(false);

        return
        [
            .. (listed ?? []).Select(entry => new DockerContainer(
                    entry.Id ?? "",
                    entry.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    entry.State ?? ""
                )
            )
        ];
    }

    /// <summary>Follows a container's output, a line at a time.</summary>
    /// <param name="id">Which container.</param>
    /// <param name="cancellation">Ends the stream.</param>
    /// <returns>Every line it writes, from now on.</returns>
    /// <remarks>
    ///     ⚠ <b>Read-only, and this backend needs nothing more.</b> <c>Placement.Process</c> writes to
    ///     a realm's stdin because doc 27 § Cost's L0 has no control plane to say "drain" over. A
    ///     Docker deployment is an L1 deployment by construction — it has an orchestrator, and a realm
    ///     learns to drain from the reply to its own heartbeat. Attaching a writable stream would mean
    ///     hijacking the connection to build a second way of saying something the cluster already
    ///     says.
    /// </remarks>
    public async IAsyncEnumerable<string> FollowAsync(
        string id,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation
    ) {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Url($"containers/{id}/logs?follow=true&stdout=true&stderr=true&tail=0")
        );

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) {
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);

        await foreach (var line in DockerFrames.ReadAsync(stream, cancellation).ConfigureAwait(false)) {
            yield return line;
        }
    }

    /// <summary>Waits for a container to exit.</summary>
    /// <param name="id">Which container.</param>
    /// <param name="cancellation">Stops waiting. Does not stop the container.</param>
    /// <returns>Its exit code, or zero if the daemon would not say.</returns>
    public async Task<int> WaitAsync(string id, CancellationToken cancellation) {
        using var response = await client
            .PostAsync(Url($"containers/{id}/wait"), content: null, cancellation)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) {
            return 0;
        }

        var waited = await response.Content
            .ReadFromJsonAsync<WaitResponse>(Json, cancellation)
            .ConfigureAwait(false);

        return waited?.StatusCode ?? 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        if (ownsClient) {
            client.Dispose();
        }
    }

    static string Url(string path) => $"/{ApiVersion}/{path}";

    static (string Name, string Tag) Reference(string image) {
        // A digest is asked for whole: `fromImage=repo@sha256:…` with no tag beside it.
        if (image.Contains('@', StringComparison.Ordinal)) {
            return (image, "");
        }

        var colon = image.LastIndexOf(':');

        // A colon before the last slash is a registry's port, not a tag. And an untagged image must
        // still be asked for by one, because the Engine reads an empty `tag` as "every tag there is".
        return colon > 0 && image.IndexOf('/', colon) < 0
            ? (image[..colon], image[(colon + 1)..])
            : (image, "latest");
    }

    static async Task Refuse(HttpResponseMessage response, string what, CancellationToken cancellation) {
        if (response.IsSuccessStatusCode) {
            return;
        }

        // The daemon's own message, because it is always more useful than the status code: "no such
        // image" and "port is already allocated" are the two an operator actually hits.
        var said = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);

        throw new InvalidOperationException(
            $"Docker would not {what}: {(int)response.StatusCode} {said.Trim()}"
        );
    }

    static HttpClient Connect(string? endpoint) {
        var handler = new SocketsHttpHandler { ConnectCallback = (_, cancellation) => Dial(endpoint, cancellation) };

        return new(handler) { BaseAddress = new("http://docker/") };
    }

    static async ValueTask<Stream> Dial(string? endpoint, CancellationToken cancellation) {
        var target = endpoint ?? (OperatingSystem.IsWindows() ? DefaultPipe : DefaultSocket);

        if (target.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) || target.StartsWith(@"\\.\pipe\", StringComparison.Ordinal)) {
            var name = target.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase)
                ? target["npipe://".Length..].TrimStart('.', '/', '\\').Replace("pipe/", "", StringComparison.Ordinal)
                : target[@"\\.\pipe\".Length..];

            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(cancellation).ConfigureAwait(false);

            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        await socket.ConnectAsync(new UnixDomainSocketEndPoint(target), cancellation).ConfigureAwait(false);

        return new NetworkStream(socket, ownsSocket: true);
    }

    sealed class VersionResponse {
        public string? Version { get; set; }

        public string? Os { get; set; }
    }

    sealed class CreateRequest {
        public string? Image { get; set; }

        public List<string>? Entrypoint { get; set; }

        public List<string>? Cmd { get; set; }

        public Dictionary<string, string>? Labels { get; set; }

        public List<string>? Env { get; set; }

        public bool Tty { get; set; }

        public Dictionary<string, object>? ExposedPorts { get; set; }

        public HostConfigRequest? HostConfig { get; set; }
    }

    sealed class HostConfigRequest {
        public Dictionary<string, List<PortBinding>>? PortBindings { get; set; }

        public RestartPolicyRequest? RestartPolicy { get; set; }
    }

    sealed class PortBinding {
        public string? HostPort { get; set; }
    }

    sealed class RestartPolicyRequest {
        public string? Name { get; set; }
    }

    sealed class CreateResponse {
        public string? Id { get; set; }
    }

    sealed class ListResponse {
        public string? Id { get; set; }

        public Dictionary<string, string>? Labels { get; set; }

        public string? State { get; set; }
    }

    sealed class WaitResponse {
        public int StatusCode { get; set; }
    }

    sealed class InspectResponse {
        public InspectConfig? Config { get; set; }
    }

    sealed class InspectConfig {
        public List<string>? Entrypoint { get; set; }
    }
}
