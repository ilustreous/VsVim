﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using EditorUtils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Vim;
using IServiceProvider = System.IServiceProvider;

namespace VsVim.Implementation
{
    [Export(typeof(ITextManager))]
    internal sealed class TextManager : ITextManager
    {
        private readonly IVsAdapter _vsAdapter;
        private readonly IVsTextManager _textManager;
        private readonly RunningDocumentTable _table;
        private readonly IServiceProvider _serviceProvider;
        private readonly ITextDocumentFactoryService _textDocumentFactoryService;
        private readonly ITextBufferFactoryService _textBufferFactoryService;

        internal IEnumerable<ITextBuffer> TextBuffers
        {
            get
            {
                var list = new List<ITextBuffer>();
                foreach (var item in _table)
                {
                    ITextBuffer buffer;
                    if (_vsAdapter.GetTextBufferForDocCookie(item.DocCookie).TryGetValue(out buffer))
                    {
                        list.Add(buffer);
                    }
                }
                return list;
            }
        }

        internal IEnumerable<ITextView> TextViews
        {
            get { return TextBuffers.Select(x => GetTextViews(x)).SelectMany(x => x); }
        }

        internal ITextView ActiveTextView
        {
            get
            {
                IVsTextView vsTextView;
                IWpfTextView textView = null;
                ErrorHandler.ThrowOnFailure(_textManager.GetActiveView(0, null, out vsTextView));
                textView = _vsAdapter.EditorAdapter.GetWpfTextView(vsTextView);
                if (textView == null)
                {
                    throw new InvalidOperationException();
                }
                return textView;
            }
        }

        [ImportingConstructor]
        internal TextManager(
            IVsAdapter adapter,
            ITextDocumentFactoryService textDocumentFactoryService,
            ITextBufferFactoryService textBufferFactoryService,
            SVsServiceProvider serviceProvider)
        {
            _vsAdapter = adapter;
            _serviceProvider = serviceProvider;
            _textManager = _serviceProvider.GetService<SVsTextManager, IVsTextManager>();
            _textDocumentFactoryService = textDocumentFactoryService;
            _textBufferFactoryService = textBufferFactoryService;
            _table = new RunningDocumentTable(_serviceProvider);
        }

        internal bool NavigateTo(VirtualSnapshotPoint point)
        {
            var tuple = SnapshotPointUtil.GetLineColumn(point.Position);
            var line = tuple.Item1;
            var column = tuple.Item2;
            var vsBuffer = _vsAdapter.EditorAdapter.GetBufferAdapter(point.Position.Snapshot.TextBuffer);
            var viewGuid = VSConstants.LOGVIEWID_Code;
            var hr = _textManager.NavigateToLineAndColumn(
                vsBuffer,
                ref viewGuid,
                line,
                column,
                line,
                column);
            return ErrorHandler.Succeeded(hr);
        }

        internal Result Save(ITextBuffer textBuffer)
        {
            // In order to save the ITextBuffer we need to get a document cookie for it.  The only way I'm
            // aware of is to use the path moniker which is available for the accompanying ITextDocment 
            // value.  
            //
            // In many types of files (.cs, .vb, .cpp) there is usually a 1-1 mapping between ITextBuffer 
            // and the ITextDocument.  But in any file type where an IProjectionBuffer is common (.js, 
            // .aspx, etc ...) this mapping breaks down.  To get it back we must visit all of the 
            // source buffers for a projection and individually save them
            var result = Result.Success;
            foreach (var sourceBuffer in textBuffer.GetSourceBuffersRecursive())
            {
                // The inert buffer doesn't need to be saved.  It's used as a fake buffer by web applications
                // in order to render projected content
                if (sourceBuffer.ContentType == _textBufferFactoryService.InertContentType)
                {
                    continue;
                }

                var sourceResult = SaveCore(sourceBuffer);
                if (sourceResult.IsError)
                {
                    result = sourceResult;
                }
            }

            return result;
        }

        internal Result SaveCore(ITextBuffer textBuffer)
        {
            ITextDocument textDocument;
            if (!_textDocumentFactoryService.TryGetTextDocument(textBuffer, out textDocument))
            {
                return Result.Error;
            }

            try
            {
                var docCookie = _vsAdapter.GetDocCookie(textDocument).Value;
                var runningDocumentTable = _serviceProvider.GetService<SVsRunningDocumentTable, IVsRunningDocumentTable>();
                ErrorHandler.ThrowOnFailure(runningDocumentTable.SaveDocuments((uint)__VSRDTSAVEOPTIONS.RDTSAVEOPT_ForceSave, null, 0, docCookie));
                return Result.Success;
            }
            catch (Exception e)
            {
                return Result.CreateError(e);
            }
        }

        internal bool CloseView(ITextView textView)
        {
            IVsCodeWindow vsCodeWindow;
            if (!_vsAdapter.GetCodeWindow(textView).TryGetValue(out vsCodeWindow))
            {
                return false;
            }

            if (vsCodeWindow.IsSplit())
            {
                return SendSplit(vsCodeWindow);
            }

            IVsWindowFrame vsWindowFrame;
            if (!_vsAdapter.GetContainingWindowFrame(textView).TryGetValue(out vsWindowFrame))
            {
                return false;
            }

            // It's possible for IVsWindowFrame elements to nest within each other.  When closing we want to 
            // close the actual tab in the editor so get the top most item
            vsWindowFrame = vsWindowFrame.GetTopMost();

            var value = __FRAMECLOSE.FRAMECLOSE_NoSave;
            return ErrorHandler.Succeeded(vsWindowFrame.CloseFrame((uint)value));
        }

        internal bool SplitView(ITextView textView)
        {
            IVsCodeWindow codeWindow;
            if (_vsAdapter.GetCodeWindow(textView).TryGetValue(out codeWindow))
            {
                return SendSplit(codeWindow);
            }

            return false;
        }

        internal bool MoveViewUp(ITextView textView)
        {
            try
            {
                var vsCodeWindow = _vsAdapter.GetCodeWindow(textView).Value;
                var vsTextView = vsCodeWindow.GetSecondaryView().Value;
                return ErrorHandler.Succeeded(vsTextView.SendExplicitFocus());
            }
            catch
            {
                return false;
            }
        }

        internal bool MoveViewDown(ITextView textView)
        {
            try
            {
                var vsCodeWindow = _vsAdapter.GetCodeWindow(textView).Value;
                var vsTextView = vsCodeWindow.GetPrimaryView().Value;
                return ErrorHandler.Succeeded(vsTextView.SendExplicitFocus());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Send the split command.  This is really a toggle command that will split
        /// and unsplit the window
        /// </summary>
        private static bool SendSplit(IVsCodeWindow codeWindow)
        {
            var target = codeWindow as IOleCommandTarget;
            if (target != null)
            {
                var group = VSConstants.GUID_VSStandardCommandSet97;
                var cmdId = VSConstants.VSStd97CmdID.Split;
                var result = target.Exec(ref group, (uint)cmdId, (uint)OLECMDEXECOPT.OLECMDEXECOPT_DODEFAULT, IntPtr.Zero, IntPtr.Zero);
                return VSConstants.S_OK == result;
            }
            return false;
        }

        internal IEnumerable<ITextView> GetTextViews(ITextBuffer textBuffer)
        {
            return _vsAdapter.GetTextViews(textBuffer)
                .Select(x => _vsAdapter.EditorAdapter.GetWpfTextView(x))
                .Where(x => x != null);
        }

        #region ITextManager

        IEnumerable<ITextBuffer> ITextManager.TextBuffers
        {
            get { return TextBuffers; }
        }

        IEnumerable<ITextView> ITextManager.TextViews
        {
            get { return TextViews; }
        }

        ITextView ITextManager.ActiveTextViewOptional
        {
            get { return ActiveTextView; }
        }

        IEnumerable<ITextView> ITextManager.GetTextViews(ITextBuffer textBuffer)
        {
            return GetTextViews(textBuffer);
        }

        bool ITextManager.NavigateTo(VirtualSnapshotPoint point)
        {
            return NavigateTo(point);
        }

        Result ITextManager.Save(ITextBuffer textBuffer)
        {
            return Save(textBuffer);
        }

        bool ITextManager.CloseView(ITextView textView)
        {
            return CloseView(textView);
        }

        bool ITextManager.SplitView(ITextView textView)
        {
            return SplitView(textView);
        }

        bool ITextManager.MoveViewUp(ITextView textView)
        {
            return MoveViewUp(textView);
        }

        bool ITextManager.MoveViewDown(ITextView textView)
        {
            return MoveViewDown(textView);
        }

        #endregion
    }
}
