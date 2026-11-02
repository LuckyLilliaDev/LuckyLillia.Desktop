using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace LuckyLilliaDesktop.Views;

public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            OnOkClick(this, e);
        };
        // 没有系统标题栏, Esc 是除按钮外唯一的取消途径
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(null);
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    public TextInputDialog(string message, string watermark = "") : this()
    {
        MessageText.Text = message;
        InputBox.Watermark = watermark;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            InputBox.Focus();
            return;
        }
        Close(text);
    }
}
