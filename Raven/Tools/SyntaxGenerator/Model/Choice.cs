#nullable disable
using System.Xml.Serialization;

namespace SyntaxGenerator.Model;

public class Choice : TreeTypeChild {
    // Note: 'Choice's should not be children of a 'Choice'.  It's not necessary, and the child
    // choice can just be inlined into the parent.
    [XmlElement(ElementName = "Field", Type = typeof(Field))]
    [XmlElement(ElementName = "Sequence", Type = typeof(Sequence))]
    public List<TreeTypeChild> Children;

    [XmlAttribute]
    public bool Optional;
}
