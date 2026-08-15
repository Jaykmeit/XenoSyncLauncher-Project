using System.Threading.Tasks;
using System.Windows;

namespace XenoSyncLauncher.Settings;

/// <summary>
/// Shown non-modally (Show(), not ShowDialog()) and kept open across
/// multiple attempts: the caller awaits WaitForSubmitAsync() each time it
/// needs an answer, and calls ShowIncorrectError() to reuse the same window
/// for a retry instead of opening a new one when a previous attempt turns
/// out to be wrong. Only Cancel (or closing the window) ends the flow.
/// </summary>
public partial class CredentialPromptWindow : Window
{
    private readonly bool _isPassword;
    private TaskCompletionSource<string?>? _pendingSubmit;

    public CredentialPromptWindow(string message, bool isPassword)
    {
        InitializeComponent();

        _isPassword = isPassword;
        MessageText.Text = message;

        ValuePasswordBox.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
        ValueTextBox.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;

        Closed += (_, _) => _pendingSubmit?.TrySetResult(null);
    }

    /// <summary>Waits for the user to click Continue (returns the entered value) or Cancel/close the window (returns null).</summary>
    public Task<string?> WaitForSubmitAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        SetInputEnabled(true);

        var field = _isPassword ? (UIElement)ValuePasswordBox : ValueTextBox;
        field.Focus();

        _pendingSubmit = new TaskCompletionSource<string?>();
        return _pendingSubmit.Task;
    }

    /// <summary>Reuses this same window for a retry: shows the error, clears the field, and re-enables input.</summary>
    public void ShowIncorrectError(string message)
    {
        if (_isPassword) ValuePasswordBox.Clear(); else ValueTextBox.Clear();

        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        SetInputEnabled(true);

        var field = _isPassword ? (UIElement)ValuePasswordBox : ValueTextBox;
        field.Focus();
    }

    private void SetInputEnabled(bool enabled)
    {
        ValuePasswordBox.IsEnabled = enabled;
        ValueTextBox.IsEnabled = enabled;
        OkButton.IsEnabled = enabled;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var value = _isPassword ? ValuePasswordBox.Password : ValueTextBox.Text;

        SetInputEnabled(false);
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;

        _pendingSubmit?.TrySetResult(value);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>Botón X de la barra de título personalizada: mismo comportamiento que Cancel.</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e) => CancelButton_Click(sender, e);
}