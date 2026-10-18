using System.Text;

namespace Template.AgenticETL.Services;

public static class PageMerger
{
    public static string MergePages(string[] pages)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pages.Length; i++)
        {
            sb.AppendLine($"--- Page {i + 1} ---");
            sb.AppendLine(pages[i]);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
