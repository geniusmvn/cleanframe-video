using System.ComponentModel;
using System.Runtime.CompilerServices;
using Erasa.Video2.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Erasa.Video2.App.ViewModels;

public sealed class JobViewModel : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;

    public JobViewModel(MediaJob model)
    {
        Model = model;
        Model.PropertyChanged += (_, _) => Refresh();
    }

    public MediaJob Model { get; }
    public string FileName => Model.FileName;
    public string Details => $"{Model.DimensionsText}  •  {Model.DurationText}  •  {FormatBytes(Model.FileSizeBytes)}";
    public string StateText => Model.StateText;
    public string? Error => Model.Error;
    public double Progress => Model.Progress * 100;
    public bool HasError => !string.IsNullOrWhiteSpace(Model.Error);

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasError));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        var value = (double)bytes;
        var units = new[] { "B", "KB", "MB", "GB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
