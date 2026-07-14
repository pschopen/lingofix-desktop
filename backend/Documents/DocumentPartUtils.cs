using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Lingofix.Backend.Documents;

internal static class DocumentPartUtils
{
    /// <summary>
    /// True when <paramref name="element"/> lives inside a textbox story
    /// (<c>w:txbxContent</c>) that is nested within <paramref name="paragraph"/>.
    /// In OOXML a textbox sits inside a run of its host paragraph, so its runs and
    /// text are <c>Descendants</c> of that paragraph. Such content belongs to its own
    /// textbox paragraph and must never be merged into the host paragraph's text
    /// stream (which would flatten the textbox into ordinary body text).
    /// Content that is directly part of <paramref name="paragraph"/> returns false,
    /// including a textbox paragraph's own runs when it is itself the argument.
    /// </summary>
    public static bool IsInsideNestedTextBox(OpenXmlElement element, OpenXmlElement paragraph)
    {
        foreach (var ancestor in element.Ancestors())
        {
            if (ReferenceEquals(ancestor, paragraph))
            {
                return false;
            }

            if (ancestor.LocalName == "txbxContent")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="run"/> hosts a drawing, picture or embedded object
    /// (i.e. a textbox anchor, image or OLE object). Such runs carry structure that
    /// must be preserved verbatim and never rebuilt as plain text runs.
    /// </summary>
    public static bool RunCarriesEmbeddedObject(OpenXmlElement run)
    {
        return run.Descendants<OpenXmlElement>()
            .Any(e => e.LocalName is "drawing" or "pict" or "object");
    }

    public static IEnumerable<OpenXmlPart> EnumerateParts(OpenXmlPart? root)
    {
        if (root is null)
        {
            yield break;
        }

        var queue = new Queue<OpenXmlPart>();
        var seen = new HashSet<Uri>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Uri))
            {
                continue;
            }

            yield return current;
            foreach (var pair in current.Parts)
            {
                queue.Enqueue(pair.OpenXmlPart);
            }
        }
    }

    public static XDocument? TryReadXml(OpenXmlPart part)
    {
        if (!part.ContentType.EndsWith("xml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return null;
        }
    }
}
