// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Fuzz.Targets;

/// <summary>An importer's settings, so that the seeds can carry a type tag.</summary>
/// <remarks>
///     ⚠ <b>Declared here rather than referenced, and the reason is that a tag nothing claims is a
///     seed that dies at the last of three layers.</b> The real settings types live in
///     <c>Vixen.Editor.Assets</c>, which this project has no business referencing — but
///     <c>!TextureImporter</c> is resolved through <c>TypeRegistry</c>, so a sidecar carrying that tag
///     binds to nothing in a build that does not contain one and every such seed comes back a
///     <see cref="YamlBindingException" />. The polymorphic path would then be seeded only by its own
///     failure, which is the mistake the heightmap seeds record: a seed that is rejected seeds
///     nothing.
///     <para>
///         Deliberately small. What is under test is the envelope, the tag resolution and the binder's
///         handling of a nested block — not this record's fields, which only have to be enough of a
///         block for a mutator to break.
///     </para>
/// </remarks>
[DataContract("TextureImporter")]
public sealed record FuzzImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 3;

    /// <summary>How large the imported texture may be.</summary>
    public int MaxSize { get; init; } = 2048;

    /// <summary>Whether mip levels are generated.</summary>
    public bool GenerateMips { get; init; } = true;

    /// <summary>What the author renamed the parts to.</summary>
    public IReadOnlyList<SubAssetRename> SubAssetNames { get; init; } = [];
}

/// <summary>A <c>.meta</c> sidecar, read twice and the two answers compared.</summary>
/// <remarks>
///     <para>
///         <b>The fifth of the formats doc 12 asked for, and the one a person edits.</b> A bundle and
///         a chunk are written by the content build; a sidecar is committed text that authors merge,
///         resolve conflicts in by hand, and occasionally write from scratch. Every project the editor
///         opens walks every one of them, so a sidecar that can crash the reader is a project that
///         cannot be opened — and the file that does it arrives through a pull request rather than
///         through a socket.
///     </para>
///     <para>
///         ⚠ <b>Three refusals and nothing else.</b> <see cref="YamlParseException" /> for text that is
///         not the dialect, <see cref="MetaVersionException" /> for a file a newer editor wrote, and
///         <see cref="YamlBindingException" /> for one that is YAML but not this schema. Those are what
///         <c>AssetMetaFile.Read</c> documents and what both of its production callers filter on, so
///         anything else — an <c>IndexOutOfRangeException</c> out of the line scanner, an
///         <c>InvalidCastException</c> out of the binder, a <c>KeyNotFoundException</c> out of the
///         migration chain — reaches the session and is the finding. <c>ArgumentException</c> is
///         <i>not</i> on the list, unlike the heightmap's, because a null argument is a caller's bug
///         here rather than a bad file.
///     </para>
///     <para>
///         <b>Both readers, because there are two and they must not disagree.</b>
///         <c>MetaScanner</c> is a hand-written line scanner that exists so an index rebuild of a
///         hundred thousand assets can read three lines of each instead of parsing all of it, and its
///         own remarks say a wrong answer is a corrupt index. That makes the pair a differential
///         oracle in the shape <c>TransportTargets</c> already uses for chunked against whole reads:
///         when both readers answer, the fast one must have got the same envelope as the slow one.
///         Neither oracle alone sees this — the scanner never throws, so "nothing escaped" is true of
///         every wrong answer it gives.
///     </para>
/// </remarks>
public sealed class AssetMetaTarget : IFuzzTarget {
    /// <inheritdoc />
    public string Name => "meta";

    /// <inheritdoc />
    public string What => "AssetMetaFile.Read and MetaScanner.TryScan, over the same sidecar, compared";

    /// <summary>How many bytes an input may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A multiple of the input is expressible here only because the dialect refuses
    ///         anchors.</b> YAML's aliases are the billion-laughs attack with a specification behind
    ///         it — ten anchors each ten times the last is a kilobyte that expands to a gigabyte — and
    ///         a format that allowed them could not state a bound of this shape at all. This one
    ///         rejects them at the reader, so a document's node tree is bounded by its tokens.
    ///     </para>
    ///     <para>
    ///         The multiple is what a token costs end to end: the shortest entry a mapping can have is
    ///         about four bytes and it becomes a key string, a scalar node, an entry record and a list
    ///         slot, and then the binder builds a record and its strings from the same tree a second
    ///         time.
    ///     </para>
    /// </remarks>
    public long AllowanceFor(int inputLength) => 16384 + (512L * inputLength);

    /// <inheritdoc />
    public void Seed(ICollection<byte[]> corpus) {
        ArgumentNullException.ThrowIfNull(corpus);

        // ⚠ Through AssetMetaFile.Write rather than typed out, so a change to the schema changes the
        // seeds with it. The written form is also the only one whose key order matches what the
        // scanner is optimised for, which is the half of this target a hand-written seed would miss.
        corpus.Add(Written(new() { Guid = AssetId.Parse("9e8a44c9930c64e388ca034c5fe4c426") }));

        corpus.Add(
            Written(
                new() {
                    Guid = AssetId.Parse("0123456789abcdef0123456789abcdef"),
                    Importer = new FuzzImportSettings { MaxSize = 1024, GenerateMips = false },
                    Addressable = new() { Address = "ui/textures/hero", Group = "UiCore", Labels = ["ui", "hd"] },
                    Extensions = { ["author"] = "jiu", ["note"] = "a string with: a colon in it" }
                }
            )
        );

        corpus.Add(
            Written(
                new() {
                    Guid = AssetId.Parse("ffffffffffffffffffffffffffffffff"),
                    MetaVersion = 1,
                    SubAssets = [
                        new() { Name = "hero", Type = "Texture" },
                        new() { Name = "hero_1", Type = "Texture", Source = "hero" }
                    ]
                }
            )
        );

        // The shapes a person produces and the writer never does: a comment, a blank line, an
        // envelope key indented under something else, and the two forms of a truncated file. Each is
        // a branch in the scanner that a mutation of well-formed output reaches only by accident.
        foreach (var text in (string[])
            [
                "# written by hand\n\nguid: 0123456789abcdef0123456789abcdef\n\n# a note\nmetaVersion: 1\n",
                "guid: 0123456789abcdef0123456789abcdef\nimporter: !TextureImporter\n  version: 3\n  metaVersion: 99\n",
                "metaVersion: 1\nimporter: !FolderImporter\n", "guid: 0123456789abcdef0123456789abcdef",
                "guid: 0123456789abcdef0123456789abcdef\r\nmetaVersion: 1\r\n", "metaVersion: 99\nguid: 00000000000000000000000000000001\n",
                "- guid: 0123456789abcdef0123456789abcdef\n", "just a scalar\n", "{}\n", ""
            ]) {
            corpus.Add(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <inheritdoc />
    public long Run(ReadOnlySpan<byte> input) {
        var text = Encoding.UTF8.GetString(input);

        // First, because it has no failure channel at all: TryScan returns a bool and is run over
        // every file in a project during an index rebuild, so anything escaping it is unfiltered.
        var scanned = MetaScanner.TryScan(text, out var envelope);
        long signature = (17 * 31) + (scanned ? 1 : 0);

        signature = (signature * 31) + envelope.MetaVersion;
        signature = (signature * 31) + (envelope.ImporterTag?.Length ?? -1);

        if (!scanned && envelope.IsValid) {
            throw new InvalidOperationException(
                $"MetaScanner.TryScan returned false but reported a valid envelope, {envelope}."
            );
        }

        AssetMeta meta;

        try {
            meta = AssetMetaFile.Read(text);
        } catch (YamlParseException) {
            return (signature * 31) + 2;
        } catch (MetaVersionException) {
            return (signature * 31) + 3;
        } catch (YamlBindingException) {
            return (signature * 31) + 4;
        }

        signature = (signature * 31) + meta.MetaVersion;

        // ⚠ Null-tolerant against a schema that says neither can be null, and that is a finding this
        // target records rather than asserts. `subAssets: null` in a document binds to a null array
        // even though `AssetMeta.SubAssets` is declared non-nullable with a non-null default — the
        // binder decides nullability from the CLR type, which for any reference type is "yes", and
        // the C# annotation it is contradicting is not in the descriptor for it to read. So a sidecar
        // somebody commits produces an AssetMeta that violates its own contract, and the crash lands
        // in whichever consumer dereferences it first, a long way from the file that caused it.
        //
        // Not asserted here because the fix is in the binder and applies to every [DataContract] type
        // in the engine, not to `.meta` — refusing a document null for a collection member is a
        // decision with that whole blast radius behind it, and it is not this harness's to make. The
        // shape stays visible in the signature, so the corpus keeps an example of it either way.
        signature = (signature * 31) + (meta.SubAssets?.Length ?? -1);
        signature = (signature * 31) + (meta.Extensions?.Count ?? -1);
        signature = (signature * 31) + (meta.Importer is null ? 0 : 5);
        signature = (signature * 31) + (meta.Addressable is null ? 0 : 6);

        // ⚠ The differential, and it only asks a question when both readers answered. A scanner that
        // declines is doing the thing its remarks promise — the caller falls back to the full parse
        // and nothing is lost. A scanner that answers *differently* is an index entry pointing at the
        // wrong asset, which no amount of not-throwing detects.
        if (scanned) {
            if (envelope.Guid != meta.Guid) {
                throw new InvalidOperationException(
                    $"MetaScanner read guid {envelope.Guid} where AssetMetaFile read {meta.Guid}."
                );
            }

            // ⚠ Only when the scanner found the key, and working out why cost the first run of this
            // target. `MetaEnvelope.MetaVersion` is zero when no `metaVersion:` line was seen, and
            // `AssetMeta.MetaVersion` defaults to the current version when the key is absent — so a
            // hand-written sidecar carrying only a guid makes the scanner say 0 and the parser say 1,
            // and they are both right. The two readers report *different things*: one what the file
            // said, the other what it means. Asking them to agree about an absent key was a bad
            // oracle rather than a defect, and narrowing it is the honest fix.
            //
            // Worth knowing all the same, and noted here because nothing else says it: a caller that
            // reads `envelope.MetaVersion` for a file with no such key is told 0, which is not a
            // version any writer has ever produced.
            if (envelope.MetaVersion != 0 && envelope.MetaVersion != meta.MetaVersion) {
                throw new InvalidOperationException(
                    $"MetaScanner read metaVersion {envelope.MetaVersion} where AssetMetaFile read {meta.MetaVersion}."
                );
            }
        }

        return signature;
    }

    static byte[] Written(AssetMeta meta) => Encoding.UTF8.GetBytes(AssetMetaFile.Write(meta));
}
