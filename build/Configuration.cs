// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Nuke.Common.Tooling;

/// <summary>The build configuration, as a type so `--configuration Relase` fails at the parameter.</summary>
[TypeConverter(typeof(TypeConverter<Configuration>))]
class Configuration : Enumeration {
    public static Configuration Debug = new() { Value = nameof(Debug) };
    public static Configuration Release = new() { Value = nameof(Release) };

    public static implicit operator string(Configuration configuration) => configuration.Value;
}
