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

namespace WebTools
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType(HtmlContentTypeDefinition.HtmlContentType)]
    [TagType(typeof(TextMarkerTag))]
    internal class RadioButtonGroupNameReferenceTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            HtmlEditorDocument document = HtmlEditorDocument.TryFromTextBuffer(buffer);

            if (document == null)
                return null;

            return new RadioButtonGroupNameReferenceTagger(textView, buffer, document) as ITagger<T>;
        }


        private sealed class RadioButtonGroupNameReferenceTag : TextMarkerTag
        {
            public RadioButtonGroupNameReferenceTag()
                : base("MarkerFormatDefinition/HighlightedReference")
            { }

            internal static readonly RadioButtonGroupNameReferenceTag Instance = new RadioButtonGroupNameReferenceTag();
        }


        private sealed class RadioButtonGroupNameReferenceTagger : ITagger<RadioButtonGroupNameReferenceTag>
        {
            public RadioButtonGroupNameReferenceTagger(ITextView view, ITextBuffer sourceBuffer, HtmlEditorDocument htmlDocument)
            {
                this.View = view;
                this.SourceBuffer = sourceBuffer;
                this.HtmlDocument = htmlDocument;
                this.View.Caret.PositionChanged += HandleCarePositionChanged;
                this.View.LayoutChanged += HandleViewLayoutChanged;
            }
            
            private ITextView View { get; set; }
            private ITextBuffer SourceBuffer { get; set; }
            private HtmlEditorDocument HtmlDocument { get; set; }

            private NormalizedSnapshotSpanCollection _highlightedSpans;


            public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

            public IEnumerable<ITagSpan<RadioButtonGroupNameReferenceTag>> GetTags(NormalizedSnapshotSpanCollection spans)
            {
                if (_highlightedSpans?.Any() ?? false)
                {
                    if (spans.First().Snapshot != _highlightedSpans.First().Snapshot)
                        _highlightedSpans = new NormalizedSnapshotSpanCollection(
                            from span in _highlightedSpans
                            select span.TranslateTo(spans.First().Snapshot, SpanTrackingMode.EdgeExclusive)
                        );

                    foreach (var span in NormalizedSnapshotSpanCollection.Overlap(spans, _highlightedSpans))
                        yield return new TagSpan<RadioButtonGroupNameReferenceTag>(span, RadioButtonGroupNameReferenceTag.Instance);
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

                this.HtmlDocument.HtmlEditorTree.GetPositionElement(point.Value.Position, out ElementNode element, out AttributeNode attribute);
                
                if ((attribute?.ValueRangeUnquoted.Contains(point.Value.Position) ?? false)
                    || (attribute?.ValueRangeUnquoted.End == point.Value.Position))
                {
                    if (attribute.Name?.Equals("name", StringComparison.InvariantCultureIgnoreCase) ?? false)
                    {
                        if ((element?.Name.Equals("input", StringComparison.InvariantCultureIgnoreCase) ?? false) &&
                            (element?.GetAttribute("type")?.Value?.Equals("radio", StringComparison.InvariantCultureIgnoreCase) ?? false))
                        {
                            string name = attribute.Value;

                            // find references
                            newHighlightedSpans = new NormalizedSnapshotSpanCollection(FindReferences(name, point.Value.Snapshot));
                        }
                    }
                }

                _highlightedSpans = newHighlightedSpans;

                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
            }

            private IReadOnlyCollection<SnapshotSpan> FindReferences(string name, ITextSnapshot snapshot)
            {
                ICollection<AttributeNode> attributes = new List<AttributeNode>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((e, p) =>
                {
                    if (!e.Name.Equals("input", StringComparison.InvariantCultureIgnoreCase))
                        return true;

                    if (!(e.GetAttribute("type")?.Value?.Equals("radio", StringComparison.InvariantCultureIgnoreCase) ?? false))
                        return true;

                    var attr = e.GetAttribute("name", ignoreCase: true);
                    if (attr?.Value == name)
                        attributes.Add(attr);

                    return true;
                }, null);

                return (
                    from attr in attributes
                    select new SnapshotSpan(snapshot, attr.ValueRangeUnquoted.Start, attr.ValueRangeUnquoted.Length)
                ).ToList();
            }
        }
    }
}
