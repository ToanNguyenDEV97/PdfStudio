using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfStudio.Core.Models;

namespace PdfStudio.App.ViewModels;

public partial class AnnotationItemViewModel : ObservableObject
{
    public PdfAnnotationModel Model { get; }

    public Guid Id => Model.Id;
    public int PageIndex => Model.PageIndex;
    public AnnotationType Type => Model.Type;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private string _textContent;
    [ObservableProperty] private string _colorHex;
    [ObservableProperty] private double _strokeThickness;
    [ObservableProperty] private string _fontName;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private bool _isSelected;

    public bool IsHighlight => Type == AnnotationType.Highlight;
    public bool IsUnderline => Type == AnnotationType.Underline;
    public bool IsStickyNote => Type == AnnotationType.StickyNote;
    public bool IsFreeText => Type == AnnotationType.FreeText;
    public bool IsInk => Type == AnnotationType.Ink;

    public Brush AccentBrush => CreateBrush(ColorHex, 1.0);
    public Brush HighlightFillBrush => CreateBrush(ColorHex, Model.Opacity);

    // Tọa độ cục bộ (tính từ góc trên-trái của chính đối tượng) để Canvas vẽ chính xác
    public List<HighlightBoxItem> LocalQuadRects { get; private set; } = [];
    public PointCollection LocalInkPoints { get; private set; } = [];

    public AnnotationItemViewModel(PdfAnnotationModel model)
    {
        Model = model;
        _x = model.Bounds.X;
        _y = model.Bounds.Y;
        _width = Math.Max(12, model.Bounds.Width);
        _height = Math.Max(12, model.Bounds.Height);
        _textContent = model.TextContent;
        _colorHex = model.ColorHex;
        _strokeThickness = model.StrokeThickness;
        _fontName = model.FontName;
        _fontSize = model.FontSize;
        RebuildGeometry();
    }

    public void MoveTo(double newX, double newY)
    {
        double dx = newX - X;
        double dy = newY - Y;
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;

        X = newX;
        Y = newY;
        Model.Bounds = new PdfRect(newX, newY, Width, Height);

        if (Model.QuadRects.Count > 0)
            Model.QuadRects = Model.QuadRects.Select(r => new PdfRect(r.X + dx, r.Y + dy, r.Width, r.Height)).ToList();

        if (Model.InkPoints.Count > 0)
            Model.InkPoints = Model.InkPoints.Select(p => new PdfPoint(p.X + dx, p.Y + dy)).ToList();
    }

    partial void OnTextContentChanged(string value)
    {
        Model.TextContent = value;
        if (IsFreeText)
        {
            var lines = value.Split('\n');
            int maxChars = Math.Max(6, lines.Max(l => l.Length));
            Width = Math.Max(90, Math.Min(420, maxChars * FontSize * 0.62 + 16));
            Height = Math.Max(FontSize * 1.7, lines.Length * FontSize * 1.35 + 10);
            Model.Bounds = new PdfRect(X, Y, Width, Height);
        }
    }

    partial void OnColorHexChanged(string value)
    {
        Model.ColorHex = value;
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(HighlightFillBrush));
    }

    partial void OnStrokeThicknessChanged(double value) => Model.StrokeThickness = value;

    partial void OnFontNameChanged(string value) => Model.FontName = value;

    partial void OnFontSizeChanged(double value)
    {
        Model.FontSize = value;
        if (IsFreeText) OnTextContentChanged(TextContent);
    }

    private void RebuildGeometry()
    {
        LocalQuadRects = Model.QuadRects
            .Select(r => new HighlightBoxItem(r.X - X, r.Y - Y, r.Width, r.Height))
            .ToList();

        var pts = new PointCollection(Model.InkPoints.Count);
        foreach (var p in Model.InkPoints)
            pts.Add(new Point(p.X - X, p.Y - Y));
        pts.Freeze();
        LocalInkPoints = pts;
    }

    private static Brush CreateBrush(string hex, double opacity)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Gold;
        }
    }
}