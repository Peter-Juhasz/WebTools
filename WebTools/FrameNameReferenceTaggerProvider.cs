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
    internal class FrameNameReferenceTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            HtmlEditorDocument document = HtmlEditorDocument.TryFromTextBuffer(buffer);

            if (document == null)
                return null;

            return new FrameNameReferenceTagger(textView, buffer, document) as ITagger<T>;
        }


        private sealed class FrameNameDefinitionTag : TextMarkerTag
        {
            public FrameNameDefinitionTag()
                : base("MarkerFormatDefinition/HighlightedDefinition")
            { }

            public static readonly FrameNameDefinitionTag Instance = new FrameNameDefinitionTag();
        }

        private sealed class FrameNameReferenceTag : TextMarkerTag
        {
            public FrameNameReferenceTag()
                : base("MarkerFormatDefinition/HighlightedReference")
            { }

            public static readonly FrameNameReferenceTag Instance = new FrameNameReferenceTag();
        }


        private class FrameNameReferenceTagger : ITagger<TextMarkerTag>
        {
            public FrameNameReferenceTagger(ITextView view, ITextBuffer sourceBuffer, HtmlEditorDocument document)
            {
                this.View = view;
                this.SourceBuffer = sourceBuffer;
                this.View.Caret.PositionChanged += HandleCaretPositionChanged;
                this.View.LayoutChanged += HandleViewLayoutChanged;
                this.HtmlDocument = document;
            }

            private static readonly IReadOnlyCollection<string> ElementsWithTarget = new string[] { "a", "form", "area", "base" };
            private static readonly IReadOnlyCollection<string> FrameElementsWithName = new string[] { "iframe", "frame" };
            private static readonly IReadOnlyCollection<string> PredefinedFrameNames = new string[] { "_blank", "_parent", "_self", "_top" };

            private ITextView View { get; set; }
            private ITextBuffer SourceBuffer { get; set; }
            private HtmlEditorDocument HtmlDocument { get; set; }

            private NormalizedSnapshotSpanCollection _highlightedSpans;
            private NormalizedSnapshotSpanCollection _definitionSpans;

            public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

            public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
            {
                if (_highlightedSpans?.Any() ?? false)
                {
                    if (spans.First().Snapshot != _highlightedSpans.First().Snapshot)
                        _highlightedSpans = new NormalizedSnapshotSpanCollection(
                            from span in _highlightedSpans
                            select span.TranslateTo(spans.First().Snapshot, SpanTrackingMode.EdgeExclusive)
                        );
                    
                    foreach (var span in NormalizedSnapshotSpanCollection.Overlap(spans, _highlightedSpans))
                        yield return new TagSpan<FrameNameReferenceTag>(span, FrameNameReferenceTag.Instance);
                }

                if (_definitionSpans?.Any() ?? false)
                {
                    if (spans.First().Snapshot != _definitionSpans.First().Snapshot)
                        _definitionSpans = new NormalizedSnapshotSpanCollection(
                            from span in _definitionSpans
                            select span.TranslateTo(spans.First().Snapshot, SpanTrackingMode.EdgeExclusive)
                        );

                    foreach (var span in NormalizedSnapshotSpanCollection.Overlap(spans, _definitionSpans))
                        yield return new TagSpan<FrameNameDefinitionTag>(span, FrameNameDefinitionTag.Instance);
                }
            }


            private void HandleViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
            {
                if (e.NewSnapshot != e.OldSnapshot)
                {
                    UpdateAtCaretPosition(View.Caret.Position);
                }
            }

            private void HandleCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
            {
                UpdateAtCaretPosition(e.NewPosition);
            }

            private void UpdateAtCaretPosition(CaretPosition caretPosition)
            {
                SnapshotPoint? point = caretPosition.Point.GetPoint(SourceBuffer, caretPosition.Affinity);
                if (point == null)
                    return;

                NormalizedSnapshotSpanCollection newHighlightedSpans = null;
                NormalizedSnapshotSpanCollection newDefinitionSpans = null;

                this.HtmlDocument.HtmlEditorTree.GetPositionElement(point.Value.Position, out ElementNode element, out AttributeNode attribute);

                if ((attribute?.ValueRangeUnquoted.Contains(point.Value.Position) ?? false) ||
                    attribute?.ValueRangeUnquoted.End == point.Value.Position)
                {
                    if (
                        FrameElementsWithName.Any(n => element?.Name?.Equals(n, StringComparison.InvariantCultureIgnoreCase) ?? false) &&
                        (attribute.Name?.Equals("name", StringComparison.InvariantCultureIgnoreCase) ?? false))
                    {
                        // find definitions
                        string name = attribute.Value;
                        newDefinitionSpans = new NormalizedSnapshotSpanCollection(FindDefinitions(name, point.Value.Snapshot));

                        // find references
                        newHighlightedSpans = new NormalizedSnapshotSpanCollection(FindReferences(name, point.Value.Snapshot));
                    }
                    else if (
                        ElementsWithTarget.Any(n => element?.Name?.Equals(n, StringComparison.InvariantCultureIgnoreCase) ?? false) &&
                        (attribute.Name?.Equals("target", StringComparison.InvariantCultureIgnoreCase) ?? false)
                    )
                    {
                        if (!PredefinedFrameNames.Contains(attribute.Value))
                        {
                            // find definitions
                            string name = attribute.Value;
                            newDefinitionSpans = new NormalizedSnapshotSpanCollection(FindDefinitions(name, point.Value.Snapshot));

                            // find references
                            newHighlightedSpans = new NormalizedSnapshotSpanCollection(FindReferences(name, point.Value.Snapshot));
                        }
                    }
                }

                _highlightedSpans = newHighlightedSpans;
                _definitionSpans = newDefinitionSpans;

                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
            }

            private IReadOnlyCollection<SnapshotSpan> FindDefinitions(string name, ITextSnapshot snapshot)
            {
                ICollection<AttributeNode> attributes = new List<AttributeNode>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((element, _) =>
                {
                    if (!FrameElementsWithName.Any(n => element?.Name?.Equals(n, StringComparison.InvariantCultureIgnoreCase) ?? false))
                        return true;

                    var attr = element.GetAttribute("name", ignoreCase: true);
                    if (attr?.Value == name)
                        attributes.Add(attr);

                    return true;
                }, null);

                return (
                    from attr in attributes
                    select new SnapshotSpan(snapshot, attr.ValueRangeUnquoted.Start, attr.ValueRangeUnquoted.Length)
                ).ToList();
            }
            
            private IReadOnlyCollection<SnapshotSpan> FindReferences(string name, ITextSnapshot snapshot)
            {
                ICollection<AttributeNode> attributes = new List<AttributeNode>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((element, _) =>
                {
                    if (!ElementsWithTarget.Any(n => element.Name?.Equals(n, StringComparison.InvariantCultureIgnoreCase) ?? false))
                        return true;

                    var attr = element.GetAttribute("target", ignoreCase: true);
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
