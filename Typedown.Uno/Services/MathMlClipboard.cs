using System.Xml.Linq;

namespace Typedown.Uno.Services;

/// <summary>Converts KaTeX's annotated MathML into the equation form Word accepts on the clipboard.</summary>
public static class MathMlClipboard
{
    private static readonly XNamespace Math = "http://www.w3.org/1998/Math/MathML";

    public static string? ToWordFormat(string? mathml)
    {
        if (string.IsNullOrWhiteSpace(mathml)) return null;
        XElement math;
        try { math = XElement.Parse(mathml); }
        catch (System.Xml.XmlException) { return null; }
        if (math.Name != Math + "math") return null;
        var content = math.Elements().ToList();
        if (content.Count == 1 && content[0].Name == Math + "semantics")
            content = content[0].Elements().Take(1).ToList();
        content.RemoveAll(e => e.Name == Math + "annotation" || e.Name == Math + "annotation-xml");
        var root = new XElement(Math + "math",
            new XAttribute(XNamespace.Xmlns + "mml", Math.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "m", "http://schemas.openxmlformats.org/officeDocument/2006/math"),
            content.Select(Unannotated));
        return "<?xml version=\"1.0\"?>\r\n" + root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Unannotated(XElement element)
    {
        var copy = new XElement(element.Name, element.Attributes().Where(a => !a.IsNamespaceDeclaration));
        foreach (var node in element.Nodes())
        {
            if (node is XElement child)
            {
                if (child.Name != Math + "annotation" && child.Name != Math + "annotation-xml") copy.Add(Unannotated(child));
            }
            else if (node is XText text) copy.Add(new XText(text.Value));
        }
        return copy;
    }
}
