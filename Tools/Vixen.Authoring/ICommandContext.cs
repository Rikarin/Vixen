// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Authoring;

/// <summary>What a running command reports to whoever is running it.</summary>
public interface ICommandContext {
    /// <summary>Declares that the running command changed something inside <paramref name="document" />.</summary>
    /// <param name="document">The document that was changed.</param>
    void Touch(IAuthoredDocument document);
}
