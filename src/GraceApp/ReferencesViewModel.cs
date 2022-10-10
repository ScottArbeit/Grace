using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder.Model;
using static Grace.Shared.Dto.Diff;
using static Grace.Shared.Dto.Reference;
using static Grace.Shared.Types;
using NodaTime;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static GraceApp.Services;

namespace GraceApp
{
    public class FileDiff
    {
        public FileSystemDifference FileSystemDifference { get; set; }
        public List<Span> Spans { get; set; }
    }

    public class ReferenceDiff
    {
        public ReferenceDto Reference1 { get; set; }
        public ReferenceDto Reference2 { get; set; }
        public DiffDto DiffDto { get; set; }
        public string Sha256Hash1 { get { return Reference1.Sha256Hash[..8]; } }
        public string Sha256Hash2 { get { return Reference2.Sha256Hash[..8]; } }
        //public List<Tuple<FileSystemDifference, List<DiffPiece[]>>> FileDifferencesOld { 
        //    get {
        //        var inlineDiffs = DiffDto.Differences
        //            .Where(diff => diff.FileSystemEntryType == FileSystemEntryType.File)
        //            .Where(diff => DiffDto.FileDiffs.Any(fd => fd.RelativePath == diff.RelativePath))
        //            .Select(diff => Tuple.Create(diff, DiffDto.FileDiffs.First(fd => fd.RelativePath == diff.RelativePath).InlineDiff))
        //            .ToList();
        //        return inlineDiffs;
        //    }
        //}

        //public List<FileDiff> FileDifferences
        //{
        //    get
        //    {
        //        return getFileDifferences(DiffDto);
        //    }
        //}

        private List<FileDiff> getFileDifferences(DiffDto diffDto)
        {
            var diffsWithFileDiffs = diffDto.Differences
                .Where(diff => diff.FileSystemEntryType == FileSystemEntryType.File)
                .Where(diff => diffDto.FileDiffs.Any(fd => fd.RelativePath == diff.RelativePath))
                .ToList();

            var tuples = diffsWithFileDiffs.Select(diff => Tuple.Create(diff, RenderInlineDiff(diffDto.FileDiffs.First(fd => fd.RelativePath == diff.RelativePath).InlineDiff))).ToList();

            var fileDiffs = tuples.Select(tuple => new FileDiff { FileSystemDifference = tuple.Item1, Spans = tuple.Item2 }).ToList();

            return fileDiffs;
        }

        //private FormattedString whatever()
        //{
        //    try
        //    {
        //        LogToConsole($"In FileDiffConverter.Convert()");
        //        LogToConsole($"In FileDiffConverter.Convert(); value: {(value == null ? "" : value.ToString())}; targetType: {targetType.FullName}; parameter: {(parameter == null ? "" : parameter.ToString())}.");
        //        var (fileSystemDifference, diffLines) = (Tuple<FileSystemDifference, List<DiffPiece[]>>)value;
        //        LogToConsole($"diffLines.Count: {diffLines.Count}");
        //        List<Span> spans = RenderInlineDiff(diffLines);
        //        LogToConsole($"spans.Count: {spans.Count}");
        //        FormattedString formattedString = new();
        //        foreach (var span in spans)
        //        { formattedString.Spans.Add(span); }
        //        LogToConsole($"formattedString: {formattedString}");
        //        return formattedString;
        //    }
        //    catch (Exception ex)
        //    {
        //        LogToConsole($"Exception: {ex.Message}");
        //        LogToConsole($"Stack trace: {ex.StackTrace}");
        //        return new FormattedString();
        //    }
        //}

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
                ChangeType.Inserted => new Span { Text = $"{RenderLine(diffLine)}", Style = (Style)diffs["AddedStyle"] },
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
            for (int i = 0; i < inlineDiff.Count; i++)
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
    }

    public partial class ReferencesViewModel
    {
        public RangeObservableCollection<ReferenceDiff> ReferenceObservableCollection { get; } = new();

        public async Task LoadViewModel()
        {
            try
            {
                ReferenceObservableCollection.Clear();

                var (referenceDtos, diffDtos) = await Services.GetDiffsForReferenceType(ReferenceType.Save);
                var refDiffs = new List<ReferenceDiff>();
                for (int index = 0; index < (referenceDtos.Count - 2); index++)
                {
                    
                    var refDiff = new ReferenceDiff() { Reference1 = referenceDtos[index], Reference2 = referenceDtos[index + 1] };
                    var (d1, d2) = refDiff.Reference1.DirectoryId.CompareTo(refDiff.Reference2.DirectoryId) switch 
                    {
                        -1 => new Tuple<Guid, Guid>(refDiff.Reference1.DirectoryId, refDiff.Reference2.DirectoryId),
                        1 => new Tuple<Guid, Guid>(refDiff.Reference2.DirectoryId, refDiff.Reference1.DirectoryId),
                        _ => new Tuple<Guid, Guid>(refDiff.Reference1.DirectoryId, refDiff.Reference2.DirectoryId),
                    };
                    refDiff.DiffDto = diffDtos.FirstOrDefault(diffDto => diffDto.DirectoryId1 == d1 && diffDto.DirectoryId2 == d2, DiffDto.Default);
                    //foreach (var x in refDiff.FileDifferences)
                    //{
                    //    LogToConsole($"x.FileSystemDifference.RelativePath: {x.FileSystemDifference.RelativePath}");
                    //    LogToConsole($"x.Spans.Count: {x.Spans.Count}");
                    //}
                    refDiffs.Add(refDiff);
                }

                ReferenceObservableCollection.AddRange(refDiffs);
                LogToConsole($"DiffCollection.Count: {ReferenceObservableCollection.Count}");
            }
            catch (Exception ex)
            {
                var x = ex.Message;
            }
        }
    }
}
