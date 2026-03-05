using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace xtermExtension
{
    [Guid("9f6f52b9-6608-4ecf-a039-9eb405ec7f97")]
    public class XtermToolWindow : ToolWindowPane
    {
        public XtermToolWindow() : base(null)
        {
            Caption = "Xterm Tab";
            Content = new XtermToolWindowControl();
        }
    }
}
