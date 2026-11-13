// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Testing;
using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     A save never fails because somebody is reading the file it replaces. On Windows a rename onto an open file is
///     refused whatever the reader shares, and Patina's library met it in every Vixen save it calls (Patina #265).
/// </summary>
public sealed partial class PatientMoveTests : IDisposable {
    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("vixen-patient-move-");

    public void Dispose() => folder.Delete(true);

    /// <summary>
    ///     A file held open by a reader can be replaced by a finished one moved over it. ⚠ Only Windows makes this mean
    ///     something; on Linux and macOS the first try succeeds and the test passes whatever the helper does.
    /// </summary>
    [Fact]
    public void A_file_being_read_can_be_moved_over() {
        var file = Path.Combine(folder.FullName, "slope.vxtexgraph");
        var part = file + ".tmp";
        File.WriteAllText(file, "name: Slope");
        File.WriteAllText(part, "name: Ramp");
        var held = Hold(file, TimeSpan.FromMilliseconds(150));

        PatientMove.Over(part, file);

        held.Join();
        Assert.Equal("name: Ramp", File.ReadAllText(file));
        Assert.False(File.Exists(part));
    }

    /// <summary>The framework's own move is what fails there, so the test above is not passing by accident.</summary>
    [Fact]
    public void The_framework_move_over_a_file_being_read_is_refused() {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a rename onto an open file.");
        var file = Path.Combine(folder.FullName, "slope.vxtexgraph");
        var part = file + ".tmp";
        File.WriteAllText(file, "name: Slope");
        File.WriteAllText(part, "name: Ramp");

        using (Open(file)) {
            Assert.Throws<UnauthorizedAccessException>(() => File.Move(part, file, true));
        }
    }

    /// <summary>A move over a file held for longer than the wait fails, rather than never returning.</summary>
    [Fact]
    public void A_move_over_a_file_held_too_long_gives_up() {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a rename onto an open file.");
        var file = Path.Combine(folder.FullName, "slope.vxtexgraph");
        var part = file + ".tmp";
        File.WriteAllText(file, "name: Slope");
        File.WriteAllText(part, "name: Ramp");

        using (Open(file)) {
            Assert.Throws<UnauthorizedAccessException>(() => PatientMove.Over(part, file));
        }

        Assert.Equal("name: Slope", File.ReadAllText(file));
        Assert.True(File.Exists(part));
    }

    /// <summary>A move whose file is gone fails at once: waiting cannot bring it back.</summary>
    [Fact]
    public void A_move_of_a_file_that_is_not_there_fails_at_once() {
        var file = Path.Combine(folder.FullName, "slope.vxtexgraph");

        Assert.Throws<FileNotFoundException>(() => PatientMove.Over(file + ".tmp", file));
    }

    /// <summary>
    ///     Nothing outside a test moves a file over another but <see cref="PatientMove.Over" />. A move told to replace
    ///     what is there fails on Windows the moment anybody reads that file, which every save did.
    /// </summary>
    [Fact]
    public void No_source_moves_over_a_file_without_waiting_for_its_readers() {
        var sources = RepositoryFiles.Files(RepositoryFiles.Root, "*.cs", "*.vxml");
        var found = new List<string>();

        foreach (var file in sources.Where(Production)) {
            var lines = File.ReadAllLines(file);

            for (var line = 0; line < lines.Length; line++) {
                if (OverwritingMove().IsMatch(lines[line])) {
                    found.Add($"{Path.GetRelativePath(RepositoryFiles.Root, file)}:{line + 1}: {lines[line].Trim()}");
                }
            }
        }

        Assert.True(sources.Count > 1000, $"Read only {sources.Count} source files, which is not this repository.");
        Assert.True(found.Count == 0, "Move through PatientMove.Over instead:\n" + string.Join('\n', found));
    }

    /// <summary>The instrument: the pattern sees the move every save used to make, and not one that never replaces.</summary>
    [Theory]
    [InlineData("        File.Move(temporary, AssetPath, true);", true)]
    [InlineData("File.Move(temporary, Path.Combine(a, b), overwrite: true);", true)]
    [InlineData("System.IO.File.Move(meta, destination, true);", true)]
    [InlineData("File.Move(staging, path, false);", false)]
    [InlineData("File.Move(staging, Path.Combine(a, b), false);", false)]
    [InlineData("File.Move(Path.Combine(a, b), Path.Combine(c, d));", false)]
    [InlineData("File.Move(from, to);", false)]
    [InlineData("PatientMove.Over(temporary, path);", false)]
    public void The_sweep_sees_an_overwriting_move(string line, bool overwriting) =>
        Assert.Equal(overwriting, OverwritingMove().IsMatch(line));

    /// <summary>Code that ships: not a test, which may well stand in for an outside program, and not the helper.</summary>
    static bool Production(string file) {
        var relative = Path.GetRelativePath(RepositoryFiles.Root, file).Replace('\\', '/');

        return !relative.Split('/').Any(static part => part.EndsWith(".Tests", StringComparison.Ordinal))
            && !relative.StartsWith("Testing/", StringComparison.Ordinal)
            && !relative.EndsWith("Vixen.Core/PatientMove.cs", StringComparison.Ordinal);
    }

    /// <summary>The framework's move with a third argument that is not <c>false</c>: the overwrite.</summary>
    [GeneratedRegex(
        @"(?<![\w.])(System\.IO\.)?File\.Move\s*\(([^,()]|\([^()]*\))+,([^,()]|\([^()]*\))+,(?!\s*false\s*\))"
    )]
    private static partial Regex OverwritingMove();

    static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    ///     A read held open for a while and let go by a thread of its own, which a busy run cannot starve the way it can
    ///     the pool. The file is open before this returns, so the caller's next step meets the read.
    /// </summary>
    static Thread Hold(string path, TimeSpan duration) {
        var reading = Open(path);
        Thread release = new(() => {
                Thread.Sleep(duration);
                reading.Dispose();
            }
        );
        release.Start();
        return release;
    }
}
