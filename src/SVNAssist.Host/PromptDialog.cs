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

        /// <summary>Dialogo con un campo di testo; ritorna il testo (OK/Invio) o null (Annulla/Esc). Thread UI.</summary>
        public static string ShowText(string message, string label, string defaultValue)
        {
            var box = new TextBox { Text = defaultValue, MinWidth = 280 };
            var fields = Field(label, box);
            return ShowFields(message, fields, new Control[] { box }) ? box.Text : null;
        }

        /// <summary>Dialogo utente/password (password mascherata, "Ricorda" attivo): [utente, password, "1"/"0"] o null. Thread UI.</summary>
        public static string[] ShowCredentials(string detail, string defaultUser)
        {
            var user = new TextBox { Text = defaultUser, MinWidth = 280 };
            var pwd = new PasswordBox { MinWidth = 280 };
            var remember = new CheckBox { Content = "Ricorda", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
            remember.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var panel = new StackPanel();
            panel.Children.Add(Field("Utente", user));
            panel.Children.Add(Field("Password", pwd));
            panel.Children.Add(remember);
            var msg = "Il server SVN richiede l'accesso." + (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail);
            return ShowFields(msg, panel, new Control[] { user, pwd })
                ? new[] { user.Text, pwd.Password, remember.IsChecked == true ? "1" : "0" }
                : null;
        }

        private static FrameworkElement Field(string label, Control input)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) };
            l.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            row.Children.Add(l);
            row.Children.Add(input);
            return row;
        }

        private static bool ShowFields(string message, UIElement fields, Control[] focusOrder)
        {
            var d = new PromptDialog(message, new[] { "OK", "Annulla" });
            d._inputs = fields;
            d.AddInputs();
            d.Loaded += (s, e) => { focusOrder[0].Focus(); (focusOrder[0] as TextBox)?.SelectAll(); };
            d.ShowModal();
            return d._result == 0;
        }

        private UIElement _inputs;
        private void AddInputs()
        {
            // il contenuto e' un DockPanel (bottoni in basso, testo che riempie): si inserisce il campo tra testo e bottoni
            var panel = (DockPanel)Content;
            var scroll = (ScrollViewer)panel.Children[1];
            var stack = new StackPanel();
            panel.Children.Remove(scroll);
            stack.Children.Add(scroll);
            stack.Children.Add(_inputs);
            panel.Children.Add(stack);
            // Invio = OK
            KeyDown += (s, e) => { if (e.Key == Key.Enter && !(e.OriginalSource is Button)) { _result = 0; DialogResult = true; } };
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
