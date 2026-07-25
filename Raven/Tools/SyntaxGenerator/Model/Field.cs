#nullable disable
using System.Xml.Serialization;

namespace SyntaxGenerator.Model;

public class Field : TreeTypeChild {
    [XmlAttribute]
    public string Name;

    [XmlAttribute]
    public string Type;

    [XmlAttribute]
    public string Optional;

    [XmlAttribute]
    public string Override;

    [XmlAttribute]
    public string New;

    [XmlAttribute]
    public int MinCount;

    [XmlAttribute]
    public bool AllowTrailingSeparator;

    [XmlElement(ElementName = "Kind", Type = typeof(Kind))]
    public List<Kind> Kinds = [];

    [XmlElement]
    public Comment PropertyComment;

    public bool IsToken => Type == "SyntaxToken";
    public bool IsOptional => string.Equals(Optional, "true", StringComparison.OrdinalIgnoreCase);
    public bool IsOverride => string.Equals(Override, "true", StringComparison.OrdinalIgnoreCase);
    public bool IsNew => string.Equals(New, "true", StringComparison.OrdinalIgnoreCase);
    public string OverrideOrNewModifier => IsOverride ? "override " : IsNew ? "new " : "";
}
