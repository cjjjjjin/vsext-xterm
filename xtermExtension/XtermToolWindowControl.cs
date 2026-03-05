using System.Windows;
using System.Windows.Controls;

namespace xtermExtension
{
    public class XtermToolWindowControl : UserControl
    {
        public XtermToolWindowControl()
        {
            Content = new Grid
            {
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock
                    {
                        Text = "xtermExtension tab is open.",
                        FontSize = 14
                    }
                }
            };
        }
    }
}
