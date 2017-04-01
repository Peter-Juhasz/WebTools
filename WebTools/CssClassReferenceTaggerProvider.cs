using Microsoft.Html.Core.Tree.Nodes;
using Microsoft.Html.Editor.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Microsoft.Web.Core.ContentTypes;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;

namespace WebTools
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(HtmlContentTypeDefinition.HtmlContentType)]
    [TagType(typeof(TextMarkerTag))]
    internal class CssClassReferenceTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            HtmlEditorDocument document = HtmlEditorDocument.TryFromTextBuffer(buffer);
            
            if (document == null)
                return null;

            return new CssClassReferenceTagger(textView, buffer, document) as ITagger<T>;
        }


        private sealed class CssClassReferenceTag : TextMarkerTag
        {
            public CssClassReferenceTag()
                : base("MarkerFormatDefinition/HighlightedReference")
            { }

            internal static readonly CssClassReferenceTag Instance = new CssClassReferenceTag();
        }


        private sealed class CssClassReferenceTagger : ITagger<CssClassReferenceTag>
        {
            public CssClassReferenceTagger(ITextView view, ITextBuffer sourceBuffer, HtmlEditorDocument htmlDocument)
            {
                this.View = view;
                this.SourceBuffer = sourceBuffer;
                this.HtmlDocument = htmlDocument;
                this.View.Caret.PositionChanged += HandleCarePositionChanged;
                this.View.LayoutChanged += HandleViewLayoutChanged;
            }

            private static Regex ClassRegex = new Regex($@"[A-Za-z0-9\-_]+", RegexOptions.Compiled);

            private ITextView View { get; set; }
            private ITextBuffer SourceBuffer { get; set; }
            private HtmlEditorDocument HtmlDocument { get; set; }

            private NormalizedSnapshotSpanCollection _highlightedSpans;


            public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

            public IEnumerable<ITagSpan<CssClassReferenceTag>> GetTags(NormalizedSnapshotSpanCollection spans)
            {
                var actual = _highlightedSpans;

                if (actual?.Any() ?? false)
                {
                    if (spans.First().Snapshot != actual.First().Snapshot)
                        actual = new NormalizedSnapshotSpanCollection(
                            from span in actual
                            select span.TranslateTo(spans.First().Snapshot, SpanTrackingMode.EdgeExclusive)
                        );

                    foreach (var span in NormalizedSnapshotSpanCollection.Overlap(spans, actual))
                        yield return new TagSpan<CssClassReferenceTag>(span, CssClassReferenceTag.Instance);
                }
            }


            private void HandleViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
            {
                if (e.NewSnapshot != e.OldSnapshot)
                    UpdateAtCaretPosition(View.Caret.Position);
            }

            private void HandleCarePositionChanged(object sender, CaretPositionChangedEventArgs e)
            {
                UpdateAtCaretPosition(e.NewPosition);
            }

            private void UpdateAtCaretPosition(CaretPosition caretPosition)
            {
                SnapshotPoint? point = caretPosition.Point.GetPoint(SourceBuffer, caretPosition.Affinity);
                if (point == null)
                    return;

                if (_highlightedSpans?.FirstOrDefault().Snapshot == SourceBuffer.CurrentSnapshot &&
                    (_highlightedSpans?.Any(s => s.Contains(point.Value)) ?? false))
                    return;

                NormalizedSnapshotSpanCollection newHighlightedSpans = null;

                this.HtmlDocument.HtmlEditorTree.GetPositionElement(point.Value.Position, out _, out AttributeNode attribute);

                if ((attribute?.ValueRangeUnquoted.Contains(point.Value.Position) ?? false)
                    || (attribute?.ValueRangeUnquoted.End == point.Value.Position))
                {
                    if (attribute.Name?.Equals("class", StringComparison.InvariantCultureIgnoreCase) ?? false)
                    {
                        int relativeIndex = point.Value.Position - attribute.ValueRangeUnquoted.Start;

                        // find definitions
                        string @class = ClassRegex.Matches(attribute.Value)
                            .Cast<Match>()
                            .FirstOrDefault(m => m.Index <= relativeIndex && relativeIndex <= m.Index + m.Length)?
                            .Value;

                        // find references
                        if (@class != null)
                            newHighlightedSpans = new NormalizedSnapshotSpanCollection(FindReferences(@class, point.Value.Snapshot));
                    }
                }

                _highlightedSpans = newHighlightedSpans;

                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
            }

            private IReadOnlyCollection<SnapshotSpan> FindReferences(string @class, ITextSnapshot snapshot)
            {
                Regex rgx = new Regex($@"(?<=(\A|\s)){@class}(?=(\s|\Z))");

                List<SnapshotSpan> attributes = new List<SnapshotSpan>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((element, _) =>
                {
                    var attr = element.GetAttribute("class", true);
                    if (attr?.Value == null)
                        return true;

                    foreach (var match in rgx.Matches(attr.Value).Cast<Match>().Where(m => m.Value == @class))
                    {
                        attributes.Add(new SnapshotSpan(snapshot, attr.ValueRangeUnquoted.Start + match.Index, @class.Length));
                    }

                    return true;
                }, null);

                return attributes;
            }
        }
    }
}
