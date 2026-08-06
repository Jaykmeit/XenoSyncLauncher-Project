using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XenoSyncLauncher.Models;

public class PatcherFlagViewModel : INotifyPropertyChanged
{
    private bool _isChecked;

    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
