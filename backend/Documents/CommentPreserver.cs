using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lingofix.Backend.Documents;

internal static class CommentPreserver
{
    public static void PreserveOriginalComments(string originalPath, string outputPath, IRunLogger? logger)
    {
        using var originalDoc = WordprocessingDocument.Open(originalPath, false);
        using var outputDoc = WordprocessingDocument.Open(outputPath, true);

        var sourceComments = originalDoc.MainDocumentPart?.WordprocessingCommentsPart?.Comments;
        var targetMain = outputDoc.MainDocumentPart;
        if (targetMain is null)
        {
            return;
        }

        if (sourceComments is null)
        {
            return;
        }

        var targetPart = targetMain.WordprocessingCommentsPart ?? targetMain.AddNewPart<WordprocessingCommentsPart>();
        targetPart.Comments = (Comments)sourceComments.CloneNode(true);
        targetPart.Comments.Save();
        logger?.Info("Comments preserved unchanged from original document.");
    }
}
