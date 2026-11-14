// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using Vixen.Testing;
using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     A host's save never fails because Vixen is reading the file. The framework's readers share reading only, and on
///     Windows a writer that meets one is refused; Patina's saves of pictures, graphs and shader caches could fail while
///     Vixen read them (Patina #277).
/// </summary>
public sealed partial class SharedReadTests : IDisposable {
    /// <summary>
    ///     The libraries a host reads its users' files through: documents, pictures, meshes, shaders and their caches.
    ///     The editor, the samples and the build tools read only files they own.
    /// </summary>
    static readonly string[] Libraries = [
        "Core/Vixen.Core", "Core/Vixen.Core.IO", "Core/Vixen.Core.Imaging", "Core/Vixen.Core.Serialization",
        "Core/Vixen.Core.Yaml", "Core/Vixen.Geometry", "Core/Vixen.Geometry.Remeshing", "Core/Vixen.Geometry.Uv",
        "Core/Vixen.Shaders", "Core/Vixen.Terrain", "Platform/Vixen.Graphics.Vulkan", "Tools/Vixen.Authoring",
        "Tools/Vixen.Interchange", "Tools/Vixen.NodeGraph", "Tools/Vixen.ShaderCompiler", "Tools/Vixen.TextureGraph",
        "Tools/Vixen.TextureGraph.Drawing", "Tools/Vixen.Texturing"
    ];

    /// <summary>
    ///     Reads that share less on purpose. ⚠ A memory-mapped file read past the end its writer truncated it to faults
    ///     the process, so a mapping keeps writers out.
    /// </summary>
    static readonly string[] Exempt =
        ["Core/Vixen.Core.IO/MemoryMappedFileMapping.cs", "Core/Vixen.Core/SharedRead.cs"];

    readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("vixen-shared-read-");

    public void Dispose() => folder.Delete(true);

    /// <summary>
    ///     A file being read can be written over and deleted. ⚠ Only Windows makes this mean something; Linux and macOS
    ///     never enforce a share, and the test passes there whatever the helper does.
    /// </summary>
    [Fact]
    public void A_file_being_read_can_be_written_over_and_deleted() {
        var file = Path.Combine(folder.FullName, "slope.vxtexgraph");
        File.WriteAllText(file, "name: Slope");

        using (var reading = SharedRead.Open(file)) {
            File.WriteAllText(file, "name: Ramp");
            Assert.Equal((byte)'n', reading.ReadByte());
        }

        Assert.Equal("name: Ramp", SharedRead.Text(file));

        using (SharedRead.Open(file)) {
            File.Delete(file);
        }

        Assert.False(File.Exists(file));
    }

    /// <summary>The framework's reader is what refuses the writer, so the test above is not passing by accident.</summary>
    [Fact]
    public void The_framework_reader_refuses_a_writer() {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows enforces a share.");
        var file = Path.Combine(folder.FullName, "slope.vxtexgraph");
        File.WriteAllText(file, "name: Slope");

        using (File.OpenRead(file)) {
            Assert.Throws<IOException>(() => File.WriteAllText(file, "name: Ramp"));
        }
    }

    /// <summary>The readers give back what the framework's do, byte order mark and all.</summary>
    [Fact]
    public void The_readers_read_what_the_framework_reads() {
        var file = Path.Combine(folder.FullName, "notes.txt");
        File.WriteAllText(file, "één\ntwee\n", new UTF8Encoding(true));

        Assert.Equal(File.ReadAllBytes(file), SharedRead.Bytes(file));
        Assert.Equal(File.ReadAllText(file), SharedRead.Text(file));
        Assert.Equal(FileShare.ReadWrite | FileShare.Delete, SharedRead.Share);
        Assert.Throws<FileNotFoundException>(() => SharedRead.Bytes(Path.Combine(folder.FullName, "gone")));
    }

    /// <summary>
    ///     Nothing in the libraries a host reads its users' files through reads one the framework's way, or opens one
    ///     sharing reading alone.
    /// </summary>
    [Fact]
    public void No_library_reads_a_file_without_sharing_it() {
        var found = new List<string>();
        var read = 0;

        foreach (var library in Libraries) {
            var sources = RepositoryFiles.Files(Path.Combine(RepositoryFiles.Root, library), "*.cs", "*.vxml");
            read += sources.Count;

            foreach (var file in sources.Where(Production)) {
                var lines = File.ReadAllLines(file);

                for (var line = 0; line < lines.Length; line++) {
                    if (UnsharedRead().IsMatch(lines[line])) {
                        found.Add($"{Relative(file)}:{line + 1}: {lines[line].Trim()}");
                    }
                }
            }
        }

        Assert.True(read > 300, $"Read only {read} source files, which is not these libraries.");
        Assert.True(found.Count == 0, "Read through SharedRead instead:\n" + string.Join('\n', found));
    }

    /// <summary>The instrument: the pattern sees the readers that refuse a writer, and not those that let one in.</summary>
    [Theory]
    [InlineData("bytes = File.ReadAllBytes(path);", true)]
    [InlineData("return Read(System.IO.File.ReadAllText(path), chain);", true)]
    [InlineData("using var file = File.OpenRead(path);", true)]
    [InlineData("foreach (var line in File.ReadLines(path)) {", true)]
    [InlineData("new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)", true)]
    [InlineData("Share = FileShare.Read,", true)]
    [InlineData("new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)", false)]
    [InlineData("bytes = SharedRead.Bytes(path);", false)]
    [InlineData("File.WriteAllText(path, text);", false)]
    public void The_sweep_sees_an_unshared_read(string line, bool unshared) =>
        Assert.Equal(unshared, UnsharedRead().IsMatch(line));

    static string Relative(string file) => Path.GetRelativePath(RepositoryFiles.Root, file).Replace('\\', '/');

    /// <summary>Code that ships, not a test standing in for another program, and not a read that shares less on purpose.</summary>
    static bool Production(string file) {
        var relative = Relative(file);

        return !relative.Split('/').Any(static part => part.EndsWith(".Tests", StringComparison.Ordinal))
            && !relative.Contains("/obj/", StringComparison.Ordinal)
            && !relative.Contains("/bin/", StringComparison.Ordinal)
            && !Exempt.Contains(relative);
    }

    /// <summary>The framework's whole-file readers and its open for reading, and a share of reading alone.</summary>
    [GeneratedRegex(
        @"(?<![\w.])(System\.IO\.)?File\.(ReadAll(Text|Bytes|Lines)(Async)?|ReadLines(Async)?|OpenRead|OpenText)\s*\("
        + @"|FileShare\.Read\s*[,)]"
    )]
    private static partial Regex UnsharedRead();
}
