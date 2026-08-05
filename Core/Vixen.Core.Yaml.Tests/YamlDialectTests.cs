// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Core.Yaml.Tests;

public sealed class YamlDialectTests {
    /// <summary>
    ///     The rule that makes a <c>.meta</c> file readable: a small collection of nothing but
    ///     scalars goes on one line, and anything else does not.
    /// </summary>
    [Fact]
    public void ASmallAllScalarCollectionIsWrittenOnOneLine() {
        var written = YamlWriter.Write(
            new YamlMapping()
                .Set("wrap", new YamlMapping().Set("u", new YamlScalar("Repeat")).Set("v", new YamlScalar("Repeat")))
                .Set("labels", new YamlSequence().Add(new YamlScalar("ui")).Add(new YamlScalar("hd")))
                .Set("extensions", new YamlMapping())
        );

        Assert.Equal(
            """
            wrap: { u: Repeat, v: Repeat }
            labels: [ui, hd]
            extensions: {}

            """.ReplaceLineEndings("\n"),
            written
        );
    }

    /// <summary>
    ///     And the two things that send it back to block form: something inside that is not a
    ///     scalar, or a rendered width past <see cref="YamlWriter.FlowWidthLimit" />. The second is
    ///     doc 08's <c>materialMapping</c>, which is all scalars and still belongs on its own lines.
    /// </summary>
    [Fact]
    public void ACollectionThatIsWideOrNestedIsWrittenAsABlock() {
        var wide = new YamlMapping()
            .Set("Body", new YamlScalar("vx:9e8a44c9930c64e388ca034c5fe4c426"))
            .Set("Cloth", new YamlScalar("vx:c1d2e3f4a5b60718c1d2e3f4a5b60718"));

        var nested = new YamlMapping()
            .Set("count", new YamlScalar("3", YamlScalarStyle.Plain))
            .Set("inner", new YamlMapping().Set("a", new YamlScalar("1", YamlScalarStyle.Plain)));

        Assert.Equal(
            """
            materialMapping:
              Body: vx:9e8a44c9930c64e388ca034c5fe4c426
              Cloth: vx:c1d2e3f4a5b60718c1d2e3f4a5b60718
            generateLods:
              count: 3
              inner: { a: 1 }

            """.ReplaceLineEndings("\n"),
            YamlWriter.Write(new YamlMapping().Set("materialMapping", wide).Set("generateLods", nested))
        );
    }

    /// <summary>
    ///     An explicit style wins over the emitter's judgement. This is what carries a hand-written
    ///     file's shape through a rewrite: whatever the author chose is what was read, and what was
    ///     read is what is written.
    /// </summary>
    [Fact]
    public void AnExplicitStyleOverridesWhatTheEmitterWouldHaveChosen() {
        var forcedBlock = new YamlMapping { Style = YamlCollectionStyle.Block }.Set("u", new YamlScalar("Repeat"));

        var forcedFlow = new YamlMapping { Style = YamlCollectionStyle.Flow }
            .Set("Body", new YamlScalar("vx:9e8a44c9930c64e388ca034c5fe4c426"))
            .Set("Cloth", new YamlScalar("vx:c1d2e3f4a5b60718c1d2e3f4a5b60718"));

        Assert.Equal(
            """
            wrap:
              u: Repeat
            materialMapping: { Body: 'vx:9e8a44c9930c64e388ca034c5fe4c426', Cloth: 'vx:c1d2e3f4a5b60718c1d2e3f4a5b60718' }

            """.ReplaceLineEndings("\n"),
            YamlWriter.Write(new YamlMapping().Set("wrap", forcedBlock).Set("materialMapping", forcedFlow))
        );
    }

    /// <summary>
    ///     A string that happens to read as a number or a boolean has to come back a string. Left
    ///     unquoted, a version field typed as text would silently become <c>1.2</c> the first time
    ///     someone wrote <c>1.20</c>.
    /// </summary>
    [Theory]
    [InlineData("2048", "'2048'")]
    [InlineData("true", "'true'")]
    [InlineData("null", "'null'")]
    [InlineData("~", "'~'")]
    [InlineData("1.5e-3", "'1.5e-3'")]
    [InlineData("-8", "'-8'")]
    [InlineData("", "''")]
    [InlineData(" padded ", "' padded '")]
    [InlineData("width: 2048", "'width: 2048'")]
    [InlineData("red # not a comment", "'red # not a comment'")]
    [InlineData("it's fine", "it's fine")]
    [InlineData("Srgb", "Srgb")]
    [InlineData("vx:9e8a44c9930c64e388ca034c5fe4c426", "vx:9e8a44c9930c64e388ca034c5fe4c426")]
    [InlineData("ui/textures/hero", "ui/textures/hero")]
    public void AStringIsQuotedExactlyWhenLeavingItBareWouldChangeIt(string value, string expected) =>
        Assert.Equal($"key: {expected}\n", YamlWriter.Write(new YamlMapping().Set("key", new YamlScalar(value))));

    /// <summary>
    ///     And a value the mapper knows is a number is written as one — the mapper marks it
    ///     <see cref="YamlScalarStyle.Plain" /> because it is the only layer that knows the type.
    /// </summary>
    [Fact]
    public void AValueDeclaredPlainIsWrittenBare() =>
        Assert.Equal(
            "maxSize: 2048\n",
            YamlWriter.Write(new YamlMapping().Set("maxSize", new YamlScalar("2048", YamlScalarStyle.Plain)))
        );

    /// <summary>
    ///     A colon is only a key separator when a space follows it, so <c>vx:</c> references are
    ///     bare in block context — but inside a flow collection a reader is looking for keys, and
    ///     they are not.
    /// </summary>
    [Fact]
    public void AColonIsSafeInBlockContextAndNotInFlow() {
        var reference = new YamlScalar("vx:9e8a44c9930c64e388ca034c5fe4c426");

        Assert.Equal(
            "albedo: vx:9e8a44c9930c64e388ca034c5fe4c426\n",
            YamlWriter.Write(new YamlMapping().Set("albedo", reference))
        );

        Assert.Equal(
            "slots: ['vx:9e8a44c9930c64e388ca034c5fe4c426']\n",
            YamlWriter.Write(new YamlMapping().Set("slots", new YamlSequence().Add(reference)))
        );
    }

    [Fact]
    public void ATagIsTheTypeNameWithoutItsMarker() {
        var node = YamlReader.Read("importer: !TextureImporter\n  version: 3\n");
        var importer = Assert.IsType<YamlMapping>(((YamlMapping)node)["importer"]);

        Assert.Equal("TextureImporter", importer.Tag);
    }

    /// <summary>
    ///     Comments are the one thing the emitter normalises rather than reproduces, because the
    ///     scanner has already dropped the whitespace after the <c>#</c>. What matters for a
    ///     migration is that it converges: the second rewrite of a file changes nothing.
    /// </summary>
    [Fact]
    public void CommentSpacingIsNormalisedOnceAndThenStable() {
        const string original = "#no space\nguid: abc\n";

        var once = YamlWriter.Write(YamlReader.Read(original));
        var twice = YamlWriter.Write(YamlReader.Read(once));

        Assert.Equal("# no space\nguid: abc\n", once);
        Assert.Equal(once, twice);
    }

    /// <summary>
    ///     The root is a block however small it is. A two-key <c>.meta</c> rendered as
    ///     <c>{ guid: …, metaVersion: 1 }</c> would turn the next edit into a whole-line diff, which
    ///     is the one thing the dialect exists to avoid.
    /// </summary>
    [Fact]
    public void TheRootIsAlwaysABlockHoweverSmallItIs() =>
        Assert.Equal(
            "guid: abc\n",
            YamlWriter.Write(
                new YamlMapping { Style = YamlCollectionStyle.Flow }.Set("guid", new YamlScalar("abc"))
            )
        );

    /// <summary>
    ///     A <see cref="YamlScalarStyle.Plain" /> that would break the document is still quoted. The
    ///     style is a hint about typing, not permission to write something that no longer parses —
    ///     and a tree assembled by hand can be wrong about it in a way one the reader produced
    ///     cannot.
    /// </summary>
    [Fact]
    public void APlainStyleThatWouldBreakTheDocumentIsQuotedAnyway() =>
        Assert.Equal(
            "key: 'red # not a comment'\n",
            YamlWriter.Write(
                new YamlMapping().Set("key", new YamlScalar("red # not a comment", YamlScalarStyle.Plain))
            )
        );

    [Fact]
    public void AnEmptyFileIsAnEmptyMappingRatherThanAnError() {
        var node = YamlReader.Read(string.Empty);

        Assert.Equal(0, Assert.IsType<YamlMapping>(node).Count);
    }

    /// <summary>
    ///     Anchors and aliases are not in the dialect. An asset reference is a <c>vx:</c> scalar,
    ///     which answers the same question without admitting the cycles that come with them.
    /// </summary>
    [Fact]
    public void AnAliasIsRefusedRatherThanQuietlyExpanded() {
        var failure = Assert.Throws<YamlParseException>(
            () => YamlReader.Read("base: &anchor\n  a: 1\nderived: *anchor\n")
        );

        Assert.Contains("vx:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AComplexKeyIsRefusedRatherThanFlattened() {
        var failure = Assert.Throws<YamlParseException>(() => YamlReader.Read("? [a, b]\n: value\n"));

        Assert.Contains("Complex keys", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An empty key is legal YAML and is not in this dialect, and the refusal has to happen in
    ///     the reader. <c>YamlMapping.Set</c> guards against one too, but that guard states a
    ///     <i>caller's</i> contract — a migration that computed a key and got nothing back — and a
    ///     key read out of a file has no caller to blame, so letting it fire turned a one-byte
    ///     document into an <c>ArgumentException</c> naming a parameter nobody passed.
    ///     <para>Found by <c>Vixen.Net.Fuzz</c>'s <c>meta</c> target; the shortest input in its corpus.</para>
    /// </summary>
    [Fact]
    public void AnEmptyKeyIsAParseErrorRatherThanAnArgumentException() {
        var failure = Assert.Throws<YamlParseException>(() => YamlReader.Read(":"));

        Assert.Contains("must have a name", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     YamlDotNet does not always keep to its own exception type, so the boundary in
    ///     <c>YamlReader</c> cannot only catch <c>YamlException</c>. A comment ending in an invalid
    ///     byte comes back an <c>EndOfStreamException</c> and an unterminated plain scalar comes back
    ///     an <c>InvalidOperationException</c> — neither is a caller's mistake, and both were
    ///     reaching callers whose <c>when</c> filters could not name a type nobody knew was thrown.
    ///     <para>Both found by <c>Vixen.Net.Fuzz</c>'s <c>meta</c> target on its first run.</para>
    /// </summary>
    [Theory]
    // The escape is what a UTF-8 decode makes of the stray byte the fuzzer actually found.
    [InlineData("# rwr1\uFFFD")]
    [InlineData("a: \"unterminated")]
    [InlineData("guid: 0123\nimporter: !T\n  v: [1, 2")]
    public void AMalformedDocumentIsAParseErrorWhateverTheLibraryThrew(string text) =>
        Assert.Throws<YamlParseException>(() => YamlReader.Read(text));

    /// <summary>
    ///     Key order is the schema's — the C# record's declaration order — and replacing a value
    ///     keeps the key where it was. Moving it would be a diff nobody asked for.
    /// </summary>
    [Fact]
    public void ReplacingAValueKeepsTheKeyWhereItWas() {
        var mapping = new YamlMapping()
            .Set("guid", new YamlScalar("abc"))
            .Set("metaVersion", new YamlScalar("1", YamlScalarStyle.Plain))
            .Set("importer", new YamlScalar("x"));

        mapping.Set("metaVersion", new YamlScalar("2", YamlScalarStyle.Plain));

        Assert.Equal(["guid", "metaVersion", "importer"], mapping.Keys);
        Assert.Equal("2", ((YamlScalar)mapping["metaVersion"]!).Value);
    }

    /// <summary>
    ///     And it keeps the comments. A comment above a key describes the key, not the value that
    ///     happens to be there, so re-GUIDing an asset or bumping <c>metaVersion</c> must not delete
    ///     the sentence somebody wrote above it. A caller that means to change it says so.
    /// </summary>
    [Fact]
    public void ReplacingAValueKeepsTheCommentsUnlessTheNewValueBringsItsOwn() {
        var root = (YamlMapping)YamlReader.Read(
            "# the identity, assigned once\nguid: abc # do not edit\nmetaVersion: 1\n"
        );

        root.Set("guid", new YamlScalar("def"));

        Assert.Equal(
            "# the identity, assigned once\nguid: def # do not edit\nmetaVersion: 1\n",
            YamlWriter.Write(root)
        );

        var replacement = new YamlScalar("ghi");
        replacement.LeadingComments.Add("minted by the duplicate repair");
        root.Set("guid", replacement);

        Assert.Contains("# minted by the duplicate repair\nguid: ghi", YamlWriter.Write(root), StringComparison.Ordinal);
        Assert.DoesNotContain("assigned once", YamlWriter.Write(root), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The property the corpus test asserts by example, asserted over arbitrary documents: what
    ///     the emitter writes, the reader reads back to something the emitter writes identically.
    ///     Anything the two disagree about — a quoting rule, a width, an indent — shows up here
    ///     without anyone having thought of the case.
    /// </summary>
    [Fact]
    public void WriteThenReadThenWriteIsAFixedPointForAnyDocument() =>
        Gen.Select(GenNode(3), Gen.Const(0)).Sample(pair => {
                var once = YamlWriter.Write(pair.Item1);
                var twice = YamlWriter.Write(YamlReader.Read(once));
                Assert.Equal(once, twice);
            },
            iter: 2_000
        );

    static Gen<YamlNode> GenNode(int depth) =>
        depth == 0
            ? GenScalar()
            : Gen.Frequency(
                (5, GenScalar()),
                (2, GenMapping(depth)),
                (2, GenSequence(depth))
            );

    static Gen<YamlNode> GenScalar() =>
        Gen.Select(GenText(), Gen.Int[0, 3])
            .Select(pair => (YamlNode)new YamlScalar(
                    pair.Item1,
                    pair.Item2 switch {
                        0 => YamlScalarStyle.Any,
                        1 => YamlScalarStyle.Plain,
                        2 => YamlScalarStyle.SingleQuoted,
                        _ => YamlScalarStyle.DoubleQuoted
                    }
                )
            );

    /// <summary>
    ///     Words chosen to be awkward on purpose: things that read as numbers, as booleans, as
    ///     nothing, and things carrying the characters that decide whether a scalar can go bare.
    /// </summary>
    static Gen<string> GenText() =>
        Gen.OneOfConst(
            "Srgb",
            "ui/textures/hero",
            "vx:9e8a44c9930c64e388ca034c5fe4c426",
            "2048",
            "0.85",
            "true",
            "null",
            "~",
            "-8",
            "1.5e-3",
            "a b",
            "width: 2048",
            "red # not a comment",
            "it's fine",
            "back\\slash",
            "quote\"inside",
            "  padded  ",
            string.Empty,
            "[bracketed]",
            "{braced}",
            "- dashed"
        );

    static Gen<YamlNode> GenMapping(int depth) =>
        Gen.Select(GenKey().List[0, 4], Gen.Int[0, 2])
            .SelectMany(pair => GenNode(depth - 1).List[pair.Item1.Count, pair.Item1.Count]
                .Select(values => {
                        var mapping = new YamlMapping { Style = StyleOf(pair.Item2) };

                        for (var index = 0; index < pair.Item1.Count; index++) {
                            mapping.Set(pair.Item1[index] + index, values[index]);
                        }

                        return (YamlNode)mapping;
                    }
                )
            );

    static Gen<YamlNode> GenSequence(int depth) =>
        Gen.Select(GenNode(depth - 1).List[0, 4], Gen.Int[0, 2])
            .Select(pair => {
                    var sequence = new YamlSequence { Style = StyleOf(pair.Item2) };

                    foreach (var item in pair.Item1) {
                        sequence.Add(item);
                    }

                    return (YamlNode)sequence;
                }
            );

    static Gen<string> GenKey() => Gen.OneOfConst("guid", "maxSize", "wrap", "u", "Body", "a b", "2048");

    static YamlCollectionStyle StyleOf(int choice) =>
        choice switch {
            0 => YamlCollectionStyle.Any,
            1 => YamlCollectionStyle.Block,
            _ => YamlCollectionStyle.Flow
        };
}
