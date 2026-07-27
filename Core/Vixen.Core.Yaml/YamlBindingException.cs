// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Yaml;

/// <summary>A document that is valid YAML but does not fit the type it was read into.</summary>
/// <remarks>
///     Separate from <see cref="YamlParseException" /> because the two are different problems with
///     different fixes: one is a broken file, the other is a file describing something the running
///     build does not have a type for. An importer that was removed produces the second, and the
///     asset database's answer to it is to quarantine the asset rather than to refuse to open.
/// </remarks>
public sealed class YamlBindingException : Exception {
    /// <summary>Where in the document it went wrong, as a dotted path.</summary>
    public string Path { get; }

    /// <summary>Reports a problem.</summary>
    /// <param name="path">Where in the document.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="inner">What caused it, if anything.</param>
    public YamlBindingException(string path, string message, Exception? inner = null)
        : base(path.Length == 0 ? message : $"{path}: {message}", inner) =>
        Path = path;
}
