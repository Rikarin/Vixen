// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform.Linux;

/// <summary>Reading what the kernel publishes, and parsing the shapes it publishes it in.</summary>
/// <remarks>
///     <para>
///         Deliberately <em>not</em> <c>[SupportedOSPlatform("linux")]</c>, and it is the only type
///         in this assembly that is not. Everything here is <see cref="File" /> and
///         <see cref="string" />: reading a path that does not exist returns nothing on every
///         operating system, and a processor list parses the same way on all of them. Keeping the
///         annotation off is what lets the parsing be tested on the machine the developer is
///         actually sitting at, which for this repository is as often a Mac as not.
///     </para>
///     <para>
///         Every failure is expected. The file is absent on an older kernel, unreadable in a
///         container, empty on a virtual machine, or a directory the process has no business in.
///         None of them is worth an exception, and the caller's answer to all of them is the same.
///     </para>
/// </remarks>
static class Sysfs {
    /// <summary>Reads a file's text, trimmed, or nothing.</summary>
    public static string? ReadText(string path) {
        try {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Reads a file's single integer, or nothing.</summary>
    public static int? ReadInteger(string path) => int.TryParse(ReadText(path), out var value) ? value : null;

    /// <summary>Enumerates a directory's subdirectories, or nothing.</summary>
    public static IEnumerable<string> Directories(string path) {
        try {
            return Directory.EnumerateDirectories(path);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>Reads sysfs's <c>0-7,16-23</c> processor-list syntax.</summary>
    /// <remarks>
    ///     The ranges are inclusive at both ends. Reading one as exclusive loses the last
    ///     performance core on every machine that has two kinds, which is a mistake that costs a few
    ///     per cent of a frame and reports nothing.
    /// </remarks>
    public static List<int> ParseCpuList(string? text) {
        var indices = new List<int>();

        if (string.IsNullOrWhiteSpace(text)) {
            return indices;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var dash = part.IndexOf('-', StringComparison.Ordinal);

            if (dash < 0) {
                if (int.TryParse(part, out var single)) {
                    indices.Add(single);
                }

                continue;
            }

            if (int.TryParse(part[..dash], out var first) && int.TryParse(part[(dash + 1)..], out var last)) {
                for (var index = first; index <= last; index++) {
                    indices.Add(index);
                }
            }
        }

        return indices;
    }
}
