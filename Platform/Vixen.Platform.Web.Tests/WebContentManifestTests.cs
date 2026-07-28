// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.IO;
using Vixen.Platform.Web;
using Xunit;

namespace Vixen.Platform.Web.Tests;

/// <summary>
///     The hand-written manifest reader, which exists because <c>System.Text.Json</c> costs 59 KB
///     Brotli on a payload measured against 930 KB — and which therefore has to be right on its own
///     rather than by delegation.
/// </summary>
public class WebContentManifestTests {
    static WebContentManifest Parse(string json) =>
        WebContentManifest.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void AnEmptyArrayIsAnEmptyManifest() {
        var manifest = Parse("[]");

        Assert.Equal(0, manifest.Count);
        Assert.False(manifest.HasDirectory("/"));
    }

    [Fact]
    public void EntriesAreReadWithEveryField() {
        var manifest = Parse(
            """
            [
              { "path": "/textures/atlas.ktx2", "length": 4194304, "modified": 1730000000000 },
              { "path": "/bundles/level1.vxb", "length": 83886080, "url": "level1.4f2c9e.vxb" }
            ]
            """
        );

        Assert.Equal(2, manifest.Count);

        Assert.True(manifest.TryGet("/textures/atlas.ktx2", out var atlas));
        Assert.Equal(4_194_304, atlas.Length);
        Assert.Equal(1_730_000_000_000, atlas.Modified);
        Assert.Null(atlas.Url);

        Assert.True(manifest.TryGet("/bundles/level1.vxb", out var level));
        Assert.Equal(83_886_080, level.Length);
        Assert.Equal("level1.4f2c9e.vxb", level.Url);
        Assert.Equal(0, level.Modified);
    }

    [Fact]
    public void FieldOrderDoesNotMatter() {
        var manifest = Parse("""[{ "length": 7, "url": "u", "modified": 3, "path": "/a" }]""");

        Assert.True(manifest.TryGet("/a", out var entry));
        Assert.Equal(7, entry.Length);
        Assert.Equal(3, entry.Modified);
        Assert.Equal("u", entry.Url);
    }

    [Fact]
    public void APathWithoutALeadingSlashIsNormalised() {
        var manifest = Parse("""[{ "path": "textures/a.png", "length": 1 }]""");

        Assert.True(manifest.TryGet("/textures/a.png", out var entry));
        Assert.Equal("/textures/a.png", entry.Path);
    }

    [Fact]
    public void CaseIsNotFolded() {
        // Virtual paths are case-sensitive on every platform, including the ones whose file systems
        // are not. A manifest that folded case would hide the mismatch until a Linux CDN served it.
        var manifest = Parse("""[{ "path": "/Texture.PNG", "length": 1 }, { "path": "/texture.png", "length": 2 }]""");

        Assert.Equal(2, manifest.Count);
        Assert.True(manifest.TryGet("/Texture.PNG", out var upper));
        Assert.True(manifest.TryGet("/texture.png", out var lower));
        Assert.Equal(1, upper.Length);
        Assert.Equal(2, lower.Length);
    }

    [Fact]
    public void UnknownFieldsAreSkippedRatherThanRejected() {
        // Forwards compatibility: a build that records a hash, a content type or a nested group is
        // one an older engine should still be able to mount.
        var manifest = Parse(
            """
            [
              {
                "path": "/a.bin",
                "hash": "sha256-abc",
                "compressed": true,
                "variants": [ { "codec": "br" }, { "codec": "gz" } ],
                "group": { "name": "core", "priority": 1 },
                "length": 42,
                "obsolete": null
              }
            ]
            """
        );

        Assert.True(manifest.TryGet("/a.bin", out var entry));
        Assert.Equal(42, entry.Length);
    }

    [Fact]
    public void EscapesInAPathAreDecoded() {
        var manifest = Parse("""[{ "path": "/a\\b\"c\/d.bin", "length": 1 }]""");

        Assert.True(manifest.TryGet("/a\\b\"c/d.bin", out _));
    }

    [Fact]
    public void NonAsciiPathsSurviveAsUtf8() {
        var manifest = Parse("""[{ "path": "/textures/日本語.png", "length": 1 }]""");

        Assert.True(manifest.TryGet("/textures/日本語.png", out _));
    }

    [Fact]
    public void AnEscapeTheReaderDoesNotImplementSaysSo() {
        // A \uXXXX escape, which the reader deliberately does not implement — see ManifestReader
        // for what it does and why. The failure has to name itself, or the symptom is a path that
        // silently does not match anything.
        var exception = Assert.Throws<InvalidDataException>(
            () => Parse("[{ \"path\": \"/a\\u0041b\", \"length\": 1 }]")
        );

        Assert.Contains("ManifestReader", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{ \"path\" \"/a\" }]")]
    [InlineData("[{ \"path\": \"/a")]
    [InlineData("[\"not-an-object\"]")]
    public void MalformedInputIsRejectedRatherThanGuessedAt(string json) =>
        Assert.Throws<InvalidDataException>(() => Parse(json));

    [Fact]
    public void AnEntryWithNoPathIsSkipped() {
        var manifest = Parse("""[{ "length": 1 }, { "path": "/a", "length": 2 }]""");

        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.TryGet("/a", out _));
    }

    [Fact]
    public void ADuplicatePathIsTheLastOneWins() {
        var manifest = Parse("""[{ "path": "/a", "length": 1 }, { "path": "/a", "length": 2 }]""");

        Assert.Equal(1, manifest.Count);
        Assert.True(manifest.TryGet("/a", out var entry));
        Assert.Equal(2, entry.Length);
    }

    [Fact]
    public void ADirectoryExistsExactlyWhenSomethingIsUnderIt() {
        var manifest = Parse("""[{ "path": "/textures/ui/button.png", "length": 1 }]""");

        Assert.True(manifest.HasDirectory("/"));
        Assert.True(manifest.HasDirectory("/textures"));
        Assert.True(manifest.HasDirectory("/textures/ui"));
        Assert.False(manifest.HasDirectory("/textures/ui/button.png"));
        Assert.False(manifest.HasDirectory("/audio"));

        // Not a prefix match on the string: /texture is not a parent of /textures/…
        Assert.False(manifest.HasDirectory("/texture"));
    }

    [Fact]
    public void WhitespaceAndFormattingAreIgnored() {
        var compact = Parse("""[{"path":"/a","length":1},{"path":"/b","length":2}]""");
        var spread = Parse("[\n\t[]".Replace("[]", "") + "  {\r\n \"path\" : \"/a\" ,\n \"length\" : 1 } ,\n{\"path\":\"/b\",\"length\":2}\n]\n");

        Assert.Equal(compact.Count, spread.Count);
        Assert.True(spread.TryGet("/a", out var a));
        Assert.Equal(1, a.Length);
    }

    [Fact]
    public void ANegativeOrLargeLengthRoundTrips() {
        var manifest = Parse("""[{ "path": "/a", "length": 9007199254740993, "modified": -1 }]""");

        Assert.True(manifest.TryGet("/a", out var entry));

        // Beyond a double's exact integer range, which is what a JSON DOM would have quietly
        // rounded. A content bundle is not this large; the point is that the reader does not lose
        // precision it was not asked to lose.
        Assert.Equal(9_007_199_254_740_993, entry.Length);
        Assert.Equal(-1, entry.Modified);
    }
}

/// <summary>
///     Enumeration, which on a web server is entirely a question about the list of paths — there
///     are no directories to ask.
/// </summary>
public class WebContentManifestEnumerationTests {
    static WebContentManifest Manifest { get; } = WebContentManifest.Parse(
        Encoding.UTF8.GetBytes(
            """
            [
              { "path": "/a.bin", "length": 10 },
              { "path": "/textures/one.png", "length": 20, "modified": 1730000000000 },
              { "path": "/textures/two.png", "length": 30 },
              { "path": "/textures/ui/button.png", "length": 40 },
              { "path": "/textures/ui/deep/inner/leaf.png", "length": 50 }
            ]
            """
        )
    );

    [Fact]
    public void ShallowEnumerationListsDirectoriesThenFiles() {
        var entries = Manifest.Enumerate(new("/")).ToArray();

        Assert.Equal(["/textures", "/a.bin"], entries.Select(entry => entry.Path.Value));
        Assert.True(entries[0].IsDirectory);
        Assert.False(entries[1].IsDirectory);
    }

    [Fact]
    public void ShallowEnumerationDoesNotDescend() {
        var entries = Manifest.Enumerate(new("/textures")).ToArray();

        Assert.Equal(
            ["/textures/ui", "/textures/one.png", "/textures/two.png"],
            entries.Select(entry => entry.Path.Value)
        );
    }

    [Fact]
    public void RecursiveEnumerationReachesEveryFile() {
        var files = Manifest.Enumerate(new("/"), recursive: true)
            .Where(entry => !entry.IsDirectory)
            .Select(entry => entry.Path.Value)
            .ToArray();

        Assert.Equal(
            [
                "/a.bin",
                "/textures/one.png",
                "/textures/two.png",
                "/textures/ui/button.png",
                "/textures/ui/deep/inner/leaf.png"
            ],
            files
        );
    }

    [Fact]
    public void RecursiveEnumerationListsEveryIntermediateDirectory() {
        // The one this is easy to get wrong on: /textures/ui/deep and /textures/ui/deep/inner are
        // named by no entry's parent directly, only by a path passing through them.
        var directories = Manifest.Enumerate(new("/"), recursive: true)
            .Where(entry => entry.IsDirectory)
            .Select(entry => entry.Path.Value)
            .ToArray();

        Assert.Equal(
            ["/textures", "/textures/ui", "/textures/ui/deep", "/textures/ui/deep/inner"],
            directories
        );
    }

    [Fact]
    public void LengthAndTimeComeFromTheManifest() {
        var entry = Manifest.Enumerate(new("/textures")).Single(item => item.Path.Value == "/textures/one.png");

        Assert.Equal(20, entry.Length);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_730_000_000_000), entry.LastWriteUtc);
    }

    [Fact]
    public void AnEntryWithNoRecordedTimeHasTheDefault() {
        var entry = Manifest.Enumerate(new("/textures")).Single(item => item.Path.Value == "/textures/two.png");

        Assert.Equal(default, entry.LastWriteUtc);
    }

    [Fact]
    public void EnumeratingSomethingThatIsNotThereIsEmpty() =>
        Assert.Empty(Manifest.Enumerate(new("/audio"), recursive: true));
}
