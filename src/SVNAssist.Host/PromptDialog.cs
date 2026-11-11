using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace SVNAssist.Host
{
    /// <summary>Dialogo modale VS con un messaggio e N pulsanti arbitrari (le MessageBox di VS ne hanno solo di fissi).</summary>
    internal sealed class PromptDialog : DialogWindow
    {
        private int _result = -1;

        private PromptDialog(string message, string[] buttons)
        {
            Title = "SVNAssist";
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxWidth = 640; MaxHeight = 560; MinWidth = 340;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);

            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            text.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 440 };

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                var b = new Button { Content = buttons[i], MinWidth = 80, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) };
                b.SetResourceReference(StyleProperty, VsResourceKeys.ThemedDialogButtonStyleKey);
                b.Click += (s, e) => { _result = index; DialogResult = true; };
                row.Children.Add(b);
            }

            var panel = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(row, Dock.Bottom);
            panel.Children.Add(row);
            panel.Children.Add(scroll);
            Content = panel;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) { _result = -1; DialogResult = false; } };
        }

        /// <summary>Indice del pulsante scelto, -1 se chiuso con Esc/X. Thread UI.</summary>
        public static int Show(string message, string[] buttons)
        {
            var d = new PromptDialog(message ?? "", buttons.Length == 0 ? new[] { "OK" } : buttons);
            d.ShowModal();
            return d._result;
        }
    }
}
