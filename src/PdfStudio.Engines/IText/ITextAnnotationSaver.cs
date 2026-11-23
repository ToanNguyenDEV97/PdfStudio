using System.Globalization;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Xobject;
using iText.Layout;
using iText.Layout.Element;
using PdfStudio.Core.Models;

namespace PdfStudio.Engines.IText;

internal static class ITextAnnotationSaver
{
    public static void Save(string inputPath, string outputPath, IReadOnlyList<PdfAnnotationModel> annotations)
    {
        if (annotations.Count == 0)
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        var reader = new PdfReader(inputPath);
        reader.SetUnethicalReading(true);

        using var writer = new PdfWriter(outputPath);
        // Incremental Update (AppendMode) giúp Ctrl+S cực nhanh và giữ nguyên cấu trúc gốc
        using var pdfDoc = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode());

        foreach (var item in annotations)
        {
            int pageNum = item.PageIndex + 1;
            if (pageNum < 1 || pageNum > pdfDoc.GetNumberOfPages()) continue;

            PdfPage page = pdfDoc.GetPage(pageNum);
            PdfAnnotation? annot = item.Type switch
            {
                AnnotationType.Highlight => CreateTextMarkupAnnotation(pdfDoc, page, item, isUnderline: false),
                AnnotationType.Underline => CreateTextMarkupAnnotation(pdfDoc, page, item, isUnderline: true),
                AnnotationType.StickyNote => CreateStickyNoteAnnotation(pdfDoc, page, item),
                AnnotationType.FreeText => CreateFreeTextAnnotation(pdfDoc, page, item),
                AnnotationType.Ink => CreateInkAnnotation(pdfDoc, page, item),
                _ => null
            };

            if (annot is not null)
                page.AddAnnotation(annot);
        }
    }

    private static PdfAnnotation? CreateTextMarkupAnnotation(
        PdfDocument pdfDoc, PdfPage page, PdfAnnotationModel item, bool isUnderline)
    {
        var sourceRects = item.QuadRects.Count > 0 ? item.QuadRects : [item.Bounds];
        if (sourceRects.Count == 0) return null;

        var (r, g, b) = ParseHexColor(item.ColorHex, isUnderline ? (0.09f, 0.64f, 0.29f) : (0.98f, 0.80f, 0.08f));
        var rgbColor = new DeviceRgb(r, g, b);

        var pdfRects = new List<Rectangle>(sourceRects.Count);
        var quadValues = new List<float>(sourceRects.Count * 8);

        foreach (var vr in sourceRects)
        {
            var pr = ToPdfRect(page, vr);
            pdfRects.Add(pr);

            // Thứ tự 4 đỉnh QuadPoints theo PDF Spec: TopLeft, TopRight, BottomLeft, BottomRight
            var tl = ToPdfPoint(page, vr.X, vr.Y);
            var tr = ToPdfPoint(page, vr.X + vr.Width, vr.Y);
            var bl = ToPdfPoint(page, vr.X, vr.Y + vr.Height);
            var br = ToPdfPoint(page, vr.X + vr.Width, vr.Y + vr.Height);

            quadValues.AddRange([tl.X, tl.Y, tr.X, tr.Y, bl.X, bl.Y, br.X, br.Y]);
        }

        Rectangle unionRect = UnionRects(pdfRects);
        var dict = new PdfDictionary();
        dict.Put(PdfName.Type, PdfName.Annot);
        dict.Put(PdfName.Subtype, isUnderline ? PdfName.Underline : PdfName.Highlight);
        dict.Put(PdfName.Rect, ToPdfArray(unionRect));
        dict.Put(new PdfName("QuadPoints"), new PdfArray(quadValues.ToArray()));
        dict.Put(PdfName.C, new PdfArray(new float[] { r, g, b }));
        dict.Put(new PdfName("CA"), new PdfNumber(isUnderline ? 1.0f : (float)Math.Clamp(item.Opacity, 0.15, 0.85)));
        dict.Put(PdfName.F, new PdfNumber(4));

        // Sinh Normal Appearance Stream để MuPDF/Chrome hiển thị chuẩn màu & độ trong suốt
        var appearance = new PdfFormXObject(new Rectangle(0, 0, unionRect.GetWidth(), unionRect.GetHeight()));
        var canvas = new PdfCanvas(appearance, pdfDoc);
        canvas.SaveState();

        if (isUnderline)
        {
            canvas.SetStrokeColor(rgbColor);
            canvas.SetLineWidth((float)Math.Clamp(item.StrokeThickness, 1.0, 4.0));
            foreach (var pr in pdfRects)
            {
                float lx = pr.GetLeft() - unionRect.GetLeft();
                float ly = pr.GetBottom() - unionRect.GetBottom() + 1.0f;
                canvas.MoveTo(lx, ly).LineTo(lx + pr.GetWidth(), ly).Stroke();
            }
        }
        else
        {
            var gState = new PdfExtGState()
                .SetFillOpacity((float)Math.Clamp(item.Opacity, 0.15, 0.85))
                .SetBlendMode(PdfExtGState.BM_MULTIPLY);
            canvas.SetExtGState(gState);
            canvas.SetFillColor(rgbColor);
            foreach (var pr in pdfRects)
            {
                float lx = pr.GetLeft() - unionRect.GetLeft();
                float ly = pr.GetBottom() - unionRect.GetBottom();
                canvas.Rectangle(lx, ly, pr.GetWidth(), pr.GetHeight()).Fill();
            }
        }

        canvas.RestoreState();
        var annot = PdfAnnotation.MakeAnnotation(dict);
        annot.SetNormalAppearance(appearance.GetPdfObject());
        return annot;
    }

    private static PdfAnnotation CreateStickyNoteAnnotation(PdfDocument pdfDoc, PdfPage page, PdfAnnotationModel item)
    {
        var (r, g, b) = ParseHexColor(item.ColorHex, (0.96f, 0.62f, 0.04f));
        Rectangle pdfRect = ToPdfRect(page, new PdfRect(item.Bounds.X, item.Bounds.Y, 22, 22));

        var dict = new PdfDictionary();
        dict.Put(PdfName.Type, PdfName.Annot);
        dict.Put(PdfName.Subtype, PdfName.Text);
        dict.Put(PdfName.Rect, ToPdfArray(pdfRect));
        dict.Put(PdfName.Contents, new PdfString(item.TextContent, PdfEncodings.UNICODE_BIG));
        dict.Put(new PdfName("T"), new PdfString("PdfStudio", PdfEncodings.UNICODE_BIG));
        dict.Put(new PdfName("Name"), new PdfName("Comment"));
        dict.Put(PdfName.C, new PdfArray(new float[] { r, g, b }));
        dict.Put(PdfName.F, new PdfNumber(4));

        // Vẽ biểu tượng ghi chú nổi sắc nét vào /AP
        var appearance = new PdfFormXObject(new Rectangle(0, 0, 22, 22));
        var canvas = new PdfCanvas(appearance, pdfDoc);
        canvas.SaveState();
        canvas.SetFillColor(new DeviceRgb(r, g, b));
        canvas.SetStrokeColor(new DeviceRgb(r * 0.7f, g * 0.7f, b * 0.7f));
        canvas.SetLineWidth(1.0f);
        canvas.RoundRectangle(1.5, 1.5, 19, 19, 4).FillStroke();
        canvas.SetStrokeColor(ColorConstants.WHITE);
        canvas.SetLineWidth(1.4f);
        canvas.MoveTo(5.5, 14).LineTo(16.5, 14).Stroke();
        canvas.MoveTo(5.5, 10.5).LineTo(16.5, 10.5).Stroke();
        canvas.MoveTo(5.5, 7).LineTo(12.5, 7).Stroke();
        canvas.RestoreState();

        var annot = PdfAnnotation.MakeAnnotation(dict);
        annot.SetNormalAppearance(appearance.GetPdfObject());
        return annot;
    }

    private static PdfAnnotation CreateFreeTextAnnotation(PdfDocument pdfDoc, PdfPage page, PdfAnnotationModel item)
    {
        var (r, g, b) = ParseHexColor(item.ColorHex, (0.07f, 0.09f, 0.15f));
        var rgbColor = new DeviceRgb(r, g, b);
        float fontSize = (float)Math.Clamp(item.FontSize, 6, 72);

        double width = Math.Max(40, item.Bounds.Width);
        double height = Math.Max(fontSize * 1.6, item.Bounds.Height);
        Rectangle pdfRect = ToPdfRect(page, new PdfRect(item.Bounds.X, item.Bounds.Y, width, height));

        string daString = string.Format(
            CultureInfo.InvariantCulture,
            "/Helv {0:0.##} Tf {1:0.###} {2:0.###} {3:0.###} rg",
            fontSize, r, g, b);

        var dict = new PdfDictionary();
        dict.Put(PdfName.Type, PdfName.Annot);
        dict.Put(PdfName.Subtype, PdfName.FreeText);
        dict.Put(PdfName.Rect, ToPdfArray(pdfRect));
        dict.Put(PdfName.Contents, new PdfString(item.TextContent, PdfEncodings.UNICODE_BIG));
        dict.Put(new PdfName("DA"), new PdfString(daString));
        dict.Put(PdfName.F, new PdfNumber(4));

        // Nhúng font hệ thống Unicode để tiếng Việt có dấu hiển thị chuẩn trên mọi trình đọc
        PdfFont font = LoadVietnameseFont(item.FontName);
        var appearance = new PdfFormXObject(new Rectangle(0, 0, pdfRect.GetWidth(), pdfRect.GetHeight()));
        using (var layoutCanvas = new Canvas(appearance, pdfDoc))
        {
            var paragraph = new Paragraph(item.TextContent)
                .SetFont(font)
                .SetFontSize(fontSize)
                .SetFontColor(rgbColor)
                .SetMargin(0)
                .SetPadding(2)
                .SetMultipliedLeading(1.15f);
            layoutCanvas.Add(paragraph);
        }

        var annot = PdfAnnotation.MakeAnnotation(dict);
        annot.SetNormalAppearance(appearance.GetPdfObject());
        return annot;
    }

    private static PdfAnnotation? CreateInkAnnotation(PdfDocument pdfDoc, PdfPage page, PdfAnnotationModel item)
    {
        if (item.InkPoints.Count < 2) return null;

        var (r, g, b) = ParseHexColor(item.ColorHex, (0.72f, 0.11f, 0.11f));
        float lineWidth = (float)Math.Clamp(item.StrokeThickness, 0.75, 14.0);

        var pdfPoints = item.InkPoints.Select(p => ToPdfPoint(page, p.X, p.Y)).ToList();
        float minX = pdfPoints.Min(p => p.X) - lineWidth;
        float maxX = pdfPoints.Max(p => p.X) + lineWidth;
        float minY = pdfPoints.Min(p => p.Y) - lineWidth;
        float maxY = pdfPoints.Max(p => p.Y) + lineWidth;

        var rect = new Rectangle(minX, minY, Math.Max(2, maxX - minX), Math.Max(2, maxY - minY));

        var strokeCoords = new PdfArray();
        foreach (var pt in pdfPoints)
        {
            strokeCoords.Add(new PdfNumber(pt.X));
            strokeCoords.Add(new PdfNumber(pt.Y));
        }

        var inkList = new PdfArray();
        inkList.Add(strokeCoords);

        var borderStyle = new PdfDictionary();
        borderStyle.Put(new PdfName("W"), new PdfNumber(lineWidth));

        var dict = new PdfDictionary();
        dict.Put(PdfName.Type, PdfName.Annot);
        dict.Put(PdfName.Subtype, PdfName.Ink);
        dict.Put(PdfName.Rect, ToPdfArray(rect));
        dict.Put(new PdfName("InkList"), inkList);
        dict.Put(PdfName.C, new PdfArray(new float[] { r, g, b }));
        dict.Put(new PdfName("BS"), borderStyle);
        dict.Put(PdfName.F, new PdfNumber(4));

        var appearance = new PdfFormXObject(new Rectangle(0, 0, rect.GetWidth(), rect.GetHeight()));
        var canvas = new PdfCanvas(appearance, pdfDoc);
        canvas.SaveState();
        canvas.SetStrokeColor(new DeviceRgb(r, g, b));
        canvas.SetLineWidth(lineWidth);
        canvas.SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND);
        canvas.SetLineJoinStyle(PdfCanvasConstants.LineJoinStyle.ROUND);

        canvas.MoveTo(pdfPoints[0].X - rect.GetLeft(), pdfPoints[0].Y - rect.GetBottom());
        for (int i = 1; i < pdfPoints.Count; i++)
            canvas.LineTo(pdfPoints[i].X - rect.GetLeft(), pdfPoints[i].Y - rect.GetBottom());
        canvas.Stroke();
        canvas.RestoreState();

        var annot = PdfAnnotation.MakeAnnotation(dict);
        annot.SetNormalAppearance(appearance.GetPdfObject());
        return annot;
    }

    // =========================================================
    // HÀM QUY ĐỔI TỌA ĐỘ TOP-LEFT 72 DPI -> PDF USER SPACE
    // =========================================================
    private static (float X, float Y) ToPdfPoint(PdfPage page, double vx, double vy)
    {
        Rectangle crop = page.GetCropBox();
        float cx = crop.GetLeft();
        float cy = crop.GetBottom();
        float cw = crop.GetWidth();
        float ch = crop.GetHeight();
        int rot = ((page.GetRotation() % 360) + 360) % 360;

        return rot switch
        {
            90 => ((float)(cx + vy), (float)(cy + vx)),
            180 => ((float)(cx + cw - vx), (float)(cy + vy)),
            270 => ((float)(cx + cw - vy), (float)(cy + ch - vx)),
            _ => ((float)(cx + vx), (float)(cy + ch - vy))
        };
    }

    private static Rectangle ToPdfRect(PdfPage page, PdfRect vr)
    {
        var p1 = ToPdfPoint(page, vr.X, vr.Y);
        var p2 = ToPdfPoint(page, vr.X + vr.Width, vr.Y);
        var p3 = ToPdfPoint(page, vr.X, vr.Y + vr.Height);
        var p4 = ToPdfPoint(page, vr.X + vr.Width, vr.Y + vr.Height);

        float minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        float maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        float minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        float maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));

        return new Rectangle(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private static Rectangle UnionRects(IReadOnlyList<Rectangle> rects)
    {
        float minX = rects.Min(r => r.GetLeft());
        float minY = rects.Min(r => r.GetBottom());
        float maxX = rects.Max(r => r.GetRight());
        float maxY = rects.Max(r => r.GetTop());
        return new Rectangle(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private static PdfArray ToPdfArray(Rectangle r) =>
    new PdfArray(new float[] { r.GetLeft(), r.GetBottom(), r.GetRight(), r.GetTop() });

    private static (float R, float G, float B) ParseHexColor(string hex, (float, float, float) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 8) clean = clean[2..]; // Bỏ kênh alpha nếu có dạng #AARRGGBB
        if (clean.Length != 6) return fallback;

        if (byte.TryParse(clean[0..2], NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(clean[2..4], NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(clean[4..6], NumberStyles.HexNumber, null, out byte b))
        {
            return (r / 255f, g / 255f, b / 255f);
        }
        return fallback;
    }

    private static PdfFont LoadVietnameseFont(string? fontName)
    {
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = fontName?.ToLowerInvariant() switch
        {
            "times new roman" => ["times.ttf", "arial.ttf"],
            "calibri" => ["calibri.ttf", "arial.ttf"],
            "tahoma" => ["tahoma.ttf", "arial.ttf"],
            "segoe ui" => ["segoeui.ttf", "arial.ttf"],
            _ => ["arial.ttf", "segoeui.ttf", "tahoma.ttf"]
        };

        foreach (string file in candidates)
        {
            string fullPath = System.IO.Path.Combine(fontsDir, file);
            if (File.Exists(fullPath))
                return PdfFontFactory.CreateFont(fullPath, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
        }

        return PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
    }
}