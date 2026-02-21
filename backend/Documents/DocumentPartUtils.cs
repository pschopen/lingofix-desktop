using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace Lingofix.Backend.Documents;

internal static class DocumentPartUtils
{
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
