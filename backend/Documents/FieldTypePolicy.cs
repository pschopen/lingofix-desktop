namespace Lingofix.Backend.Documents;

internal static class FieldTypePolicy
{
    public static readonly HashSet<string> SafeFieldTypes =
    [
        "HYPERLINK",
        "SYMBOL",
        "QUOTE",
        "PAGE",
        "NUMPAGES",
        "SECTION",
        "SECTIONPAGES",
        "PAGEREF"
    ];
}
