using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace Lingofix.Backend.Documents;

internal sealed class FieldAudit
{
    public required Dictionary<string, int> SafeByType { get; init; }
    public required Dictionary<string, int> UnsafeByType { get; init; }
}

internal sealed class SpecialContentAudit
{
    public required int ChartTextNodeCount { get; init; }
    public required int SmartArtTextNodeCount { get; init; }
    public required int OleObjectCount { get; init; }
    public required int VmlTextboxCount { get; init; }
    public required FieldAudit FieldAudit { get; init; }
}

internal static class DocxSpecialContentInspector
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";

    public static SpecialContentAudit Inspect(WordprocessingDocument doc)
    {
        var chartTextNodes = 0;
        var smartArtTextNodes = 0;
        var oleObjectCount = 0;
        var vmlTextboxCount = 0;

        var safeByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unsafeByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in DocumentPartUtils.EnumerateParts(doc.MainDocumentPart))
        {
            var xml = DocumentPartUtils.TryReadXml(part);
            if (xml is null)
            {
                continue;
            }

            chartTextNodes += CountChartTextNodes(part, xml);
            smartArtTextNodes += CountSmartArtTextNodes(part, xml);
            oleObjectCount += xml.Descendants().Count(e =>
                e.Name.LocalName.Equals("OLEObject", StringComparison.OrdinalIgnoreCase) ||
                e.Name.LocalName.Equals("object", StringComparison.OrdinalIgnoreCase));
            vmlTextboxCount += xml.Descendants(V + "textbox").Count();

            foreach (var type in ReadFieldTypes(xml))
            {
                if (FieldTypePolicy.SafeFieldTypes.Contains(type))
                {
                    safeByType[type] = safeByType.GetValueOrDefault(type) + 1;
                }
                else
                {
                    unsafeByType[type] = unsafeByType.GetValueOrDefault(type) + 1;
                }
            }
        }

        return new SpecialContentAudit
        {
            ChartTextNodeCount = chartTextNodes,
            SmartArtTextNodeCount = smartArtTextNodes,
            OleObjectCount = oleObjectCount,
            VmlTextboxCount = vmlTextboxCount,
            FieldAudit = new FieldAudit
            {
                SafeByType = safeByType,
                UnsafeByType = unsafeByType
            }
        };
    }

    private static int CountChartTextNodes(OpenXmlPart part, XDocument xml)
    {
        if (!part.ContentType.Contains("drawingml.chart", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return xml.Descendants(A + "t").Count(e => !string.IsNullOrWhiteSpace(e.Value));
    }

    private static int CountSmartArtTextNodes(OpenXmlPart part, XDocument xml)
    {
        if (!part.ContentType.Contains("diagramData+xml", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return xml.Descendants(A + "t").Count(e => !string.IsNullOrWhiteSpace(e.Value));
    }

    private static IEnumerable<string> ReadFieldTypes(XDocument xml)
    {
        foreach (var instr in xml.Descendants(W + "instrText"))
        {
            var value = (instr.Value ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                continue;
            }

            var token = value.Split([' ', '\\', '"'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            yield return token.ToUpperInvariant();
        }
    }
}
