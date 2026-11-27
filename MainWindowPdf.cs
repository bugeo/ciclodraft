using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CicloDraft.Controls;
using CicloDraft.Geometry;
using CicloDraft.IO;
using CicloDraft.Model;
using NetTopologySuite.Geometries;
using SkiaSharp;

namespace CicloDraft;

/// <summary>Cosa mettere nel PDF: i gruppi (0 = tutto il progetto, se non ci sono gruppi), sezioni, fotoinserimenti, formato.</summary>
public class PdfOptions
{
    public List<int> Groups { get; set; } = new();
    public bool Sections { get; set; } = true;
    public bool Photos { get; set; } = true;
    public bool A4 { get; set; }
}

/// <summary>
/// Esportazione PDF: per ogni gruppo una pagina orizzontale con la planimetria del solo gruppo (in scala),
/// poi le sezioni che lo attraversano e i fotoinserimenti che lo inquadrano, disegnati con il solo gruppo.
/// </summary>
public partial class MainWindow
{
    static readonly int[] Scales = { 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000, 20000, 25000, 50000, 100000 };
    const float Mm = 72f / 25.4f;

    float PageW, PageH;
    record PdfPage(string Kind, int Group, CrossSection? Section, PhotoInsertRef? Photo);

    /// <summary>Copia del progetto per la stampa (le modifiche alla vista non toccano quello aperto).</summary>
    Project PrintCopy()
    {
        var origin = _ed.Data?.Origin ?? new V2(0, 0);
        var copy = ProjectFile.ToProject(ProjectFile.FromJson(ProjectFile.ToJson(_projectPath ?? "stampa.ciclo", _ed.Project, _ed.Data)), origin);
        copy.Regenerate();
        copy.Photos.AddRange(_ed.Project.Photos);
        return copy;
    }

    static IEnumerable<Element> GroupElements(Project p, int g) =>
        p.Elements.Where(e => (g == 0 ? true : e.Group == g) && e.Error == null && e.Polygon.Count >= 3);

    static bool SectionCrosses(CrossSection s, List<Element> els)
    {
        var line = new GeometryFactory().CreateLineString(new[] { new Coordinate(s.A.X, s.A.Y), new Coordinate(s.B.X, s.B.Y) });
        foreach (var e in els)
            if (Project.ToNts(e.Polygon) is { } pg && pg.Intersects(line)) return true;
        return false;
    }

    static string Sidecar(string photo, string ext) => Path.ChangeExtension(photo, null) + ext;

    /// <summary>Fotocamera salvata accanto alla foto, in coordinate locali del progetto.</summary>
    PhotoCamera? PhotoCam(PhotoInsertRef r)
    {
        var sc = Sidecar(r.Path, ".fotoinserimento.json");
        if (!File.Exists(r.Path) || !File.Exists(sc)) return null;
        try
        {
            var f = PhotoInsertFile.Load(sc);
            if (f.Camera is not { } c) return null;
            var o = _ed.Data?.Origin ?? new V2(0, 0);
            return PhotoCamera.FromArray(new[] { c.X - o.X, c.Y - o.Y, c.Z, c.Yaw, c.Pitch, c.Roll, c.F }, c.W, c.H);
        }
        catch { return null; }
    }

    /// <summary>Il gruppo cade nell'inquadratura della foto (davanti alla fotocamera, entro 400 m).</summary>
    static bool PhotoSees(PhotoCamera cam, List<Element> els)
    {
        bool In(V2 p) { var (u, v, d) = cam.Project(p.X, p.Y, 0); return d > 0.3 && d < 400 && u >= 0 && v >= 0 && u < cam.W && v < cam.H; }
        foreach (var e in els)
        {
            var ring = e.Polygon;
            for (int i = 0; i < ring.Count; i++)
            {
                V2 a = ring[i], b = ring[(i + 1) % ring.Count];
                int n = Math.Clamp((int)(V2.Dist(a, b) / 1.0), 1, 400);
                for (int k = 0; k < n; k++) if (In(a + (b - a) * (k / (double)n))) return true;
            }
        }
        return false;
    }

    static SKImage ToSk(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms);
        return SKImage.FromEncodedData(SKData.CreateCopy(ms.ToArray()));
    }

    static SKImage JpegOf(Rgba img)
    {
        var info = new SKImageInfo(img.W, img.H, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(img.Px, 0, bmp.GetPixels(), img.Px.Length);
        using var raw = SKImage.FromBitmap(bmp);
        var data = raw.Encode(SKEncodedImageFormat.Jpeg, 95);
        return SKImage.FromEncodedData(data);
    }

    /// <summary>
    /// Disegna un controllo come vettori sulla pagina, nel rettangolo indicato. Il disegno di Avalonia imposta da sé la
    /// matrice della tela: si registra in una «picture» (le matrici restano relative) e la si posa sulla pagina.
    /// </summary>
    static async Task DrawVector(SKCanvas cv, Control c, Size size, SKRect dst, float pageW, float pageH)
    {
        c.Measure(size); c.Arrange(new Rect(size));
        using var rec = new SKPictureRecorder();
        var rc = rec.BeginRecording(new SKRect(0, 0, (float)size.Width, (float)size.Height));
        await Avalonia.Skia.Helpers.DrawingContextHelper.RenderAsync(rc, c, new Rect(size), new Vector(96, 96));
        using var pic = rec.EndRecording();
        int n = cv.Save();
        cv.ClipRect(dst);
        cv.Translate(dst.Left, dst.Top);
        cv.Scale((float)(dst.Width / size.Width), (float)(dst.Height / size.Height));
        cv.DrawPicture(pic);
        cv.RestoreToCount(n);
    }

    /// <summary>Planimetria del gruppo disegnata a vettori nell'area; restituisce il denominatore della scala.</summary>
    async Task<int> DrawPlan(SKCanvas cv, Project copy, List<Element> els, SKRect area)
    {
        const double PxPerPt = 1.4;
        var ped = new Editor { Project = copy, Data = _ed.Data };
        var map = new MapCanvas { Print = true, ShowLabels = Map.ShowLabels, ShowPhotoIcons = true, Editor = ped, ShowGroups = false };
        map.PrintSections = copy.Sections.Where(s => SectionCrosses(s, els)).ToHashSet();
        map.PrintPhotos = copy.Photos.Where(r => PhotoCam(r) is { } c && PhotoSees(c, els)).ToHashSet();
        var size = new Size(Math.Round(area.Width * PxPerPt), Math.Round(area.Height * PxPerPt));
        map.Measure(size); map.Arrange(new Rect(size));
        var pts = els.SelectMany(e => e.Polygon).ToList();
        map.FitPoints(pts, new Thickness(size.Width * 0.04, size.Height * 0.06));
        // scala normalizzata: la più grande tra quelle d'uso che fa stare tutto il gruppo
        double ptPerM = map.View.s / PxPerPt;
        double den = 1000.0 / (ptPerM / Mm);
        int scale = Scales.FirstOrDefault(x => x >= den - 1e-6);
        if (scale == 0) scale = (int)Math.Ceiling(den);
        var v = map.View;
        map.View = (v.cx, v.cy, 1000.0 / scale * Mm * PxPerPt);
        await DrawVector(cv, map, size, area, PageW, PageH);
        return scale;
    }

    async Task<SKImage?> RenderPhoto(PhotoInsertRef r, Project copy)
    {
        if (PhotoCam(r) is not { } cam) return null;
        var data = _ed.Data;
        return await Task.Run(() =>
        {
            var clean = Sidecar(r.Path, ".senza_auto.png");
            var photo = Rgba.Load(File.Exists(clean) ? clean : r.Path);
            if (photo.W != cam.W || photo.H != cam.H) photo = Rgba.Load(r.Path);
            var mp = Sidecar(r.Path, ".maschera.png");
            Rgba? mask = File.Exists(mp) ? Rgba.Load(mp) : null;
            if (mask != null && (mask.W != photo.W || mask.H != photo.H)) mask = null;
            var res = PhotoRender.Render(cam, photo, mask, copy, data, new PhotoRenderOptions());
            var comp = res.Composite;
            var f = PhotoInsertFile.Load(Sidecar(r.Path, ".fotoinserimento.json"));
            var m = f.WarpBase is { Length: 9 } wb ? wb : f.Warp is { Length: 8 } wq ? Homography.RectToQuad(photo.W, photo.H, wq) : Homography.IdentityMatrix();
            if (f.WarpSrc is { Length: 8 } ws && f.WarpDst is { Length: 8 } wd && Homography.QuadToQuad(ws, wd) is { } hp) m = Homography.Mul(hp, m);
            if (m.Zip(Homography.IdentityMatrix()).Any(p => Math.Abs(p.First - p.Second) > 1e-6))
                comp = PhotoRender.ApplyWarp(res.Raw, photo, mask, m).Composite;
            return JpegOf(comp);
        });
    }

    public async Task<string> ExportPdf(string path, PdfOptions o, Action<string>? progress = null)
    {
        var copy = PrintCopy();
        float W = (o.A4 ? 297 : 420) * Mm, H = (o.A4 ? 210 : 297) * Mm;
        PageW = W; PageH = H;
        float margin = 12 * Mm, head = 15 * Mm, foot = 16 * Mm;
        string projName = _projectPath != null ? Path.GetFileNameWithoutExtension(_projectPath) : BaseName;

        // pagine: per ogni gruppo la planimetria, poi sezioni e fotoinserimenti che lo riguardano
        var pages = new List<PdfPage>();
        var groupEls = new Dictionary<int, List<Element>>();
        foreach (var g in o.Groups)
        {
            copy.OnlyGroup = g == 0 ? null : g;
            var els = GroupElements(copy, g).ToList();
            if (els.Count == 0) continue;
            groupEls[g] = els;
            pages.Add(new PdfPage("plan", g, null, null));
            if (o.Sections) foreach (var s in copy.Sections.Where(s => SectionCrosses(s, els))) pages.Add(new PdfPage("section", g, s, null));
            if (o.Photos) foreach (var r in copy.Photos.Where(r => PhotoCam(r) is { } c && PhotoSees(c, els))) pages.Add(new PdfPage("photo", g, null, r));
        }
        copy.OnlyGroup = null;
        if (pages.Count == 0) throw new InvalidOperationException("Nei gruppi scelti non ci sono elementi da stampare.");

        using var font = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) ?? SKTypeface.Default;
        using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        var ink = new SKColor(0x1F, 0x24, 0x2B); var grey = new SKColor(0x6B, 0x72, 0x7C); var rule = new SKColor(0xD5, 0xD9, 0xDE);
        SKPaint Txt(float size, bool b = false, SKColor? c = null) => new() { Typeface = b ? bold : font, TextSize = size, Color = c ?? ink, IsAntialias = true };

        using var fs = File.Create(path);
        using var doc = SKDocument.CreatePdf(fs, new SKDocumentPdfMetadata { Title = projName, Creator = "CicloDraft", Producer = "CicloDraft", RasterDpi = 200 });
        int pn = 0;
        foreach (var pg in pages)
        {
            pn++;
            progress?.Invoke($"Pagina {pn} di {pages.Count}…");
            await Task.Delay(1);
            var grp = copy.Groups.FirstOrDefault(x => x.Id == pg.Group);
            string gName = grp?.Name ?? "Progetto";
            copy.OnlyGroup = pg.Group == 0 ? null : pg.Group;
            var els = groupEls[pg.Group];
            var cv = doc.BeginPage(W, H);
            cv.Clear(SKColors.White);

            // intestazione: colore e nome del gruppo, a destra il progetto e la data
            float x = margin, yBase = margin + 7 * Mm;
            if (grp?.Color is { } col && SKColor.TryParse(col, out var gc))
            {
                using var sw = new SKPaint { Color = gc, IsAntialias = true };
                cv.DrawRoundRect(x, yBase - 4.2f * Mm, 4.5f * Mm, 4.5f * Mm, 1.2f, 1.2f, sw);
                x += 7 * Mm;
            }
            string title = pg.Kind switch
            {
                "section" => $"{gName} · {(string.IsNullOrWhiteSpace(pg.Section!.Name) ? "Sezione" : pg.Section.Name)}",
                "photo" => $"{gName} · {pg.Photo!.Name}",
                _ => gName
            };
            using (var t = Txt(16, true)) cv.DrawText(title, x, yBase, t);
            using (var t = Txt(9, false, grey))
            {
                var right = $"{projName}  ·  {DateTime.Now:dd/MM/yyyy}";
                cv.DrawText(right, W - margin - t.MeasureText(right), yBase, t);
            }
            using (var lp = new SKPaint { Color = rule, StrokeWidth = 0.6f })
            {
                cv.DrawLine(margin, margin + head - 2 * Mm, W - margin, margin + head - 2 * Mm, lp);
                cv.DrawLine(margin, H - margin - foot + 2 * Mm, W - margin, H - margin - foot + 2 * Mm, lp);
            }
            var area = new SKRect(margin, margin + head, W - margin, H - margin - foot);

            if (pg.Kind == "plan")
            {
                int scale = await DrawPlan(cv, copy, els, area);
                DrawLegend(cv, copy, els, margin, H - margin - foot + 8 * Mm, W * 0.62f, Txt);
                DrawScale(cv, scale, W - margin, H - margin - 3 * Mm, copy.ViewRotation, o.A4, Txt, ink);
            }
            else
            {
                SKImage? img = null;
                if (pg.Kind == "section")
                {
                    var r = SectionCalc.Compute(pg.Section!, copy, _ed.Data);
                    var (sp, ss) = BuildSectionPage(pg.Section!, r);
                    float k = (float)Math.Min(area.Width / ss.Width, area.Height / ss.Height);
                    float iw = (float)ss.Width * k, ih = (float)ss.Height * k;
                    await DrawVector(cv, sp, ss, SKRect.Create(area.MidX - iw / 2, area.Top + (area.Height - ih) / 2, iw, ih), W, H);
                }
                else img = await RenderPhoto(pg.Photo!, copy);
                if (img != null)
                {
                    float k = Math.Min(area.Width / img.Width, area.Height / img.Height);
                    float iw = img.Width * k, ih = img.Height * k;
                    var dst = SKRect.Create(area.MidX - iw / 2, area.Top + (area.Height - ih) / 2, iw, ih);
                    using var sp = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                    cv.DrawImage(img, dst, sp);
                    img.Dispose();
                }
            }
            using (var t = Txt(8, false, grey))
            {
                var s = $"{pn} / {pages.Count}";
                cv.DrawText(s, W - margin - t.MeasureText(s), H - margin + 1 * Mm, t);
            }
            doc.EndPage();
        }
        copy.OnlyGroup = null;
        doc.Close();
        return $"{pages.Count} {(pages.Count == 1 ? "pagina" : "pagine")}";
    }

    /// <summary>Legenda della planimetria: un riquadro per tipo con la superficie del gruppo.</summary>
    static void DrawLegend(SKCanvas cv, Project p, List<Element> els, float x0, float y, float maxW, Func<float, bool, SKColor?, SKPaint> txt)
    {
        float x = x0;
        foreach (var g in els.GroupBy(e => e.Type).OrderBy(g => Element.TypeLabel(g.Key)))
        {
            var fill = MapCanvas.Colors(g.Key, 255).fill as Avalonia.Media.ISolidColorBrush;
            var c = fill?.Color ?? Avalonia.Media.Colors.Gray;
            string label = Element.TypeLabel(g.Key), val = $"{g.Sum(e => e.Metrics.Area):0} m²".Replace('.', ',');
            using var t = txt(8.5f, true, null); using var t2 = txt(8.5f, false, new SKColor(0x6B, 0x72, 0x7C));
            float w = 4 * Mm + t.MeasureText(label) + 1.5f * Mm + t2.MeasureText(val) + 6 * Mm;
            if (x + w > x0 + maxW && x > x0) { x = x0; y += 5 * Mm; }
            using (var sw = new SKPaint { Color = new SKColor(c.R, c.G, c.B), IsAntialias = true })
                cv.DrawRoundRect(x, y - 2.6f * Mm, 3 * Mm, 3 * Mm, 0.8f, 0.8f, sw);
            cv.DrawText(label, x + 4 * Mm, y, t);
            cv.DrawText(val, x + 4 * Mm + t.MeasureText(label) + 1.5f * Mm, y, t2);
            x += w;
        }
    }

    /// <summary>Scala grafica e numerica in basso a destra, con la freccia del nord.</summary>
    static void DrawScale(SKCanvas cv, int scale, float right, float y, double rotDeg, bool a4, Func<float, bool, SKColor?, SKPaint> txt, SKColor ink)
    {
        // barra: una lunghezza "tonda" vicina a 40 mm sulla carta
        double mPerMm = scale / 1000.0;
        double target = 40 * mPerMm;
        double[] nice = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 };
        double len = nice.LastOrDefault(n => n <= target); if (len == 0) len = 1;
        float bw = (float)(len / mPerMm * Mm), bh = 1.6f * Mm;
        string lab = $"1:{scale:N0} ({(a4 ? "A4" : "A3")})".Replace(',', '.');
        using var t = txt(9, true, null); using var ts = txt(7.5f, false, null);
        float labW = t.MeasureText(lab);
        float bx = right - labW - 6 * Mm - bw - 12 * Mm;
        using (var fill = new SKPaint { Color = ink }) using (var st = new SKPaint { Color = ink, Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f })
        {
            for (int i = 0; i < 4; i++)
            {
                var r = SKRect.Create(bx + bw * i / 4, y - bh, bw / 4, bh);
                if (i % 2 == 0) cv.DrawRect(r, fill);
                cv.DrawRect(r, st);
            }
        }
        cv.DrawText("0", bx - ts.MeasureText("0") / 2, y - bh - 1.2f * Mm, ts);
        string ls = $"{len:0} m";
        cv.DrawText(ls, bx + bw - ts.MeasureText(ls) / 2, y - bh - 1.2f * Mm, ts);
        cv.DrawText(lab, right - labW, y, t);
        // nord: la freccia ruota con la vista
        float nx = bx - 10 * Mm, ny = y - 3.5f * Mm;
        cv.Save();
        cv.Translate(nx, ny);
        cv.RotateDegrees((float)-rotDeg);
        using (var path = new SKPath())
        {
            path.MoveTo(0, -4.5f * Mm); path.LineTo(2.2f * Mm, 3 * Mm); path.LineTo(0, 1.8f * Mm); path.LineTo(-2.2f * Mm, 3 * Mm); path.Close();
            using var fp = new SKPaint { Color = ink, IsAntialias = true };
            cv.DrawPath(path, fp);
        }
        cv.Restore();
        using (var tn = txt(7.5f, true, null)) cv.DrawText("N", nx - tn.MeasureText("N") / 2, y + 2.6f * Mm, tn);
    }
}
