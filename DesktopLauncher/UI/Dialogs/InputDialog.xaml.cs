using System.Windows;
using System.Windows.Input;

namespace DesktopLauncher.UI.Dialogs;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    /// <summary>显示输入对话框；返回 null 表示取消。</summary>
    public static string? Show(Window? owner, string title, string prompt, string defaultValue)
    {
        var dialog = new InputDialog { Owner = owner };
        dialog.Title = title;
        dialog.Prompt.Text = prompt;
        dialog.Input.Text = defaultValue;
        return dialog.ShowDialog() == true ? dialog.Input.Text : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
