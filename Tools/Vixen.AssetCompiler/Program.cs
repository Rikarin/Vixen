// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Pipes;
using Vixen.AssetCompiler;

// A worker, started by a coordinator and never by a person. It connects back to the pipe it was
// given, answers import requests until that pipe closes, and exits.
//
// Nothing is written to stdout on the happy path. A worker's output is interleaved with every other
// worker's and with the coordinator's, so anything it printed would arrive as noise in the middle of
// a build log; what it has to say about an asset goes back over the pipe as a diagnostic against
// that asset. Failures to *start* are the exception, because there is nowhere else for those to go.
if (!WorkerHost.TryParse(args, out var pipe, out var root)) {
    await Console.Error.WriteLineAsync(
        "vixen-asset-compiler --pipe <name> --root <project>\n\n"
        + "An import worker. It is started by whatever is coordinating a content build; running it by "
        + "hand does nothing useful."
    );

    return 2;
}

if (!Directory.Exists(root)) {
    await Console.Error.WriteLineAsync($"There is no project directory at '{root}'.");
    return 2;
}

using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);

try {
    // Bounded, so a worker whose coordinator died between spawning it and connecting does not sit
    // forever holding a process slot. Ten seconds is far longer than a connect takes and far shorter
    // than anybody would wait before noticing.
    await client.ConnectAsync(10_000, CancellationToken.None);
} catch (TimeoutException) {
    await Console.Error.WriteLineAsync($"Nothing was listening on '{pipe}'.");
    return 2;
}

await new WorkerHost(root).ServeAsync(client);
return 0;
