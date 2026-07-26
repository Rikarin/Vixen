// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.IO.Tests;

public class VirtualPathTests {
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a", "/a")]
    [InlineData("/a/b/c", "/a/b/c")]
    [InlineData("//a//b//", "/a/b")]
    [InlineData("/a/", "/a")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/b/..", "/a")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/a/../b", "/b")]
    [InlineData("/a/..", "/")]
    [InlineData("/.", "/")]
    [InlineData("/app/textures/../models/x.fbx", "/app/models/x.fbx")]
    public void NormalisationCollapsesWhatItShould(string input, string expected) =>
        Assert.Equal(expected, new VirtualPath(input).Value);

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("relative")]
    [InlineData("C:/x")]
    [InlineData("/a\\b")]
    [InlineData("/..")]
    [InlineData("/a/../..")]
    [InlineData("/a\u0000b")]
    public void InvalidPathsAreRejectedRatherThanRepaired(string input) {
        Assert.Throws<ArgumentException>(() => new VirtualPath(input));
        Assert.False(VirtualPath.TryCreate(input, out _));
    }

    [Fact]
    public void TheDefaultIsNotAPath() {
        var path = default(VirtualPath);

        Assert.True(path.IsEmpty);
        Assert.Equal(string.Empty, path.Value);
        Assert.False(path.IsRoot);
        Assert.Throws<InvalidOperationException>(() => path.Combine("a"));
    }

    [Fact]
    public void CaseIsPartOfTheIdentity() {
        Assert.NotEqual(new VirtualPath("/app/Texture.png"), new VirtualPath("/app/texture.png"));
        Assert.NotEqual(new VirtualPath("/App/x"), new VirtualPath("/app/x"));
    }

    [Theory]
    [InlineData("/app/textures/x.png", "/app")]
    [InlineData("/app", "/app")]
    [InlineData("/", "/")]
    public void MountIsTheFirstSegment(string path, string expected) =>
        Assert.Equal(expected, new VirtualPath(path).Mount.Value);

    [Theory]
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    public void ParentWalksUpAndStopsAtTheRoot(string path, string expected) =>
        Assert.Equal(expected, new VirtualPath(path).Parent.Value);

    [Theory]
    [InlineData("/a/b/x.png", "x.png", ".png", "x")]
    [InlineData("/x.tar.gz", "x.tar.gz", ".gz", "x.tar")]
    [InlineData("/noext", "noext", "", "noext")]
    [InlineData("/.gitignore", ".gitignore", "", ".gitignore")]
    [InlineData("/", "", "", "")]
    public void NamesAndExtensionsSplitWhereTheyShould(
        string path,
        string name,
        string extension,
        string withoutExtension
    ) {
        var subject = new VirtualPath(path);

        Assert.Equal(name, subject.FileName.ToString());
        Assert.Equal(extension, subject.Extension.ToString());
        Assert.Equal(withoutExtension, subject.FileNameWithoutExtension.ToString());
    }

    [Theory]
    [InlineData("/a", "b", "/a/b")]
    [InlineData("/", "b", "/b")]
    [InlineData("/a", "b/c", "/a/b/c")]
    [InlineData("/a/b", "../c", "/a/c")]
    [InlineData("/a", "/absolute", "/absolute")]
    [InlineData("/a", "", "/a")]
    public void CombineNormalisesTheResult(string left, string right, string expected) =>
        Assert.Equal(expected, (new VirtualPath(left) / right).Value);

    [Fact]
    public void CombineCannotEscapeTheRoot() =>
        Assert.Throws<ArgumentException>(() => new VirtualPath("/a") / "../..");

    [Theory]
    [InlineData("/x.png", ".ktx2", "/x.ktx2")]
    [InlineData("/x.png", "ktx2", "/x.ktx2")]
    [InlineData("/x", ".ktx2", "/x.ktx2")]
    [InlineData("/x.png", "", "/x")]
    public void WithExtensionReplacesOrRemoves(string path, string extension, string expected) =>
        Assert.Equal(expected, new VirtualPath(path).WithExtension(extension).Value);

    /// <summary>
    ///     The one that matters for mounts. A textual prefix test says <c>/app</c> contains
    ///     <c>/application</c>, which means a mount silently swallows a sibling — and it swallows it
    ///     in the direction where files resolve to the wrong provider rather than to none.
    /// </summary>
    [Theory]
    [InlineData("/app", "/app/x", true)]
    [InlineData("/app", "/app", true)]
    [InlineData("/app", "/application", false)]
    [InlineData("/app", "/app2/x", false)]
    [InlineData("/", "/anything/at/all", true)]
    [InlineData("/a/b", "/a", false)]
    public void ContainsIsSegmentAwareRatherThanTextual(string parent, string child, bool expected) =>
        Assert.Equal(expected, new VirtualPath(parent).Contains(new(child)));

    [Theory]
    [InlineData("/app/a/b", "/app", "/a/b")]
    [InlineData("/app", "/app", "/")]
    [InlineData("/app/a", "/", "/app/a")]
    public void RelativeToStripsTheMountAndStaysRooted(string path, string prefix, string expected) =>
        Assert.Equal(expected, new VirtualPath(path).RelativeTo(new(prefix)).Value);

    [Fact]
    public void RelativeToRejectsAPrefixThatIsNotOne() =>
        Assert.Throws<ArgumentException>(() => new VirtualPath("/a/b").RelativeTo(new("/c")));

    [Fact]
    public void SegmentsEnumerateWithoutAllocating() {
        var segments = new List<string>();

        foreach (var segment in new VirtualPath("/app/textures/x.png").EnumerateSegments()) {
            segments.Add(segment.ToString());
        }

        Assert.Equal(["app", "textures", "x.png"], segments);
        Assert.Empty(Collect(VirtualPath.Root));
        return;

        static List<string> Collect(VirtualPath path) {
            var result = new List<string>();

            foreach (var segment in path.EnumerateSegments()) {
                result.Add(segment.ToString());
            }

            return result;
        }
    }

    [Fact]
    public void OrderingIsOrdinalAndTotal() {
        var paths = new[] { new VirtualPath("/b"), new VirtualPath("/A"), new VirtualPath("/a"), VirtualPath.Root };
        Array.Sort(paths);

        // Ordinal: uppercase sorts before lowercase, on every platform and in every locale. A
        // culture-aware comparison would order these differently in Turkish, and content builds hash
        // sorted listings.
        Assert.Equal(["/", "/A", "/a", "/b"], paths.Select(path => path.Value));
    }

    /// <summary>
    ///     Normalisation is idempotent, and normalised text round-trips. Both are properties the
    ///     mount table depends on: it compares paths as strings, so two spellings of one path would
    ///     be two mounts.
    /// </summary>
    [Fact]
    public void NormalisationIsIdempotentForAnythingItAccepts() {
        var segment = Gen.OneOf(Gen.Const("a"), Gen.Const("bb"), Gen.Const("."), Gen.Const(".."), Gen.Const(""), Gen.Const("x.png"));

        segment.Array[0, 8]
            .Select(segments => "/" + string.Join("/", segments))
            .Sample(text => {
                    if (!VirtualPath.TryCreate(text, out var once)) {
                        return;
                    }

                    Assert.True(VirtualPath.TryCreate(once.Value, out var twice));
                    Assert.Equal(once, twice);
                    Assert.Equal(once.Value, twice.Value);
                }
            );
    }

    /// <summary>A path is always its parent plus its name, unless it is the root.</summary>
    [Fact]
    public void ParentAndFileNameReconstructThePath() {
        var segment = Gen.OneOf(Gen.Const("a"), Gen.Const("bb"), Gen.Const("ccc"), Gen.Const("x.png"));

        segment.Array[1, 6]
            .Select(segments => "/" + string.Join("/", segments))
            .Sample(text => {
                    var path = new VirtualPath(text);
                    Assert.Equal(path, path.Parent / path.FileName.ToString());
                }
            );
    }

    [Fact]
    public void FormattingRoundTrips() {
        var path = new VirtualPath("/app/x.png");
        Span<char> buffer = stackalloc char[path.Value.Length];

        Assert.True(path.TryFormat(buffer, out var written, default, null));
        Assert.Equal(path.Value.Length, written);
        Assert.Equal(path.Value, buffer[..written].ToString());

        // Too short must write nothing and say so, which is what ISpanFormattable requires.
        Assert.False(path.TryFormat(buffer[..3], out var partial, default, null));
        Assert.Equal(0, partial);
    }
}
