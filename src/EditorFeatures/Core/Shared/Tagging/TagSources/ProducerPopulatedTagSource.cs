// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Shared.Tagging
{
    /// <summary>
    /// <para>The <see cref="ProducerPopulatedTagSource{TTag}"/> is the core part of our asynchronous
    /// tagging infrastructure. It is the coordinator between <see cref="IAsynchronousTaggerDataSource{TTag}.ProduceTagsAsync"/>s,
    /// <see cref="ITaggerEventSource"/>s, and <see cref="ITagger{T}"/>s.</para>
    /// 
    /// <para>The <see cref="ProducerPopulatedTagSource{TTag}"/> is the type that actually owns the
    /// list of cached tags. When an <see cref="ITaggerEventSource"/> says tags need to be  recomputed,
    /// the tag source starts the computation and calls <see cref="IAsynchronousTaggerDataSource{TTag}.ProduceTagsAsync"/> to build
    /// the new list of tags. When that's done, the tags are stored in <see cref="CachedTagTrees"/>. The 
    /// tagger, when asked for tags from the editor, then returns the tags that are stored in 
    /// <see cref="CachedTagTrees"/></para>
    /// 
    /// <para>There is a one-to-many relationship between <see cref="ProducerPopulatedTagSource{TTag}"/>s
    /// and <see cref="ITagger{T}"/>s. Special cases, like reference highlighting (which processes multiple
    /// subject buffers at once) have their own providers and tag source derivations.</para>
    /// </summary>
    /// <typeparam name="TTag">The type of tag.</typeparam>
    internal partial class ProducerPopulatedTagSource<TTag> : TagSource<TTag>
        where TTag : ITag
    {
        #region Fields that can be accessed from either thread
        private readonly IAsynchronousTaggerDataSource<TTag> _dataSource;

        private IEqualityComparer<ITagSpan<TTag>> _tagSpanComparer;
        #endregion

        #region Fields that can only be accessed from the foreground thread

        private readonly ITextView _textViewOpt;

        /// <summary>
        /// Our tagger event source that lets us know when we should call into the tag producer for
        /// new tags.
        /// </summary>
        private readonly ITaggerEventSource _eventSource;

        /// <summary>
        /// During the time that we are paused from updating the UI, we will use these tags instead.
        /// </summary>
        private ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> _previousCachedTagTrees;

        /// <summary>
        /// accumulated text changes since last tag calculation
        /// </summary>
        private TextChangeRange? _accumulatedTextChanges_doNotAccessDirectly;
        private ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> _cachedTagTrees_doNotAccessDirectly;
        #endregion

        public ProducerPopulatedTagSource(
            ITextView textViewOpt,
            ITextBuffer subjectBuffer,
            IAsynchronousTaggerDataSource<TTag> dataSource,
            IAsynchronousOperationListener asyncListener,
            IForegroundNotificationService notificationService)
                : base(subjectBuffer, notificationService, asyncListener)
        {
            if (dataSource.SpanTrackingMode == SpanTrackingMode.Custom)
            {
                throw new ArgumentException("SpanTrackingMode.Custom not allowed.", "spanTrackingMode");
            }

            _textViewOpt = textViewOpt;
            _dataSource = dataSource;

            _tagSpanComparer = new TagSpanComparer<TTag>(this.TagComparer);

            this.CachedTagTrees = ImmutableDictionary.Create<ITextBuffer, TagSpanIntervalTree<TTag>>();

            _eventSource = dataSource.CreateEventSource(textViewOpt, subjectBuffer);

            AttachEventHandlersAndStart();
        }

        private IEqualityComparer<TTag> TagComparer =>
            _dataSource.TagComparer ?? EqualityComparer<TTag>.Default;

        protected TextChangeRange? AccumulatedTextChanges
        {
            get
            {
                this.WorkQueue.AssertIsForeground();
                return _accumulatedTextChanges_doNotAccessDirectly;
            }

            set
            {
                this.WorkQueue.AssertIsForeground();
                _accumulatedTextChanges_doNotAccessDirectly = value;
            }
        }

        protected ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> CachedTagTrees
        {
            get
            {
                this.WorkQueue.AssertIsForeground();
                return _cachedTagTrees_doNotAccessDirectly;
            }

            set
            {
                this.WorkQueue.AssertIsForeground();
                _cachedTagTrees_doNotAccessDirectly = value;
            }
        }

        private void AttachEventHandlersAndStart()
        {
            this.WorkQueue.AssertIsForeground();

            _eventSource.Changed += OnChanged;
            _eventSource.UIUpdatesResumed += OnUIUpdatesResumed;
            _eventSource.UIUpdatesPaused += OnUIUpdatesPaused;

            if (_dataSource.TextChangeBehavior.HasFlag(TaggerTextChangeBehavior.TrackTextChanges))
            {
                this.SubjectBuffer.Changed += OnSubjectBufferChanged;
            }

            if (_dataSource.CaretChangeBehavior.HasFlag(TaggerCaretChangeBehavior.RemoveAllTagsOnCaretMoveOutsideOfTag))
            {
                if (_textViewOpt == null)
                {
                    throw new ArgumentException(
                        nameof(_dataSource.CaretChangeBehavior) + " can only be specified for an " + nameof(IViewTaggerProvider));
                }

                _textViewOpt.Caret.PositionChanged += OnCaretPositionChanged;
            }

            // Tell the interaction object to start issuing events.
            _eventSource.Connect();
        }

        protected override void Disconnect()
        {
            this.WorkQueue.AssertIsForeground();

            base.Disconnect();

            // Tell the interaction object to stop issuing events.
            _eventSource.Disconnect();

            if (_dataSource.CaretChangeBehavior.HasFlag(TaggerCaretChangeBehavior.RemoveAllTagsOnCaretMoveOutsideOfTag))
            {
                this._textViewOpt.Caret.PositionChanged -= OnCaretPositionChanged;
            }

            if (_dataSource.TextChangeBehavior.HasFlag(TaggerTextChangeBehavior.TrackTextChanges))
            {
                this.SubjectBuffer.Changed -= OnSubjectBufferChanged;
            }

            _eventSource.UIUpdatesPaused -= OnUIUpdatesPaused;
            _eventSource.UIUpdatesResumed -= OnUIUpdatesResumed;
            _eventSource.Changed -= OnChanged;
        }

        private void OnUIUpdatesPaused(object sender, EventArgs e)
        {
            this.WorkQueue.AssertIsForeground();
            _previousCachedTagTrees = CachedTagTrees;

            RaisePaused();
        }

        private void OnUIUpdatesResumed(object sender, EventArgs e)
        {
            this.WorkQueue.AssertIsForeground();
            _previousCachedTagTrees = null;

            RaiseResumed();
        }

        private void OnChanged(object sender, TaggerEventArgs e)
        {
            using (var token = this.Listener.BeginAsyncOperation("OnChanged"))
            {
                // First, cancel any previous requests (either still queued, or started).  We no longer
                // want to continue it if new changes have come in.
                this.WorkQueue.CancelCurrentWork();

                // We don't currently have a request issued to re-compute our tags. Issue it for some
                // time in the future.
                RecalculateTagsOnChanged(e);
            }
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            this.AssertIsForeground();

            Debug.Assert(_dataSource.CaretChangeBehavior.HasFlag(TaggerCaretChangeBehavior.RemoveAllTagsOnCaretMoveOutsideOfTag));

            var caret = GetCaretPoint();
            if (caret.HasValue)
            {
                // If it changed position and we're still in a tag, there's nothing more to do
                var currentTags = GetTagIntervalTreeForBuffer(caret.Value.Snapshot.TextBuffer);
                if (currentTags != null && currentTags.GetIntersectingSpans(new SnapshotSpan(caret.Value, 0)).Count > 0)
                {
                    // Caret is inside a tag.  No need to do anything.
                    return;
                }
            }

            RemoveAllTags();
        }

        private void RemoveAllTags()
        {
            this.AssertIsForeground();

            var oldTagTrees = this.CachedTagTrees;
            this.CachedTagTrees = ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>>.Empty;

            var snapshot = this.SubjectBuffer.CurrentSnapshot;
            var oldTagTree = GetTagTree(snapshot, oldTagTrees);
            var newTagTree = GetTagTree(snapshot, this.CachedTagTrees);

            var difference = ComputeDifference(snapshot, newTagTree, oldTagTree);
            RaiseTagsChanged(snapshot.TextBuffer, difference);
        }

        protected SnapshotPoint? GetCaretPoint()
        {
            this.AssertIsForeground();
            return _dataSource.GetCaretPoint(_textViewOpt, SubjectBuffer) ?? _textViewOpt?.GetCaretPoint(SubjectBuffer);
        }

        private void OnSubjectBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            this.WorkQueue.AssertIsForeground();
            UpdateTagsForTextChange(e);
            AccumulateTextChanges(e);
        }

        private void AccumulateTextChanges(TextContentChangedEventArgs contentChanged)
        {
            this.WorkQueue.AssertIsForeground();
            var contentChanges = contentChanged.Changes;
            var count = contentChanges.Count;

            switch (count)
            {
                case 0:
                    return;

                case 1:
                    // PERF: Optimize for the simple case of typing on a line.
                    {
                        var c = contentChanges[0];
                        var textChangeRange = new TextChangeRange(new TextSpan(c.OldSpan.Start, c.OldSpan.Length), c.NewLength);
                        this.AccumulatedTextChanges = this.AccumulatedTextChanges == null
                            ? textChangeRange
                            : this.AccumulatedTextChanges.Accumulate(SpecializedCollections.SingletonEnumerable(textChangeRange));
                    }
                    break;

                default:
                    var textChangeRanges = new TextChangeRange[count];
                    for (int i = 0; i < count; i++)
                    {
                        var c = contentChanges[i];
                        textChangeRanges[i] = new TextChangeRange(new TextSpan(c.OldSpan.Start, c.OldSpan.Length), c.NewLength);
                    }

                    this.AccumulatedTextChanges = this.AccumulatedTextChanges.Accumulate(textChangeRanges);
                    break;
            }
        }

        private void UpdateTagsForTextChange(TextContentChangedEventArgs e)
        {
            this.WorkQueue.AssertIsForeground();

            if (_dataSource.TextChangeBehavior.HasFlag(TaggerTextChangeBehavior.RemoveAllTags))
            {
                this.RemoveAllTags();
                return;
            }

            // Don't bother going forward if we're not going adjust any tags based on edits.
            if (_dataSource.TextChangeBehavior.HasFlag(TaggerTextChangeBehavior.RemoveTagsThatIntersectEdits))
            {
                RemoveTagsThatIntersectEdit(e);
                return;
            }
        }

        private void RemoveTagsThatIntersectEdit(TextContentChangedEventArgs e)
        {
            if (!e.Changes.Any())
            {
                return;
            }

            // We might be able to steal the cached tags from another tag source
            if (TryStealTagsFromRelatedTagSource(e))
            {
                return;
            }

            var buffer = e.After.TextBuffer;
            TagSpanIntervalTree<TTag> treeForBuffer;
            if (!this.CachedTagTrees.TryGetValue(buffer, out treeForBuffer))
            {
                return;
            }

            var tagsToRemove = e.Changes.SelectMany(c => treeForBuffer.GetIntersectingSpans(new SnapshotSpan(e.After, c.NewSpan)));
            if (!tagsToRemove.Any())
            {
                return;
            }

            var allTags = treeForBuffer.GetSpans(e.After).ToList();
            var newTagTree = new TagSpanIntervalTree<TTag>(
                buffer,
                treeForBuffer.SpanTrackingMode,
                allTags.Except(tagsToRemove, _tagSpanComparer));

            var snapshot = e.After;

            var oldTagTrees = this.CachedTagTrees;
            this.CachedTagTrees = oldTagTrees.SetItem(snapshot.TextBuffer, newTagTree);

            // Grab our old tags. We might not have any, so in this case we'll just pretend it's
            // empty
            var oldTagTree = GetTagTree(snapshot, oldTagTrees);

            var difference = ComputeDifference(snapshot, newTagTree, oldTagTree);
            RaiseTagsChanged(snapshot.TextBuffer, difference);
        }

        private TagSpanIntervalTree<TTag> GetTagTree(ITextSnapshot snapshot, ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> tagTrees)
        {
            TagSpanIntervalTree<TTag> tagTree = null;
            return tagTrees.TryGetValue(snapshot.TextBuffer, out tagTree)
                ? tagTree
                : new TagSpanIntervalTree<TTag>(snapshot.TextBuffer, _dataSource.SpanTrackingMode);
        }

        private bool TryStealTagsFromRelatedTagSource(TextContentChangedEventArgs e)
        {
            // see bug 778731
#if INTERACTIVE 
            // If we don't have a way to find the related buffer, we're done immediately
            if (bufferToRelatedTagSource == null)
            {
                return false;
            }

            // We can only steal tags if we know where the edit came from, so do we?
            var editTag = e.EditTag as RestoreHistoryEditTag;

            if (editTag == null)
            {
                return false;
            }

            var originalSpan = editTag.OriginalSpan;

            var relatedTagSource = bufferToRelatedTagSource(originalSpan.Snapshot.TextBuffer);
            if (relatedTagSource == null)
            {
                return false;
            }

            // Reading the other tag source's cached tags is safe, since this field is allowed to be
            // accessed from multiple threads and is immutable. We still need to have a local copy
            // though to play it safe and be a good citizen (well, as good as a citizen that's about
            // to steal something can be...)
            var relatedCachedTags = relatedTagSource.cachedTags;
            TagSpanIntervalTree<TTag> relatedIntervalTree;

            if (!relatedCachedTags.TryGetValue(originalSpan.Snapshot.TextBuffer, out relatedIntervalTree))
            {
                return false;
            }

            // Excellent! Let's build a new interval tree with these tags mapped to our buffer
            // instead
            var tagsForThisBuffer = from tagSpan in relatedIntervalTree.GetSpans(originalSpan.Snapshot)
                                    where tagSpan.Span.IntersectsWith(originalSpan)
                                    let snapshotSpan = new SnapshotSpan(e.After, tagSpan.SpanStart - originalSpan.Start, tagSpan.Span.Length)
                                    select new TagSpan<TTag>(snapshotSpan, tagSpan.Tag);

            var intervalTreeForThisBuffer = new TagSpanIntervalTree<TTag>(e.After.TextBuffer, relatedIntervalTree.SpanTrackingMode, tagsForThisBuffer);

            // Update our cached tags
            UpdateCachedTagsForBuffer(e.After, intervalTreeForThisBuffer);
            return true;
#else
            return false;
#endif
        }

        /// <summary>
        /// Called on the foreground thread.
        /// </summary>
        protected override void RecomputeTagsForeground()
        {
            this.WorkQueue.AssertIsForeground();

            using (Logger.LogBlock(FunctionId.Tagger_TagSource_RecomputeTags, CancellationToken.None))
            {
                // Stop any existing work we're currently engaged in
                this.WorkQueue.CancelCurrentWork();
                var cancellationToken = this.WorkQueue.CancellationToken;

                var spansToTag = GetSpansAndDocumentsToTag();

                // Make a copy of all the data we need while we're on the foreground.  Then
                // pass it along everywhere needed.  Finally, once new tags have been computed,
                // then we update our state again on the foreground.
                var caretPosition = this.GetCaretPoint();
                var textChangeRange = this.AccumulatedTextChanges;
                var oldTagTrees = this.CachedTagTrees;

                this.WorkQueue.EnqueueBackgroundTask(
                    ct => this.RecomputeTagsAsync(caretPosition, textChangeRange, spansToTag, oldTagTrees, ct),
                    GetType().Name + ".RecomputeTags", cancellationToken);
            }
        }

        protected List<DocumentSnapshotSpan> GetSpansAndDocumentsToTag()
        {
            this.WorkQueue.AssertIsForeground();

            // TODO: Update to tag spans from all related documents.

            var snapshotToDocumentMap = new Dictionary<ITextSnapshot, Document>();
            var spansToTag = _dataSource.GetSpansToTag(_textViewOpt, SubjectBuffer) ?? this.GetFullBufferSpan();
            var spansAndDocumentsToTag = spansToTag.Select(span =>
            {
                Document document = null;
                if (!snapshotToDocumentMap.TryGetValue(span.Snapshot, out document))
                {
                    CheckSnapshot(span.Snapshot);

                    document = span.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
                    snapshotToDocumentMap[span.Snapshot] = document;
                }

                // document can be null if the buffer the given span is part of is not part of our workspace.
                return new DocumentSnapshotSpan(document, span);
            }).ToList();

            return spansAndDocumentsToTag;
        }

        private IList<SnapshotSpan> GetFullBufferSpan()
        {
            // For a standard tagger, the spans to tag is the span of the entire snapshot.
            return new[] { SubjectBuffer.CurrentSnapshot.GetFullSpan() };
        }

        [Conditional("DEBUG")]
        private void CheckSnapshot(ITextSnapshot snapshot)
        {
            var container = snapshot.TextBuffer.AsTextContainer();

            Workspace dummy;
            if (Workspace.TryGetWorkspace(container, out dummy))
            {
                // if the buffer is part of our workspace, it must be the latest.
                Contract.Assert(snapshot.Version.Next == null, "should be on latest snapshot");
            }
        }

        protected ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> ConvertToTagTrees(
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees,
            IEnumerable<ITagSpan<TTag>> newTagSpans,
            IEnumerable<DocumentSnapshotSpan> spansTagged)
        {
            // NOTE: we assume that the following list is already realized and is _not_ lazily
            // computed. It's not clear what the contract is of this API.

            // common case where we only tagged a single range of a document.
            if (spansTagged.IsSingle())
            {
                return ConvertToTagTree(oldTagTrees, newTagSpans, spansTagged.Single().SnapshotSpan);
            }

            // heavy linq case 
            var newTagsByBuffer = newTagSpans.ToLookup(t => t.Span.Snapshot.TextBuffer);
            var spansToInvalidateByBuffer = spansTagged.Select(ss => ss.SnapshotSpan).ToLookup(ss => ss.Snapshot.TextBuffer);

            var buffers = oldTagTrees.Keys.Concat(newTagsByBuffer.Select(g => g.Key))
                                          .Concat(spansTagged.Select(dss => dss.SnapshotSpan.Snapshot.TextBuffer))
                                          .Distinct();

            // Walk through each relevant buffer and decide what the interval tree should be
            // for that buffer.  In general this will work by keeping around old tags that
            // weren't in the range that was re-tagged, and merging them with the new tags
            // produced for the range that was re-tagged.
            var newTagTrees = ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>>.Empty;
            foreach (var buffer in buffers)
            {
                var newTagTree = ComputeNewTagTree(oldTagTrees, buffer, newTagsByBuffer[buffer], spansToInvalidateByBuffer[buffer]);
                if (newTagTree != null)
                {
                    newTagTrees = newTagTrees.Add(buffer, newTagTree);
                }
            }

            return newTagTrees;
        }

        private TagSpanIntervalTree<TTag> ComputeNewTagTree(
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees,
            ITextBuffer textBuffer,
            IEnumerable<ITagSpan<TTag>> newTags,
            IEnumerable<SnapshotSpan> spansToInvalidate)
        {
            var noNewTags = newTags.IsEmpty();
            var noSpansToInvalidate = spansToInvalidate.IsEmpty();

            TagSpanIntervalTree<TTag> oldTagTree;
            oldTagTrees.TryGetValue(textBuffer, out oldTagTree);

            if (oldTagTree == null)
            {
                if (noNewTags)
                {
                    // We have no new tags, and no old tags either.  No need to store anything
                    // for this buffer.
                    return null;
                }

                // If we don't have any old tags then we just need to reutrn the new tags.
                return new TagSpanIntervalTree<TTag>(textBuffer, _dataSource.SpanTrackingMode, newTags);
            }

            // If we don't have any new tags, and there was nothing to invalidate, then we can 
            // keep whatever old tags we have without doing any additional work.
            if (noNewTags && noSpansToInvalidate)
            {
                return oldTagTree;
            }

            // We either have some new tags, or we have some tags to invalidate.
            // First, determine which of the old tags we want to keep around.
            var snapshot = noNewTags ? spansToInvalidate.First().Snapshot : newTags.First().Span.Snapshot;
            var oldTagsToKeep = noSpansToInvalidate
                ? oldTagTree.GetSpans(snapshot)
                : GetNonIntersectingTagSpans(spansToInvalidate, oldTagTree);

            // Then union those with the new tags to produce the final tag tree.
            var finalTags = oldTagsToKeep.Concat(newTags);
            return new TagSpanIntervalTree<TTag>(textBuffer, _dataSource.SpanTrackingMode, finalTags);
        }

        private IEnumerable<ITagSpan<TTag>> GetNonIntersectingTagSpans(IEnumerable<SnapshotSpan> spansToInvalidate, TagSpanIntervalTree<TTag> oldTagTree)
        {
            var snapshot = spansToInvalidate.First().Snapshot;

            var tagSpansToInvalidate = new List<ITagSpan<TTag>>(
                spansToInvalidate.SelectMany(ss => oldTagTree.GetIntersectingSpans(ss)));

            return oldTagTree.GetSpans(snapshot).Except(tagSpansToInvalidate, _tagSpanComparer);
        }

        private ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> ConvertToTagTree(
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees,
            IEnumerable<ITagSpan<TTag>> newTagSpans,
            SnapshotSpan spanTagged)
        {
            var newTagsByBuffer = newTagSpans.ToLookup(t => t.Span.Snapshot.TextBuffer);
            var allBuffers = oldTagTrees.Keys.Concat(newTagsByBuffer.Select(g => g.Key))
                                             .Concat(spanTagged.Snapshot.TextBuffer).Distinct();

            var newTagTrees = ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>>.Empty;
            foreach (var buffer in allBuffers)
            {
                var newTagTree = ComputeNewTagTree(spanTagged, oldTagTrees, buffer, newTagsByBuffer[buffer]);
                if (newTagTree != null)
                {
                    newTagTrees = newTagTrees.Add(buffer, newTagTree);
                }
            }

            return newTagTrees;
        }

        /// <summary>
        /// This is the same as <see cref="ComputeNewTagTree(ImmutableDictionary{ITextBuffer, TagSpanIntervalTree{TTag}}, ITextBuffer, IEnumerable{ITagSpan{TTag}}, IEnumerable{SnapshotSpan})"/>,
        /// just optimized for the case where we were tagging a single snapshot span.
        /// </summary>
        private TagSpanIntervalTree<TTag> ComputeNewTagTree(
            SnapshotSpan spanToInvalidate,
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees,
            ITextBuffer textBuffer,
            IEnumerable<ITagSpan<TTag>> newTags)
        {
            TagSpanIntervalTree<TTag> oldTagTree;
            oldTagTrees.TryGetValue(textBuffer, out oldTagTree);

            if (oldTagTree == null)
            {
                if (newTags.IsEmpty())
                {
                    // We have no new tags, and no old tags either.  No need to store anything
                    // for this buffer.
                    return null;
                }

                // If we don't have any old tags then we just need to return the new tags.
                return new TagSpanIntervalTree<TTag>(textBuffer, _dataSource.SpanTrackingMode, newTags);
            }

            // If we're examining a text buffer other than the one we tagged.
            if (textBuffer != spanToInvalidate.Snapshot.TextBuffer)
            {
                // If we have no new tags produced for it, we can just use the olg tags for is.
                if (newTags.IsEmpty())
                {
                    return oldTagTree;
                }

                // Otherwise, merge the old and new tags.
                var finalTags = oldTagTree.GetSpans(newTags.First().Span.Snapshot).Concat(newTags);
                return new TagSpanIntervalTree<TTag>(textBuffer, _dataSource.SpanTrackingMode, finalTags);
            }
            else
            {
                // We're examining the buffer we tagged.  If we tagged just a portion in the middle
                // then produce the new set of tags by taking the old set before the retagged portion,
                // then the new tags, then the old tags after the retagged portion.  This keeps the
                // tags mostly sorted, which helps with producing an accurate diff.

                // These lists only live as long as this method.  So it's fine for us to do whatever
                // we want with them (including mutating them) inside this body.
                var beforeTagsToKeep = new List<ITagSpan<TTag>>();
                var afterTagsToKeep = new List<ITagSpan<TTag>>();
                GetTagsToKeep(oldTagTrees, spanToInvalidate, beforeTagsToKeep, afterTagsToKeep);

                // Then union those with the new tags to produce the final tag tree.
                var finalTags = beforeTagsToKeep;
                finalTags.AddRange(newTags);
                finalTags.AddRange(afterTagsToKeep);

                return new TagSpanIntervalTree<TTag>(textBuffer, _dataSource.SpanTrackingMode, finalTags);
            }
        }

        /// <summary>
        /// Returns all that tags that fully precede 'spanToInvalidate' and all the tags that
        /// fully follow it.  All the tag spans are normalized to the snapshot passed in through
        /// 'spanToInvalidate'.
        /// </summary>
        private void GetTagsToKeep(
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees,
            SnapshotSpan spanTagged,
            List<ITagSpan<TTag>> beforeTags,
            List<ITagSpan<TTag>> afterTags)
        {
            var fullRefresh = spanTagged.Length == spanTagged.Snapshot.Length;
            if (fullRefresh)
            {
                return;
            }

            // we actually have span to invalidate from old tree
            TagSpanIntervalTree<TTag> treeForBuffer;
            if (!oldTagTrees.TryGetValue(spanTagged.Snapshot.TextBuffer, out treeForBuffer))
            {
                return;
            }

            treeForBuffer.GetNonIntersectingSpans(spanTagged, beforeTags, afterTags);
        }

        protected virtual async Task RecomputeTagsAsync(
            SnapshotPoint? caretPosition,
            TextChangeRange? textChangeRange,
            IEnumerable<DocumentSnapshotSpan> spansToTag,
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var newTagSpans = SpecializedCollections.EmptyEnumerable<ITagSpan<TTag>>();

            var context = new AsynchronousTaggerContext<TTag>(
                spansToTag, caretPosition, textChangeRange, oldTagTrees, cancellationToken);
            await _dataSource.ProduceTagsAsync(context).ConfigureAwait(false);

            ProcessContext(oldTagTrees, context);
        }

        private ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> ProcessContext(
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees, 
            AsynchronousTaggerContext<TTag> context)
        {
            var spansTagged = context._spansTagged;
            var newTagTrees = ConvertToTagTrees(oldTagTrees, context.tagSpans, spansTagged);

            ProcessNewTagTrees(spansTagged, oldTagTrees, newTagTrees, context.CancellationToken);

            return newTagTrees;
        }

        protected virtual void ProcessNewTagTrees(
            IEnumerable<DocumentSnapshotSpan> spansTagged,
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> oldTagTrees,
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> newTagTrees,
            CancellationToken cancellationToken)
        {
            var bufferToChanges = new Dictionary<ITextBuffer, NormalizedSnapshotSpanCollection>();
            using (Logger.LogBlock(FunctionId.Tagger_TagSource_ProcessNewTags, cancellationToken))
            {
                foreach (var latestBuffer in newTagTrees.Keys)
                {
                    var snapshot = spansTagged.First(s => s.SnapshotSpan.Snapshot.TextBuffer == latestBuffer).SnapshotSpan.Snapshot;

                    if (oldTagTrees.ContainsKey(latestBuffer))
                    {
                        var difference = ComputeDifference(snapshot, newTagTrees[latestBuffer], oldTagTrees[latestBuffer]);
                        bufferToChanges[latestBuffer] = difference;
                    }
                    else
                    {
                        // It's a new buffer, so report all spans are changed
                        var allSpans = new NormalizedSnapshotSpanCollection(newTagTrees[latestBuffer].GetSpans(snapshot).Select(t => t.Span));
                        bufferToChanges[latestBuffer] = allSpans;
                    }
                }

                foreach (var oldBuffer in oldTagTrees.Keys)
                {
                    if (!newTagTrees.ContainsKey(oldBuffer))
                    {
                        // This buffer disappeared, so let's notify that the old tags are gone
                        var allSpans = new NormalizedSnapshotSpanCollection(oldTagTrees[oldBuffer].GetSpans(oldBuffer.CurrentSnapshot).Select(t => t.Span));
                        bufferToChanges[oldBuffer] = allSpans;
                    }
                }
            }


            RegisterNotification(() => UpdateStateAndReportChanges(newTagTrees, bufferToChanges), 0, cancellationToken);
        }

        private void UpdateStateAndReportChanges(
            ImmutableDictionary<ITextBuffer, TagSpanIntervalTree<TTag>> newTagTrees,
            Dictionary<ITextBuffer, NormalizedSnapshotSpanCollection> bufferToChanges)
        {
            this.WorkQueue.AssertIsForeground();

            // Now that we're back on the UI thread, we can safely update our state with
            // what we've computed.  There is no concern with race conditions now.  For 
            // example, say that another change happened between the time when we 
            // registered for UpdateStateAndReportChanges and now.  If we processed that
            // notification (on the UI thread) first, then our cancellation token would 
            // have been been triggered, and the foreground notification service would not 
            // call into this method. 
            // 
            // If, instead, we did get called into, then we will update our instance state.
            // Then when the foreground notification service runs RecomputeTagsForeground
            // it will see that state and use it as the new basis on which to compute diffs
            // and whatnot.
            this.CachedTagTrees = newTagTrees;
            this.AccumulatedTextChanges = null;

            // Note: we're raising changes here on the UI thread.  However, this doesn't actually
            // mean we'll be notifying the editor.  Instead, these will be batched up in the 
            // AsynchronousTagger's BatchChangeNotifier.  If we tell it about enough changes
            // to a file, it will colaesce them into one large change to keep chattyness with
            // the editor down.
            RaiseTagsChanged(bufferToChanges);
        }

        private NormalizedSnapshotSpanCollection ComputeDifference(
            ITextSnapshot snapshot,
            TagSpanIntervalTree<TTag> latestSpans,
            TagSpanIntervalTree<TTag> previousSpans)
        {
            return new NormalizedSnapshotSpanCollection(
                Difference(latestSpans.GetSpans(snapshot), previousSpans.GetSpans(snapshot), new DiffSpanComparer(this.TagComparer)));
        }

        /// <summary>
        /// Returns the TagSpanIntervalTree containing the tags for the given buffer. If no tags
        /// exist for the buffer at all, null is returned.
        /// </summary>
        public override ITagSpanIntervalTree<TTag> GetTagIntervalTreeForBuffer(ITextBuffer buffer)
        {
            this.WorkQueue.AssertIsForeground();

            // If we're currently pausing updates to the UI, then just use the tags we had before we
            // were paused so that nothing changes.  
            //
            // We're on the UI thread, so it's safe to access these variables.
            var map = _previousCachedTagTrees ?? this.CachedTagTrees;

            TagSpanIntervalTree<TTag> tags;
            if (!map.TryGetValue(buffer, out tags))
            {
                if (_dataSource.ComputeTagsSynchronouslyIfNoAsynchronousComputationHasCompleted && _previousCachedTagTrees == null)
                {
                    // We can cancel any background computations currently happening
                    this.WorkQueue.CancelCurrentWork();

                    var spansToTag = GetSpansAndDocumentsToTag();

                    // Safe to access _cachedTagTrees here.  We're on the UI thread.
                    var oldTagTrees = this.CachedTagTrees;

                    // TODO(cyrusn): Should we do this under a threaded wait dialog.  That way the
                    // use can cancel out if this takes a long time.

                    var context = new AsynchronousTaggerContext<TTag>(
                        spansToTag, GetCaretPoint(), this.AccumulatedTextChanges, oldTagTrees, CancellationToken.None);
                    _dataSource.ProduceTagsAsync(context).Wait();

                    var newTagTrees = ProcessContext(oldTagTrees, context);

                    newTagTrees.TryGetValue(buffer, out tags);
                }
            }

            return tags;
        }

        private class DiffSpanComparer : IDiffSpanComparer<ITagSpan<TTag>>
        {
            private readonly IEqualityComparer<TTag> _comparer;

            public DiffSpanComparer(IEqualityComparer<TTag> comparer)
            {
                _comparer = comparer;
            }

            public bool IsDefault(ITagSpan<TTag> tagSpan)
            {
                return tagSpan == null;
            }

            public SnapshotSpan GetSpan(ITagSpan<TTag> tagSpan)
            {
                return tagSpan.Span;
            }

            public bool Equals(ITagSpan<TTag> tagSpan1, ITagSpan<TTag> tagSpan2)
            {
                return _comparer.Equals(tagSpan1.Tag, tagSpan2.Tag);
            }
        }
    }
}
