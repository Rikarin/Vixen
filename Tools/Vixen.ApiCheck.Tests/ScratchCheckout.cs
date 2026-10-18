// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>A temporary directory that git calls a repository, for rules that read the tree through <c>RepositoryFiles</c>.</summary>
/// <remarks>
///     ⚠ <b>A git repository and not just a folder</b>, because the rules it feeds list files the way
///     every sweep here does since #1424 — <c>git ls-files --others --exclude-standard</c> — and a
///     plain temp folder is "not a git repository", which <c>RepositoryFiles</c> rightly refuses
///     rather than reporting it clean. Nothing is committed: untracked files are listed.
/// </remarks>
sealed class ScratchCheckout : IDisposable {
    /// <summary>Creates the directory and runs <c>git init</c> in it.</summary>
    /// <param name="prefix">A name for the temp folder.</param>
    public ScratchCheckout(string prefix) {
        Root = Directory.CreateTempSubdirectory(prefix).FullName;

        var start = new ProcessStartInfo("git") {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        start.ArgumentList.Add("init");
        start.ArgumentList.Add("--quiet");

        using var git = Process.Start(start)!;
        var error = git.StandardError.ReadToEndAsync();
        git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        Assert.True(git.ExitCode == 0, $"git init exited {git.ExitCode}: {error.Result}");
    }

    /// <summary>The checkout's root.</summary>
    public string Root { get; }

    /// <summary>Writes a file, creating its directory.</summary>
    /// <param name="relative">The path, <c>/</c>-separated.</param>
    /// <param name="text">What it holds.</param>
    public void Write(string relative, string text) {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <inheritdoc />
    public void Dispose() {
        // git marks its object files read-only, which Directory.Delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}
