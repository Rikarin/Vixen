// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization.Storage;
using Xunit;

namespace Vixen.ContentServer.Tests;

/// <summary>
///     Everything the server decides — what exists, what a range means, what is inside the root — with
///     no socket anywhere near it. Doc 12 § testing rules out real network, and the way to obey that
///     without leaving the interesting half unchecked is to keep the interesting half out of the
///     listener.
/// </summary>
public sealed class ContentServerTests {
    /// <summary>The ordinary case: a bundle goes out whole.</summary>
    [Fact]
    public async Task AFileIsServedWhole() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 4096);

        using var reply = world.Server.Serve("/pack.bundle");

        Assert.Equal(ContentStatus.Ok, reply.Status);
        Assert.Equal(4096, reply.Length);
        Assert.Equal(4096, reply.Total);
        Assert.Equal("application/octet-stream", reply.ContentType);
        Assert.Null(reply.ContentRange());
        Assert.Equal(world.Contents("pack.bundle"), await ServedDirectory.Read(reply));
    }

    /// <summary>Nothing at that path is a 404 with nothing attached to it.</summary>
    [Fact]
    public void SomethingThatIsNotThereIsNotFound() {
        var world = new ServedDirectory();

        using var reply = world.Server.Serve("/missing.bundle");

        Assert.Equal(ContentStatus.NotFound, reply.Status);
        Assert.Null(reply.Body);
    }

    /// <summary>A directory is not a file, and serving its listing is not this tool's job.</summary>
    [Fact]
    public void ADirectoryIsNotServed() {
        var world = new ServedDirectory();
        world.Put("packs/one.bundle", 16);

        using var reply = world.Server.Serve("/packs");

        Assert.Equal(ContentStatus.NotFound, reply.Status);
    }

    /// <summary>
    ///     The feature the whole tool exists for. <c>bytes=N-</c> is what a resumed download sends,
    ///     and the reply has to say where in the resource its body starts or the client assembles the
    ///     file out of the wrong bytes.
    /// </summary>
    [Fact]
    public async Task AnOpenEndedRangeIsAnsweredFromWhereItAsked() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 4096);

        using var reply = world.Server.Serve("/pack.bundle", "bytes=1000-");

        Assert.Equal(ContentStatus.PartialContent, reply.Status);
        Assert.Equal(1000, reply.Offset);
        Assert.Equal(3096, reply.Length);
        Assert.Equal("bytes 1000-4095/4096", reply.ContentRange());
        Assert.Equal(world.Contents("pack.bundle")[1000..], await ServedDirectory.Read(reply));
    }

    /// <summary>A closed range gives exactly that window and not a byte more.</summary>
    [Fact]
    public async Task AClosedRangeGivesExactlyThatWindow() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 4096);

        using var reply = world.Server.Serve("/pack.bundle", "bytes=1000-1999");

        Assert.Equal(1000, reply.Offset);
        Assert.Equal(1000, reply.Length);
        Assert.Equal("bytes 1000-1999/4096", reply.ContentRange());

        // The body stream can read past the end of the window, so this is the assertion that catches
        // a server copying to the end of the file under a Content-Length saying otherwise.
        Assert.Equal(world.Contents("pack.bundle")[1000..2000], await ServedDirectory.Read(reply));
    }

    /// <summary>The suffix form asks for the last N bytes, which is a real thing clients send.</summary>
    [Fact]
    public async Task ASuffixRangeGivesTheLastBytes() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 4096);

        using var reply = world.Server.Serve("/pack.bundle", "bytes=-100");

        Assert.Equal(3996, reply.Offset);
        Assert.Equal(100, reply.Length);
        Assert.Equal(world.Contents("pack.bundle")[3996..], await ServedDirectory.Read(reply));
    }

    /// <summary>A suffix longer than the file is the whole file, which RFC 9110 says outright.</summary>
    [Fact]
    public void ASuffixLongerThanTheFileIsTheWholeFile() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 100);

        using var reply = world.Server.Serve("/pack.bundle", "bytes=-9999");

        Assert.Equal(0, reply.Offset);
        Assert.Equal(100, reply.Length);
    }

    /// <summary>And a last-byte position past the end is clamped rather than refused.</summary>
    [Fact]
    public void ARangeRunningPastTheEndIsClamped() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 4096);

        using var reply = world.Server.Serve("/pack.bundle", "bytes=4000-9999");

        Assert.Equal(ContentStatus.PartialContent, reply.Status);
        Assert.Equal(4000, reply.Offset);
        Assert.Equal(96, reply.Length);
    }

    /// <summary>
    ///     A range that <i>starts</i> past the end is a different matter. Sending the whole file to a
    ///     client that asked for byte 900 000 of an 800 000-byte file would have it write those bytes
    ///     at the wrong offset, so it gets a 416 and a header saying how long the resource really is.
    /// </summary>
    [Fact]
    public void ARangeStartingPastTheEndIsRefusedWithTheRealLength() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 4096);

        using var reply = world.Server.Serve("/pack.bundle", "bytes=5000-");

        Assert.Equal(ContentStatus.RangeNotSatisfiable, reply.Status);
        Assert.Equal("bytes */4096", reply.ContentRange());
        Assert.Null(reply.Body);
    }

    /// <summary>
    ///     A header that cannot be understood is ignored and the whole resource sent, per RFC 9110
    ///     § 14.2. Refusing would break a client over a header that is optional by construction.
    /// </summary>
    [Theory]
    [InlineData("rubbish")]
    [InlineData("bytes=")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=100")]
    [InlineData("bytes=2000-1000")]
    [InlineData("bytes=0-100,200-300")]
    [InlineData("items=0-100")]
    public void ARangeThatCannotBeUnderstoodIsIgnored(string header) {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 4096);

        using var reply = world.Server.Serve("/pack.bundle", header);

        Assert.Equal(ContentStatus.Ok, reply.Status);
        Assert.Equal(4096, reply.Length);
    }

    /// <summary>
    ///     <b>Nothing outside the root is reachable.</b> This is the one thing here that turns a
    ///     convenience into a hole in somebody's laptop, so it is asserted against every spelling of
    ///     the attempt rather than the obvious one.
    /// </summary>
    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/../../secret.txt")]
    [InlineData("/packs/../../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/%2e%2e%2fsecret.txt")]
    [InlineData("/packs/%2e%2e/%2e%2e/secret.txt")]
    [InlineData("/./../secret.txt")]
    public void NothingOutsideTheRootIsReachable(string path) {
        var world = new ServedDirectory();
        world.Put("packs/one.bundle", 16);
        world.PutOutside("secret.txt", "the thing that must not be served");

        Assert.False(world.Server.TryResolve(path, out _));

        using var reply = world.Server.Serve(path);
        Assert.Equal(ContentStatus.NotFound, reply.Status);
    }

    /// <summary>A path with a null byte in it is not a path, and is refused before anything reads it.</summary>
    [Fact]
    public void APathWithANullByteIsRefused() {
        var world = new ServedDirectory();
        world.Put("pack.bundle", 16);

        Assert.False(world.Server.TryResolve("/pack.bundle\0.txt", out _));
    }

    /// <summary>Ordinary paths still resolve, including encoded characters and nested directories.</summary>
    [Theory]
    [InlineData("/pack.bundle", "/content/pack.bundle")]
    [InlineData("pack.bundle", "/content/pack.bundle")]
    [InlineData("/packs/one.bundle", "/content/packs/one.bundle")]
    [InlineData("/packs//one.bundle", "/content/packs/one.bundle")]
    [InlineData("/packs/./one.bundle", "/content/packs/one.bundle")]
    [InlineData("/a%20b.bundle", "/content/a b.bundle")]
    public void AnOrdinaryPathResolvesInsideTheRoot(string path, string expected) {
        var world = new ServedDirectory();

        Assert.True(world.Server.TryResolve(path, out var resolved));
        Assert.Equal(expected, resolved.Value);
    }

    /// <summary>
    ///     The update client reads <c>catalog.bin.hash</c> before <c>catalog.bin</c>, and a content
    ///     build directory copied as-is does not contain one. Without this, pointing a device at a
    ///     build gives a rejected update and no clue why.
    /// </summary>
    [Fact]
    public async Task AHashFileIsComputedFromTheFileItNames() {
        var world = new ServedDirectory();
        var catalog = world.Put("catalog.bin", 512);

        using var reply = world.Server.Serve("/catalog.bin.hash");

        Assert.Equal(ContentStatus.Ok, reply.Status);
        Assert.Equal("text/plain", reply.ContentType);
        Assert.Equal(ObjectId.TextLength, reply.Length);

        var served = Encoding.UTF8.GetString(await ServedDirectory.Read(reply));
        Assert.Equal(ContentHash.Compute(catalog).ToString(), served);
    }

    /// <summary>A hash file that is on disk is served rather than recomputed.</summary>
    [Fact]
    public async Task AHashFileThatIsOnDiskWins() {
        var world = new ServedDirectory();
        world.Put("catalog.bin", 512);
        world.PutText("catalog.bin.hash", new string('a', ObjectId.TextLength));

        using var reply = world.Server.Serve("/catalog.bin.hash");

        Assert.Equal(new string('a', ObjectId.TextLength), Encoding.UTF8.GetString(await ServedDirectory.Read(reply)));
    }

    /// <summary>A hash for a file that does not exist is not invented.</summary>
    [Fact]
    public void AHashForSomethingThatIsNotThereIsNotFound() {
        var world = new ServedDirectory();

        using var reply = world.Server.Serve("/nothing.bin.hash");

        Assert.Equal(ContentStatus.NotFound, reply.Status);
    }

    /// <summary>A synthesised hash answers ranges too, so it is not a special case for the client.</summary>
    [Fact]
    public async Task ASynthesisedHashCanBeRanged() {
        var world = new ServedDirectory();
        var catalog = world.Put("catalog.bin", 512);

        using var reply = world.Server.Serve("/catalog.bin.hash", "bytes=8-15");

        Assert.Equal(ContentStatus.PartialContent, reply.Status);
        Assert.Equal(8, reply.Length);
        Assert.Equal($"bytes 8-15/{ObjectId.TextLength}", reply.ContentRange());
        Assert.Equal(
            ContentHash.Compute(catalog).ToString()[8..16],
            Encoding.UTF8.GetString(await ServedDirectory.Read(reply))
        );
    }

    /// <summary>A directory with something in it, and a secret next to it that must stay there.</summary>
    sealed class ServedDirectory {
        readonly Dictionary<string, byte[]> written = new(StringComparer.Ordinal);
        readonly VirtualFileSystem files = new();

        public ContentServer Server { get; }

        public ServedDirectory() {
            var storage = new MemoryFileProvider();
            files.Mount(new("/content"), storage);

            // A second mount beside the served one: if the server can be made to climb out of
            // /content, this is what it climbs into.
            files.Mount(new("/private"), new MemoryFileProvider());

            Server = new(files, new("/content"));
        }

        public byte[] Put(string name, int size) {
            var contents = new byte[size];

            for (var index = 0; index < size; index++) {
                contents[index] = (byte)(index * 7);
            }

            using (var writing = files.OpenWrite(new VirtualPath("/content") / name)) {
                writing.Write(contents);
            }

            written[name] = contents;

            return contents;
        }

        public void PutText(string name, string contents) {
            var bytes = Encoding.UTF8.GetBytes(contents);

            using var writing = files.OpenWrite(new VirtualPath("/content") / name);
            writing.Write(bytes);
        }

        public void PutOutside(string name, string contents) {
            using var writing = files.OpenWrite(new VirtualPath("/private") / name);
            writing.Write(Encoding.UTF8.GetBytes(contents));
        }

        public byte[] Contents(string name) => written[name];

        public static async Task<byte[]> Read(ContentReply reply) {
            using var buffer = new MemoryStream();
            await reply.WriteBodyToAsync(buffer, TestContext.Current.CancellationToken);

            return buffer.ToArray();
        }
    }
}
