// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.ContentServer;
using Vixen.Core.IO;

var directory = Environment.CurrentDirectory;
var port = 8080;
var host = "localhost";

for (var index = 0; index < args.Length; index++) {
    switch (args[index]) {
        case "--root" or "-r" when index + 1 < args.Length:
            directory = Path.GetFullPath(args[++index]);
            break;

        case "--port" or "-p" when index + 1 < args.Length:
            if (!int.TryParse(args[++index], CultureInfo.InvariantCulture, out port)) {
                Console.Error.WriteLine($"'{args[index]}' is not a port number.");
                return 2;
            }

            break;

        case "--any":
            // Every interface, which is what a phone on the same wifi needs and what a laptop in a
            // café does not. Off by default for that reason.
            host = "+";
            break;

        case "--help" or "-h":
            Usage();
            return 0;

        default:
            Console.Error.WriteLine($"Unrecognised argument '{args[index]}'.");
            Usage();

            return 2;
    }
}

if (!Directory.Exists(directory)) {
    Console.Error.WriteLine($"There is no directory at '{directory}'.");
    return 2;
}

var files = new VirtualFileSystem();
files.Mount(new("/content"), new PhysicalFileProvider(directory));

var server = new ContentServer(files, new("/content"));
using var listener = new ContentServerHost(server, port, host) { Log = Console.WriteLine };

Console.WriteLine($"Serving {directory} at {listener.Prefix}");
Console.WriteLine("Point a catalog URL at <prefix>catalog.bin. Ctrl-C to stop.");

using var stopping = new CancellationTokenSource();

Console.CancelKeyPress += (_, cancel) => {
    cancel.Cancel = true;
    stopping.Cancel();
};

try {
    await listener.RunAsync(stopping.Token);
} catch (OperationCanceledException) {
    // Ctrl-C, which is how this is meant to end.
}

Console.WriteLine($"Stopped after {server.Served} requests.");

return 0;

static void Usage() {
    Console.WriteLine("""
        vixen-content-server — serves a content build directory over HTTP.

          --root, -r <dir>   what to serve (default: the working directory)
          --port, -p <n>     what to listen on (default: 8080)
          --any              bind every interface rather than localhost, for a device on the same network
          --help, -h         this

        A development tool: no TLS, no authentication, no access control. Do not put it in front of players.
        """);
}
