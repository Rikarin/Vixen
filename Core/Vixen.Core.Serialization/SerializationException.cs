// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Serialization;

/// <summary>The data does not say what the reader was told to expect.</summary>
public class SerializationException : Exception {
    /// <summary>Creates an exception with the default message.</summary>
    public SerializationException() : base("Serialisation failed.") { }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SerializationException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SerializationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The data was written by a schema version this build cannot read.</summary>
/// <remarks>
///     Separate from <see cref="SerializationException" /> because the response is different. A
///     truncated file is broken; a file from another version is intact and needs a migration, which
///     is something a person can write.
/// </remarks>
public sealed class SerializationVersionException : SerializationException {
    /// <summary>The contract the data claims to be.</summary>
    public string ContractName { get; } = string.Empty;

    /// <summary>The version the data was written with.</summary>
    public int DataVersion { get; }

    /// <summary>The version this build writes.</summary>
    public int CurrentVersion { get; }

    /// <summary>Creates an exception with the default message.</summary>
    public SerializationVersionException() { }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SerializationVersionException(string message) : base(message) { }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SerializationVersionException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates an exception naming the contract and both versions.</summary>
    /// <param name="contractName">The contract.</param>
    /// <param name="dataVersion">The version the data was written with.</param>
    /// <param name="currentVersion">The version this build writes.</param>
    public SerializationVersionException(string contractName, int dataVersion, int currentVersion) : base(
        $"'{contractName}' data is version {dataVersion} and this build writes version {currentVersion}. "
        + "Adding members does not need a version bump — the member count in the stream handles that — "
        + "so a bumped version means the layout changed incompatibly and a migration is required. "
        + $"Declare 'public static bool TryMigrate(int fromVersion, ref SerializationReader reader, ref {contractName} value)' on the type."
    ) {
        ContractName = contractName;
        DataVersion = dataVersion;
        CurrentVersion = currentVersion;
    }
}
