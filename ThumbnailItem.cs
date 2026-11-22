using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PdfMangaViewer;

public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private static readonly SolidColorBrush CurrentBorderBrush =
        new(Windows.UI.Color.FromArgb(255, 96, 205, 255));

    private static readonly SolidColorBrush DefaultBorderBrush =
        new(Colors.Transparent);

    private BitmapImage? _image;
    private bool _isCurrent;

    public ThumbnailItem(uint pageIndex, bool isCurrent)
    {
        PageIndex = pageIndex;
        _isCurrent = isCurrent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public uint PageIndex { get; }

    public string PageLabel => $"{PageIndex + 1}";

    public bool IsRequested { get; set; }

    public BitmapImage? Image
    {
        get => _image;
        set
        {
            if (!ReferenceEquals(_image, value))
            {
                _image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent != value)
            {
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BorderBrush)));
            }
        }
    }

    public Brush BorderBrush => _isCurrent ? CurrentBorderBrush : DefaultBorderBrush;
}
