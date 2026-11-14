// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Texturing.Layers;

/// <summary>A stack written at a format later than this build reads.</summary>
/// <remarks>
///     ⚠ <b>Refused, and not read as far as it goes.</b> A later format may say something under a key this build reads
///     differently, and a host that read it and saved it would write it back at this build's format, dropping
///     whatever was new. A <see cref="NotSupportedException" />, a format this build does not support, so a host that
///     already refuses one refuses this too; one that wants to say which build to use reads <see cref="Written" />.
/// </remarks>
public sealed class LayerStackNewerException : NotSupportedException {
    /// <summary>A stack at a format later than <see cref="LayerStackAsset.CurrentVersion" />.</summary>
    /// <param name="written">The format the file says it is at.</param>
    public LayerStackNewerException(int written)
        : base(
            string.Create(
                CultureInfo.InvariantCulture,
                $"This stack is at format {written}, and this build reads up to format {LayerStackAsset.CurrentVersion}."
            )
        ) {
        Written = written;
    }

    /// <summary>The format the file says it is at.</summary>
    public int Written { get; }

    /// <summary>The latest format this build reads.</summary>
    public int Reads { get; } = LayerStackAsset.CurrentVersion;
}
