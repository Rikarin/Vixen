// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Core.Yaml.Meta;

/// <summary>The three facts an index rebuild needs, without reading the rest of the file.</summary>
/// <param name="Guid">The asset's identity.</param>
/// <param name="MetaVersion">Which envelope schema version it was written against.</param>
/// <param name="ImporterTag">Which importer claims it, or <see langword="null" /> if it says none.</param>
public readonly record struct MetaEnvelope(AssetId Guid, int MetaVersion, string? ImporterTag) {
    /// <summary>Whether a GUID was found at all.</summary>
    public bool IsValid => !Guid.IsEmpty;
}

/// <summary>Reads a <c>.meta</c> file's envelope and stops.</summary>
/// <remarks>
///     <para>
///         <b>This is why the schema puts the envelope first.</b>
///         [08](../../../docs/plan/08-asset-pipeline-and-addressables.md) budgets a GUID index
///         rebuild of a hundred thousand assets at under ten seconds. Parsing a hundred thousand
///         complete documents does not fit in that; reading the first few lines of each does, and the
///         importer's settings block — which is most of the file and all of the work — is never
///         touched.
///     </para>
///     <para>
///         So this is a line scanner rather than the YAML parser, and it can be because it is looking
///         at a fixed, known prefix of a file this repository also writes: three top-level keys, in
///         order, no anchors, no flow at the root. Anything it cannot make sense of it declines,
///         and the caller falls back to a full parse — a wrong answer here would be a corrupt index,
///         so it says "no" rather than guessing.
///     </para>
///     <para>
///         ⚠ <b>Taking the first match is only safe because a document may not state a key twice.</b>
///         Scanning stops at the first <c>metaVersion:</c> and <see cref="YamlReader" /> used to take
///         the last, so a merge artefact carrying two of them made the two readers of one format
///         disagree about the version an asset compiles under — 11 against 1, on 142 bytes, found by
///         <c>Vixen.Fuzz</c>'s <c>meta</c> target. The refusal lives in the reader because it is the
///         one that parses; what this scanner owes in exchange is to keep reading top-down, so that
///         "the first" and "the only" are the same answer.
///     </para>
///     <para>
///         ⚠ <b>The keys are matched case-insensitively, and matching them ordinally was the same
///         class of disagreement in a different place.</b> <c>YamlSerializer</c> binds members
///         case-insensitively, so a document saying <c>MetaVersion: 5</c> gave <b>0 from this scanner
///         and 5 from the parser</b> — the index would file the asset at version zero and the compiler
///         would build it at five. The fix belongs here rather than at the reader: refusing keys that
///         differ only in case would false-refuse a legitimate <c>extensions</c> map carrying both
///         <c>Foo</c> and <c>foo</c>, which is a document nobody wrote by mistake.
///     </para>
/// </remarks>
public static class MetaScanner {
    /// <summary>Reads the envelope out of a document.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="envelope">What it found.</param>
    /// <returns>Whether a GUID was found.</returns>
    public static bool TryScan(ReadOnlySpan<char> text, out MetaEnvelope envelope) {
        var guid = AssetId.Empty;
        var version = 0;
        string? importer = null;
        var found = 0;

        while (!text.IsEmpty && found < 3) {
            var end = text.IndexOf('\n');
            var line = end < 0 ? text : text[..end];
            text = end < 0 ? default : text[(end + 1)..];

            if (line.Length > 0 && line[^1] == '\r') {
                line = line[..^1];
            }

            // Top-level keys only. Anything indented belongs to a block this is deliberately not
            // reading, and stepping into one is how a scanner starts finding the wrong 'version:'.
            if (line.IsEmpty || line[0] is ' ' or '\t' or '#' or '-') {
                continue;
            }

            var colon = line.IndexOf(':');

            if (colon < 0) {
                continue;
            }

            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();

            if (key.Equals("guid", StringComparison.OrdinalIgnoreCase)) {
                if (!AssetId.TryParse(value, CultureInfo.InvariantCulture, out guid)) {
                    break;
                }

                found++;
            } else if (key.Equals("metaVersion", StringComparison.OrdinalIgnoreCase)) {
                if (!int.TryParse(value, CultureInfo.InvariantCulture, out version)) {
                    break;
                }

                found++;
            } else if (key.Equals("importer", StringComparison.OrdinalIgnoreCase)) {
                importer = value.Length > 1 && value[0] == '!' ? value[1..].ToString() : null;
                found++;
            }
        }

        envelope = new(guid, version, importer);
        return envelope.IsValid;
    }

    /// <summary>Reads the envelope out of a file.</summary>
    /// <param name="path">Where the file is.</param>
    /// <param name="envelope">What it found.</param>
    /// <returns>Whether a GUID was found.</returns>
    public static bool TryScanFile(string path, out MetaEnvelope envelope) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return TryScan(File.ReadAllText(path), out envelope);
    }
}
