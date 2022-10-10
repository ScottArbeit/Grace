using DiffPlex.DiffBuilder.Model;
using static GraceApp.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Grace.Shared.Types;

namespace GraceApp
{
    public class FileDiffConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                LogToConsole($"In FileDiffConverter.Convert()");
                LogToConsole($"In FileDiffConverter.Convert(); value: {(value == null ? "" : value.ToString())}; targetType: {targetType.FullName}; parameter: {(parameter == null ? "" : parameter.ToString())}.");
                var (fileSystemDifference, diffLines) = (Tuple<FileSystemDifference, List<DiffPiece[]>>)value;
                LogToConsole($"diffLines.Count: {diffLines.Count}");
                List<Span> spans = RenderInlineDiff(diffLines);
                LogToConsole($"spans.Count: {spans.Count}");
                FormattedString formattedString = new();
                foreach (var span in spans)
                    { formattedString.Spans.Add(span); }
                LogToConsole($"formattedString: {formattedString}");
                return formattedString;
            }
            catch (Exception ex)
            {
                LogToConsole($"Exception: {ex.Message}");
                LogToConsole($"Stack trace: {ex.StackTrace}");
                return new FormattedString();
            }
        }

        private string RenderLine(DiffPiece diffLine)
        {
            if (!diffLine.Position.HasValue)
            {
                return $"        {diffLine.Text}&#10;";
            }
            else
            {
                return $"{diffLine.Position,6:D}: {diffLine.Text}&#10;";
            }
        }

        private Span GetSpan(DiffPiece diffLine)
        {
            return diffLine.Type switch
            {
                ChangeType.Inserted => new Span { Text = $"{RenderLine(diffLine)}", Style = (Style)diffs["InsertedStyle"] },
                ChangeType.Deleted => new Span { Text = $"{RenderLine(diffLine)}", Style = (Style)diffs["DeletedStyle"] },
                ChangeType.Modified => new Span { Text = $"{RenderLine(diffLine)}", Style = (Style)diffs["ChangedStyle"] },
                ChangeType.Imaginary => new Span { Text = $"{RenderLine(diffLine)}", Style = (Style)diffs["DeemphasizedStyle"] },
                ChangeType.Unchanged => new Span { Text = $"{RenderLine(diffLine)}", Style = (Style)diffs["UnchangedStyle"] },
                _ => new Span()
            };
        }

        private readonly DiffStyles diffs = new();
        private readonly List<Span> spans = new();

        private List<Span> RenderInlineDiff(List<DiffPiece[]> inlineDiff)
        {
            List<Span> spans = new();
            for (int i = 0; i < inlineDiff.Count - 1; i++)
            {
                foreach (var diffLine in inlineDiff[i])
                {
                    spans.Add(GetSpan(diffLine));
                }
                if (i != inlineDiff.Count - 1)
                {
                    spans.Add(new Span { Text = "--------&#10;", Style = (Style)diffs["DeemphasizedStyle"] });
                }
                else
                {
                    spans.Add(new Span { Text = "&#10;", Style = (Style)diffs["DeemphasizedStyle"] });
                }
            }

            return spans;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException("FileDiffConverter has not implemented ConvertBack().");
        }
    }
}
