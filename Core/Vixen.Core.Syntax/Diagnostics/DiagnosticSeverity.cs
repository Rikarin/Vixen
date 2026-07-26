// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Syntax.Diagnostics;

/// <summary>Severity of a <see cref="Diagnostic" />, ordered least to most severe.</summary>
public enum DiagnosticSeverity {
    /// <summary>Diagnostic hidden from normal output (e.g. IDE-only).</summary>
    Hidden,

    /// <summary>Informational; not a problem.</summary>
    Info,

    /// <summary>A warning that does not prevent compilation.</summary>
    Warning,

    /// <summary>An error that prevents successful compilation.</summary>
    Error
}
