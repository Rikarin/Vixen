#nullable disable
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

public class Field : TreeTypeChild {
    [XmlAttribute] public string Name { get; set; }

    [XmlAttribute] public string Type { get; set; }

    [XmlAttribute] public string Optional { get; set; }

    [XmlAttribute] public string Override { get; set; }

    [XmlAttribute] public string New { get; set; }

    [XmlAttribute] public int MinCount { get; set; }

    [XmlAttribute] public bool AllowTrailingSeparator { get; set; }

    [XmlElement(ElementName = "Kind", Type = typeof(Kind))] public List<Kind> Kinds { get; set; } = [];

    [XmlElement] public Comment PropertyComment { get; set; }

    public bool IsToken => Type == "SyntaxToken";
    public bool IsOptional => string.Equals(Optional, "true", StringComparison.OrdinalIgnoreCase);
    public bool IsOverride => string.Equals(Override, "true", StringComparison.OrdinalIgnoreCase);
    public bool IsNew => string.Equals(New, "true", StringComparison.OrdinalIgnoreCase);
    public string OverrideOrNewModifier => IsOverride ? "override " : IsNew ? "new " : "";
}
