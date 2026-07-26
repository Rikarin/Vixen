// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.IO.Tests;

public sealed class PhysicalFileProviderTests : IDisposable {
    readonly string directory = Path.Combine(Path.GetTempPath(), "vixen-io-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheCaseCheckIsOnlyEnabledWhereTheVolumeNeedsIt() {
        var provider = new PhysicalFileProvider(directory);

        // Probed, not assumed from the OS: an APFS volume can be formatted either way, and a
        // case-sensitive volume can be mounted on Windows. Whatever the probe decided, the
        // observable behaviour has to be the same, which is what the second half asserts.
        File.WriteAllText(Path.Combine(directory, "Texture.png"), "x");

        Assert.False(provider.Exists(new("/texture.png")));
        Assert.True(provider.Exists(new("/Texture.png")));
    }

    [Fact]
    public void TheCaseCheckCanBeTurnedOff() {
        var provider = new PhysicalFileProvider(directory, enforceCaseSensitivity: false);
        File.WriteAllText(Path.Combine(directory, "Texture.png"), "x");

        Assert.Equal(!OperatingSystem.IsLinux(), provider.Exists(new("/texture.png")));
        Assert.False(provider.EnforcesCaseSensitivity);
    }

    [Fact]
    public void AReadOnlyProviderRefusesEveryMutation() {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "x");
        var provider = new PhysicalFileProvider(directory, isReadOnly: true);

        Assert.Equal("x", ReadAll(provider, new("/a.txt")));
        Assert.Throws<NotSupportedException>(() => provider.OpenWrite(new("/b.txt")));
        Assert.Throws<NotSupportedException>(() => provider.Delete(new("/a.txt")));
        Assert.Throws<NotSupportedException>(() => provider.CreateDirectory(new("/d")));
    }

    [Fact]
    public void MappingReadsTheFileWithoutCopyingIt() {
        var provider = new PhysicalFileProvider(directory);
        var contents = new byte[8192];

        for (var index = 0; index < contents.Length; index++) {
            contents[index] = (byte)index;
        }

        File.WriteAllBytes(Path.Combine(directory, "a.bin"), contents);

        Assert.True(provider.TryMap(new("/a.bin"), out var mapped));

        using (mapped) {
            Assert.Equal(contents.Length, mapped.Memory.Length);
            Assert.True(mapped.Memory.Span.SequenceEqual(contents));
        }
    }

    [Fact]
    public void MappingDeclinesWhatItCannotMap() {
        var provider = new PhysicalFileProvider(directory);
        File.WriteAllBytes(Path.Combine(directory, "empty.bin"), []);

        // Not a failure — an empty file has nothing to map and a missing one has nothing to map
        // either. Callers fall back to a stream, which is the documented answer.
        Assert.False(provider.TryMap(new("/empty.bin"), out _));
        Assert.False(provider.TryMap(new("/missing.bin"), out _));
    }

    [Fact]
    public void NormalisationIsWhatKeepsAPathInsideTheRoot() {
        var provider = new PhysicalFileProvider(directory);
        File.WriteAllText(Path.Combine(directory, "inside.txt"), "x");

        // `..` never reaches the provider: VirtualPath resolves it, and resolving past the root is
        // rejected at construction. This is the guard, and it is at the type rather than in every
        // provider that would otherwise have to remember it.
        Assert.Throws<ArgumentException>(() => new VirtualPath("/../outside.txt"));
        Assert.Equal("/inside.txt", (new VirtualPath("/sub") / "../inside.txt").Value);
        Assert.True(provider.Exists(new VirtualPath("/sub") / "../inside.txt"));
    }

    [Fact]
    public void TheRootIsCreatedForAWritableProviderAndNotForAReadOnlyOne() {
        var writable = Path.Combine(directory, "writable");
        var missing = Path.Combine(directory, "missing");

        _ = new PhysicalFileProvider(writable);
        Assert.True(Directory.Exists(writable));

        _ = new PhysicalFileProvider(missing, isReadOnly: true);
        Assert.False(Directory.Exists(missing));
    }

    static string ReadAll(IFileProvider provider, VirtualPath path) {
        using var stream = provider.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
