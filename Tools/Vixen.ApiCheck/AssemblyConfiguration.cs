// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Vixen.ApiCheck;

/// <summary>
///     Which configuration an assembly was built in, read from the assembly itself — the one thing
///     about its surface that this tool cannot see in the surface.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A baseline is a promise about a Release package, and <c>--update</c> will happily
///         write a Debug one down instead.</b> <c>CheckApi</c> hard-codes Release for a reason it
///         states at length; the tool underneath it takes a path and believes whatever is at the end
///         of it. The two configurations genuinely disagree — the engine has <c>public const bool</c>
///         feature flags (<c>LeakTracker.IsSupported</c>, <c>JobScheduler.SafetyChecksEnabled</c>,
///         <c>UiDiagnostics.RecordsRegions</c>) whose values are <c>#if DEBUG</c>, and a
///         <c>const</c>'s <em>value</em> is part of what
///         <see cref="ApiSurfaceReader" /> prints. So a regeneration run against
///         <c>bin/Debug</c> rewrites one line in a diff of fifty as
///         <c>= true -> bool</c>, and the gate fails on master afterwards.
///     </para>
///     <para>
///         It failed that way twice in one session — the inner-loop escape from a gate an agent is
///         forbidden to run is exactly this tool, and <c>bin/Debug</c> is what a developer has
///         lying around. The advice was to read the diff, which is right and is also how this edit
///         survives: it reads as noise. So the tool refuses instead of hoping, which makes it
///         honest about the one input it was previously unable to question.
///     </para>
/// </remarks>
public static class AssemblyConfiguration {
    /// <summary>The configuration a baseline is a promise about.</summary>
    public const string Baseline = "Release";

    /// <summary>
    ///     The value of the assembly's <c>AssemblyConfigurationAttribute</c>, or <see langword="null" />
    ///     when it carries none — which is a different answer from "Debug" and is treated as one.
    /// </summary>
    /// <remarks>
    ///     Read straight from metadata rather than through Roslyn: the attribute is one string in a
    ///     blob, and loading a second compilation to find it would cost more than the whole surface
    ///     read. The SDK writes it from <c>$(Configuration)</c> unless a project turns
    ///     <c>GenerateAssemblyConfigurationAttribute</c> off, so an absent attribute means the
    ///     question cannot be answered here rather than that the answer is no.
    /// </remarks>
    public static string? Read(string assemblyPath) {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);

        try {
            using var stream = File.OpenRead(assemblyPath);
            using var reader = new PEReader(stream);

            if (!reader.HasMetadata) {
                return null;
            }

            var metadata = reader.GetMetadataReader();

            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes()) {
                var attribute = metadata.GetCustomAttribute(handle);

                if (!IsAssemblyConfiguration(metadata, attribute)) {
                    continue;
                }

                var value = metadata.GetBlobReader(attribute.Value);

                // Prolog, then a SerString. Anything else is an attribute this reader does not
                // understand, and guessing at it would be worse than saying so.
                if (value.RemainingBytes < sizeof(ushort) || value.ReadUInt16() != 1) {
                    return null;
                }

                return value.ReadSerializedString();
            }

            return null;
        } catch (BadImageFormatException) {
            // Not a managed assembly. The surface reader says so far better than this can, and it
            // is the next thing to run.
            return null;
        }
    }

    /// <summary>Whether a configuration read by <see cref="Read" /> may be written into a baseline.</summary>
    /// <remarks>
    ///     <see langword="null" /> is refused as well. An assembly that does not say what it is
    ///     cannot be shown to be the one the gate compares against, and the failure this guards is
    ///     silent in the direction that breaks master.
    /// </remarks>
    public static bool IsBaseline(string? configuration) =>
        configuration is not null && string.Equals(configuration, Baseline, StringComparison.OrdinalIgnoreCase);

    /// <summary>How a configuration is named in a log line, including when there is none.</summary>
    public static string Describe(string? configuration) => configuration ?? "no configuration attribute";

    static bool IsAssemblyConfiguration(MetadataReader metadata, CustomAttribute attribute) {
        if (attribute.Constructor.Kind != HandleKind.MemberReference) {
            return false;
        }

        var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);

        if (member.Parent.Kind != HandleKind.TypeReference) {
            return false;
        }

        var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);

        return metadata.StringComparer.Equals(type.Name, "AssemblyConfigurationAttribute")
            && metadata.StringComparer.Equals(type.Namespace, "System.Reflection");
    }
}
