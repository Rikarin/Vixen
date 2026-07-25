#nullable disable
using System.Xml.Serialization;

namespace Vixen.Core.Syntax.Generator.Model;

[XmlRoot]
public class Tree {
    [XmlAttribute] public string Root { get; set; }

    /// <summary>
    ///     Namespace the generated node classes are emitted into, e.g.
    ///     <c>Vixen.Raven.Syntax</c>. Green nodes land in its <c>.InternalSyntax</c>
    ///     child. Required — the generator serves several languages and cannot guess.
    /// </summary>
    [XmlAttribute] public string Namespace { get; set; }

    [XmlElement(ElementName = "Node", Type = typeof(Node))]
    [XmlElement(ElementName = "AbstractNode", Type = typeof(AbstractNode))]
    [XmlElement(ElementName = "PredefinedNode", Type = typeof(PredefinedNode))]
    public List<TreeType> Types { get; set; }
}
