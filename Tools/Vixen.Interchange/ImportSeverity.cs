// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Interchange;

/// <summary>How much attention something an importer said needs.</summary>
public enum ImportSeverity {
    /// <summary>Worth knowing. The import succeeded.</summary>
    Information,

    /// <summary>Worth fixing. The import succeeded anyway.</summary>
    Warning,

    /// <summary>The import did not succeed.</summary>
    Error
}
