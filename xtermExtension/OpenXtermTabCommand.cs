using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;

namespace xtermExtension
{
    internal sealed class OpenXtermTabCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("70516c53-3cae-4f12-8e66-2f3f4ce13f98");

        private readonly AsyncPackage package;

        private OpenXtermTabCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                throw new InvalidOperationException("Unable to get OleMenuCommandService.");
            }

            _ = new OpenXtermTabCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = package.JoinableTaskFactory.RunAsync(async delegate
            {
                ToolWindowPane window = await package.ShowToolWindowAsync(typeof(XtermToolWindow), 0, true, package.DisposalToken);
                if (window?.Frame == null)
                {
                    throw new NotSupportedException("Cannot create tool window.");
                }

                if (window is XtermToolWindow xtermWindow)
                {
                    xtermWindow.RecreateContentIfNeeded("CommandOpen");
                    if (xtermWindow.Content is XtermToolWindowControl control)
                    {
                        control.EnsureActiveAfterShow();
                    }
                }
            });
        }
    }
}
