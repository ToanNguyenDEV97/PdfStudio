using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Models;

namespace PdfStudio.App.ViewModels;

public sealed record HighlightBoxItem(
    double X,
    double Y,
    double Width,
    double Height,
    bool IsActive = false);

public partial class PdfPageViewModel : ObservableObject
{
    public const double DipPerPoint = 96.0 / 72.0;

    public int PageIndex { get; }
    public int PageNumber => PageIndex + 1;
    public double WidthPoints { get; }
    public double HeightPoints { get; }
    public string Label => $"Trang {PageNumber}";

    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;
    [ObservableProperty] private BitmapSource? _pageImage;
    [ObservableProperty] private BitmapSource? _thumbnailImage;
    [ObservableProperty] private bool _isSelected;

    // Lớp 2: Khung bôi đen văn bản và kết quả tìm kiếm (tọa độ 72 DPI)
    public ObservableCollection<HighlightBoxItem> SelectionRects { get; } = [];
    public ObservableCollection<HighlightBoxItem> SearchRects { get; } = [];
    // Lớp 3: Danh sách Ghi chú (Annotation) chưa lưu của trang
    public ObservableCollection<AnnotationItemViewModel> Annotations { get; } = [];

    public double RenderedScale { get; set; }

    public PdfPageViewModel(PageMetadata meta, double initialZoomPercent)
    {
        PageIndex = meta.PageIndex;
        WidthPoints = meta.WidthPoints;
        HeightPoints = meta.HeightPoints;
        UpdateDisplaySize(initialZoomPercent);
    }

    public void UpdateDisplaySize(double zoomPercent)
    {
        double factor = DipPerPoint * (zoomPercent / 100.0);
        DisplayWidth = Math.Max(40, WidthPoints * factor);
        DisplayHeight = Math.Max(40, HeightPoints * factor);
    }
}