using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace xtermExtension
{
    [Guid("9f6f52b9-6608-4ecf-a039-9eb405ec7f97")]
    public class XtermToolWindow : ToolWindowPane, IVsWindowFrameNotify3
    {
        private bool recreateContentOnNextShow;

        public XtermToolWindow() : base(null)
        {
            Caption = "Xterm Tab";
            Content = new XtermToolWindowControl();
        }

        public override void OnToolWindowCreated()
        {
            base.OnToolWindowCreated();

            IVsWindowFrame frame = Frame as IVsWindowFrame;
            if (frame != null)
            {
                // Register this tool window as frame view-helper to receive OnShow/OnClose notifications.
                ErrorHandler.ThrowOnFailure(frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, this));
                LogFrameEvent("Frame notifications registered");
            }
        }

        int IVsWindowFrameNotify3.OnShow(int fShow)
        {
            LogFrameEvent("OnShow fired: fShow=" + fShow + " (" + DescribeShowFlag(fShow) + ")");

            if (ShouldRecreateOnShow(fShow))
            {
                RecreateContentIfNeeded("OnShow");
            }
            else
            {
                LogFrameEvent("Skip recreate for this OnShow phase");
            }

            if (Content is XtermToolWindowControl control)
            {
                control.EnsureActiveAfterShow();
            }

            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify3.OnClose(ref uint pgrfSaveOptions)
        {
            LogFrameEvent("OnClose fired: saveOptions=" + pgrfSaveOptions);
            if (Content is XtermToolWindowControl control)
            {
                control.BeginClose();
            }
            recreateContentOnNextShow = true;
            LogFrameEvent("Marked to recreate content on next show");
            return VSConstants.S_OK;
        }

        internal void RecreateContentIfNeeded(string trigger)
        {
            if (!recreateContentOnNextShow)
            {
                return;
            }

            Content = new XtermToolWindowControl();
            recreateContentOnNextShow = false;
            LogFrameEvent("Content recreated (" + trigger + ")");
        }

        private static bool ShouldRecreateOnShow(int fShow)
        {
            // 10 is FRAMESHOW_BeforeWinHidden; do not consume recreate flag while closing.
            if (fShow == 10 || fShow == (int)__FRAMESHOW.FRAMESHOW_WinHidden)
            {
                return false;
            }

            // 12 is observed during re-open path in this extension.
            if (fShow == 12)
            {
                return true;
            }

            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinShown
                || fShow == (int)__FRAMESHOW.FRAMESHOW_WinRestored
                || fShow == (int)__FRAMESHOW.FRAMESHOW_WinMinimized
                || fShow == (int)__FRAMESHOW.FRAMESHOW_WinMaximized)
            {
                return true;
            }

            return false;
        }

        int IVsWindowFrameNotify3.OnDockableChange(int fDockable, int x, int y, int w, int h)
        {
            LogFrameEvent("OnDockableChange fired: fDockable=" + fDockable + ", x=" + x + ", y=" + y + ", w=" + w + ", h=" + h);
            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify3.OnMove(int x, int y, int w, int h)
        {
            LogFrameEvent("OnMove fired: x=" + x + ", y=" + y + ", w=" + w + ", h=" + h);
            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify3.OnSize(int x, int y, int w, int h)
        {
            LogFrameEvent("OnSize fired: x=" + x + ", y=" + y + ", w=" + w + ", h=" + h);
            return VSConstants.S_OK;
        }

        private static string DescribeShowFlag(int fShow)
        {
            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinShown)
            {
                return "Shown";
            }

            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinHidden)
            {
                return "Hidden";
            }

            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinRestored)
            {
                return "Restored";
            }

            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinMinimized)
            {
                return "Minimized";
            }

            if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinMaximized)
            {
                return "Maximized";
            }

            return "Unknown";
        }

        private static void LogFrameEvent(string message)
        {
            string line = "[xtermExtension] " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
            Console.WriteLine(line);
            Debug.WriteLine(line);
        }
    }
}
