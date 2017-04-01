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
    internal class HtmlIdReferenceTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            HtmlEditorDocument document = HtmlEditorDocument.TryFromTextBuffer(buffer);

            if (document == null)
                return null;

            return new HtmlIdReferenceTagger(textView, buffer, document) as ITagger<T>;
        }


        private sealed class HtmlIdDefinitionTag : TextMarkerTag
        {
            public HtmlIdDefinitionTag()
                : base("MarkerFormatDefinition/HighlightedDefinition")
            { }

            public static readonly HtmlIdDefinitionTag Instance = new HtmlIdDefinitionTag();
        }

        private sealed class HtmlIdReferenceTag : TextMarkerTag
        {
            public HtmlIdReferenceTag()
                : base("MarkerFormatDefinition/HighlightedReference")
            { }

            public static readonly HtmlIdReferenceTag Instance = new HtmlIdReferenceTag();
        }


        private class HtmlIdReferenceTagger : ITagger<TextMarkerTag>
        {
            public HtmlIdReferenceTagger(ITextView view, ITextBuffer sourceBuffer, HtmlEditorDocument document)
            {
                this.View = view;
                this.SourceBuffer = sourceBuffer;
                this.View.Caret.PositionChanged += HandleCaretPositionChanged;
                this.View.LayoutChanged += HandleViewLayoutChanged;
                this.HtmlDocument = document;
            }

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
                        yield return new TagSpan<HtmlIdReferenceTag>(span, HtmlIdReferenceTag.Instance);
                }

                if (_definitionSpans?.Any() ?? false)
                {
                    if (spans.First().Snapshot != _definitionSpans.First().Snapshot)
                        _definitionSpans = new NormalizedSnapshotSpanCollection(
                            from span in _definitionSpans
                            select span.TranslateTo(spans.First().Snapshot, SpanTrackingMode.EdgeExclusive)
                        );

                    foreach (var span in NormalizedSnapshotSpanCollection.Overlap(spans, _definitionSpans))
                        yield return new TagSpan<HtmlIdDefinitionTag>(span, HtmlIdDefinitionTag.Instance);
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

                this.HtmlDocument.HtmlEditorTree.GetPositionElement(point.Value.Position, out _, out AttributeNode attribute);

                if ((attribute?.ValueRangeUnquoted.Contains(point.Value.Position) ?? false)
                    || (attribute?.ValueRangeUnquoted.End == point.Value.Position))
                {
                    if (new string[] { "id", "for", "aria-labelledby", "aria-describedby", "aria-controls" }.Any(n =>
                        attribute.Name?.Equals(n, StringComparison.InvariantCultureIgnoreCase) ?? false
                    ))
                    {
                        // find definitions
                        string id = attribute.Value;
                        newDefinitionSpans = new NormalizedSnapshotSpanCollection(FindDefinitions(id, point.Value.Snapshot));

                        // find references
                        newHighlightedSpans = new NormalizedSnapshotSpanCollection(FindReferences(id, point.Value.Snapshot));
                    }
                    else if (attribute.Name?.Equals("href", StringComparison.InvariantCultureIgnoreCase) ?? false)
                    {
                        if (attribute.Value?.StartsWith("#") ?? false)
                        {
                            // find definitions
                            string id = attribute.Value.Substring(1);
                            newDefinitionSpans = new NormalizedSnapshotSpanCollection(FindDefinitions(id, point.Value.Snapshot));

                            // find references
                            newHighlightedSpans = new NormalizedSnapshotSpanCollection(FindReferences(id, point.Value.Snapshot));
                        }
                    }
                }

                _highlightedSpans = newHighlightedSpans;
                _definitionSpans = newDefinitionSpans;

                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
            }

            private IReadOnlyCollection<SnapshotSpan> FindDefinitions(string id, ITextSnapshot snapshot)
            {
                ICollection<AttributeNode> attributes = new List<AttributeNode>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((e, p) =>
                {
                    var attr = e.GetAttribute("id", true);
                    if (attr?.Value == id)
                        attributes.Add(attr);

                    return true;
                }, null);

                return (
                    from attr in attributes
                    select new SnapshotSpan(snapshot, attr.ValueRangeUnquoted.Start, attr.ValueRangeUnquoted.Length)
                ).ToList();
            }

            private IReadOnlyCollection<SnapshotSpan> FindReferences(string id, ITextSnapshot snapshot)
            {
                return FindForReferences(id, snapshot)
                    .Union(FindHrefReferences(id, snapshot))
                    .Union(FindAriaReferences(id, snapshot))
                    .ToList();
            }

            private IReadOnlyCollection<SnapshotSpan> FindForReferences(string id, ITextSnapshot snapshot)
            {
                ICollection<AttributeNode> attributes = new List<AttributeNode>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((e, p) =>
                {
                    if (!e.Name.Equals("label", StringComparison.InvariantCultureIgnoreCase))
                        return true;

                    var attr = e.GetAttribute("for", true);
                    if (attr?.Value == id)
                        attributes.Add(attr);

                    return true;
                }, null);

                return (
                    from attr in attributes
                    select new SnapshotSpan(snapshot, attr.ValueRangeUnquoted.Start, attr.ValueRangeUnquoted.Length)
                ).ToList();
            }

            private IReadOnlyCollection<SnapshotSpan> FindAriaReferences(string id, ITextSnapshot snapshot)
            {
                ICollection<AttributeNode> attributes = new List<AttributeNode>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((e, p) =>
                {
                    var attr = e.GetAttribute("aria-labelledby", true);
                    if (attr?.Value == id)
                        attributes.Add(attr);

                    attr = e.GetAttribute("aria-describedby", true);
                    if (attr?.Value == id)
                        attributes.Add(attr);

                    attr = e.GetAttribute("aria-controls", true);
                    if (attr?.Value == id)
                        attributes.Add(attr);

                    return true;
                }, null);

                return (
                    from attr in attributes
                    select new SnapshotSpan(snapshot, attr.ValueRangeUnquoted.Start, attr.ValueRangeUnquoted.Length)
                ).ToList();
            }

            private IReadOnlyCollection<SnapshotSpan> FindHrefReferences(string id, ITextSnapshot snapshot)
            {
                string anchor = $"#{id}";

                ICollection<AttributeNode> attributes = new List<AttributeNode>();
                this.HtmlDocument.HtmlEditorTree.RootNode.Accept((e, p) =>
                {
                    if (!e.Name.Equals("a", StringComparison.InvariantCultureIgnoreCase))
                        return true;

                    var attr = e.GetAttribute("href", true);
                    if (attr?.Value == anchor)
                        attributes.Add(attr);

                    return true;
                }, null);

                return (
                    from attr in attributes
                    select new SnapshotSpan(snapshot, attr.ValueRangeUnquoted.Start + 1, attr.ValueRangeUnquoted.Length - 1)
                ).ToList();
            }
        }
    }
}
