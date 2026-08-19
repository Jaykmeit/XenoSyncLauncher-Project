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

    /// <summary>
    /// Updates the status line shown while a submitted credential is being
    /// checked, WITHOUT re-enabling input or treating it as an error -
    /// intended to be called as DepotDownloader keeps printing output after
    /// a password/code was sent to it. Before this existed, the window just
    /// showed a static "Verifying..." with zero feedback from the moment
    /// Continue was clicked until DepotDownloader either succeeded, re-asked
    /// for the same credential, or the whole process eventually exited/timed
    /// out - meaning any failure DepotDownloader reported in a form we don't
    /// treat as a repeatable prompt (e.g. a login error phrased differently
    /// than expected) was silently swallowed into the background log while
    /// this window sat frozen. Calling this with DepotDownloader's own
    /// output line instead lets the person see live what's actually
    /// happening (connecting, retrying, or the real failure reason) instead
    /// of an unmoving generic message.
    /// </summary>
    public void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
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
        StatusText.Text = "Verifying...";
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