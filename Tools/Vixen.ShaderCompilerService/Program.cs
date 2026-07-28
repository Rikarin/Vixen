// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using Vixen.ShaderCompiler;
using Vixen.ShaderCompilerService;
using Vixen.Shaders;

var roots = new List<string>();
var references = new List<string>();
var port = 9930;
var address = IPAddress.Loopback;
var target = "spirv";
var cache = string.Empty;

for (var index = 0; index < args.Length; index++) {
    switch (args[index]) {
        case "--port" or "-p" when index + 1 < args.Length:
            if (!int.TryParse(args[++index], CultureInfo.InvariantCulture, out port)) {
                Console.Error.WriteLine($"'{args[index]}' is not a port number.");
                return 2;
            }

            break;

        case "--target" or "-t" when index + 1 < args.Length:
            target = args[++index];
            break;

        case "--reference" or "-r" when index + 1 < args.Length:
            references.Add(Path.GetFullPath(args[++index]));
            break;

        case "--cache" or "-c" when index + 1 < args.Length:
            cache = Path.GetFullPath(args[++index]);
            break;

        case "--any":
            // Every interface, which is what a phone on the same wifi needs and what a laptop in a
            // café does not. Off by default for that reason.
            address = IPAddress.Any;
            break;

        case "--help" or "-h":
            Usage();
            return 0;

        default:
            if (args[index].StartsWith('-')) {
                Console.Error.WriteLine($"Unrecognised argument '{args[index]}'.");
                Usage();

                return 2;
            }

            roots.Add(Path.GetFullPath(args[index]));
            break;
    }
}

if (roots.Count == 0) {
    Console.Error.WriteLine("Name at least one directory or .rvn file to serve shaders from.");
    Usage();

    return 2;
}

var sources = new List<string>();

foreach (var root in roots) {
    if (Directory.Exists(root)) {
        sources.AddRange(Directory.EnumerateFiles(root, "*.rvn", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        continue;
    }

    if (!File.Exists(root)) {
        Console.Error.WriteLine($"There is nothing at '{root}'.");
        return 2;
    }

    sources.Add(root);
}

if (sources.Count == 0) {
    Console.Error.WriteLine("Those directories hold no .rvn files.");
    return 2;
}

Console.WriteLine($"{sources.Count} shader source{(sources.Count == 1 ? "" : "s")}, default target {target}.");

using var server = new ShaderCompilerServer(address, port, Open) { Log = Console.Out };
server.Start();

Console.WriteLine($"Point a RemoteEffectSource at this machine on port {server.Port}. Ctrl-C to stop.");

using var stopping = new CancellationTokenSource();

Console.CancelKeyPress += (_, cancel) => {
    cancel.Cancel = true;
    stopping.Cancel();
};

try {
    await Task.Delay(Timeout.Infinite, stopping.Token);
} catch (OperationCanceledException) {
    // Ctrl-C, which is how this is meant to end.
}

Console.WriteLine($"Stopped after {server.Served} served and {server.Missed} missed.");

return 0;

// A source per target, opened the first time a device asks for that target. Parsing every shader in
// the project is what opening one costs, so this is deliberately lazy: a laptop serving one phone
// should not pay for the four backends nobody asked for.
IEffectSource? Open(string requested) {
    var name = string.IsNullOrEmpty(requested) ? target : requested;

    RavenEffectCompiler compiler;

    try {
        compiler = new(sources, name, references);
    } catch (ArgumentException exception) {
        Console.Error.WriteLine(exception.Message);
        return null;
    }

    if (cache.Length == 0) {
        return compiler;
    }

    // The cache in front of the compiler, so five devices asking for one variant is one compilation
    // and a restart of this process costs nothing. `Expect` is set because this side does know what
    // the sources hash to — editing a shader has to invalidate exactly its variants.
    return new EffectDiskCache(Path.Combine(cache, name), name, compiler) { Expect = compiler.SourceHash };
}

static void Usage() {
    Console.WriteLine("""
        vixen-shader-compiler — compiles shader variants for a device that has no compiler.

          <path>...             directories to search for .rvn files, or the files themselves
          --target, -t <name>   default backend: spirv or glsl (default: spirv)
          --reference, -r <lib> a compiled .rvnlib to bind against; repeatable
          --cache, -c <dir>     keep compiled variants here, so a restart costs nothing
          --port, -p <n>        what to listen on (default: 9930)
          --any                 bind every interface rather than loopback, for a device on the same network
          --help, -h            this

        A development tool: no TLS, no authentication, no access control. Anything that reaches the
        port gets whatever this can compile. Keep it behind a firewall.
        """);
}
