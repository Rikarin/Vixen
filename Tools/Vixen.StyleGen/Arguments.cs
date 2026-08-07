// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.StyleGen;

/// <summary>The command line, which is written by a <c>.targets</c> rather than by a person.</summary>
/// <remarks>
///     <para>
///         Hand-parsed rather than <c>System.CommandLine</c>, and the reason is the response file
///         below rather than a dislike of the library: the whole point of this argument surface is
///         that MSBuild writes it, and MSBuild writes it long. A project with four hundred source
///         files gives four hundred <c>--scan</c> paths, which is past the command-line limit on
///         Windows and near it everywhere else — so the real interface is <c>@file</c>, and the flat
///         parse that supports it is thirty lines.
///     </para>
///     <para>
///         ⚠ <b>An unknown option is an error rather than something to ignore.</b> This surface is
///         consumed by a file nobody reads while it is working, so an option renamed on one side and
///         not the other has to fail loudly — the alternative is a build that silently stops scanning
///         and a stylesheet that silently loses half its rules.
///     </para>
/// </remarks>
internal static class Arguments {
    /// <summary>Reads a command line, expanding any <c>@response-file</c> in it.</summary>
    /// <param name="args">The arguments as given.</param>
    /// <param name="error">Receives why it could not be read.</param>
    /// <returns>The request, or <c>null</c>.</returns>
    internal static StyleGenRequest? Parse(IReadOnlyList<string> args, out string? error) {
        ArgumentNullException.ThrowIfNull(args);

        error = null;

        var flat = new List<string>();

        foreach (var argument in args) {
            if (argument.StartsWith('@')) {
                var path = argument[1..];

                if (!File.Exists(path)) {
                    error = $"{path}: there is no response file here";
                    return null;
                }

                // One argument per line, so a path with a space in it needs no quoting — which is
                // the other half of why the response file exists at all.
                flat.AddRange(File.ReadAllLines(path).Where(line => line.Length > 0));
                continue;
            }

            flat.Add(argument);
        }

        var request = new StyleGenRequest();
        var scan = new List<string>();
        var bases = new List<string>();
        var safelist = new List<string>();

        for (var index = 0; index < flat.Count; index++) {
            var option = flat[index];

            switch (option) {
                case "--public":
                    request = request with { Public = true };
                    continue;

                case "--tokens" or "--scan" or "--base" or "--safelist" or "--output"
                    or "--accessor" or "--namespace" or "--class" or "--report":
                    break;

                default:
                    error = $"'{option}' is not an option this step has";
                    return null;
            }

            if (++index >= flat.Count) {
                error = $"'{option}' was given nothing to be";
                return null;
            }

            var value = flat[index];

            request = option switch {
                "--tokens" => request with { Tokens = value },
                "--output" => request with { Output = value },
                "--accessor" => request with { Accessor = value },
                "--namespace" => request with { Namespace = value },
                "--class" => request with { Class = value },
                "--report" => request with { Report = value },
                _ => request
            };

            switch (option) {
                case "--scan": scan.Add(value); break;
                case "--base": bases.Add(value); break;
                case "--safelist": safelist.Add(value); break;
                default: break;
            }
        }

        return request with { Scan = scan, Base = bases, Safelist = safelist };
    }
}
