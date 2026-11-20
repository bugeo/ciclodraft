using System.Globalization;
using System.Text;
using CicloDraft.Geometry;
using CicloDraft.Model;

namespace CicloDraft.IO;

/// <summary>
/// Scrive un DXF ASCII R12 (AC1009): il formato più compatibile (AutoCAD, BricsCAD,
/// LibreCAD, QGIS, DraftSight...). Unità di disegno: metri ($INSUNITS = 6).
/// Le coordinate sono quelle del sistema di riferimento del GeoPackage.
/// </summary>
public static class DxfWriter
{
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string LayerFor(ElementType t) => t switch
    {
        ElementType.Marciapiede => "CD_MARCIAPIEDE",
        ElementType.Ciclabile => "CD_PISTA_CICLABILE",
        ElementType.Carreggiata => "CD_CARREGGIATA",
        ElementType.Corsia => "CD_CORSIA",
        ElementType.Parcheggio => "CD_PARCHEGGIO",
        ElementType.Aiuola => "CD_AIUOLA",
        ElementType.Separatore => "CD_SEPARATORE_GIALLO",
        ElementType.CorsiaCiclabile => "CD_CORSIA_CICLABILE",
        ElementType.CorsiaDoppia => "CD_CORSIA_DOPPIA",
        _ => "CD_ELEMENTI"
    };

    static int ColorFor(ElementType t) => t switch
    {
        ElementType.Marciapiede => 8,
        ElementType.Ciclabile => 1,
        ElementType.Carreggiata => 250,
        ElementType.Corsia => 4,
        ElementType.Parcheggio => 5,
        ElementType.Aiuola => 3,
        ElementType.Separatore => 2,
        ElementType.CorsiaCiclabile => 30,
        ElementType.CorsiaDoppia => 140,
        _ => 7
    };

    public static void Write(string path, Project p, GpkgData? data, bool includeContext, bool includeLabels)
    {
        V2 o = data?.Origin ?? new V2(0, 0);
        var ents = new StringBuilder();
        var layers = new Dictionary<string, int>
        {
            ["CD_BORDI_STRADA"] = 7,
            ["CD_INTERASSI"] = 1,
            ["CD_SEGNALETICA"] = 7,
            ["CD_TESTI"] = 7,
        };
        foreach (ElementType t in Enum.GetValues<ElementType>()) layers[LayerFor(t)] = ColorFor(t);

        var box = BBox.Empty;

        foreach (var e in p.Edges.Where(e => e.Points.Count >= 2 && (e.Owner == -1 || p.Find(e.Owner) is { Centered: true })))
        {
            Poly(ents, e.Owner == -1 ? "CD_BORDI_STRADA" : "CD_INTERASSI", e.Points, false, o);
            box = box.Union(BBox.Of(e.Points));
        }

        foreach (var el in p.Elements.Where(x => x.Error == null && x.Polygon.Count >= 3))
        {
            foreach (var dl in el.DividerLines())
                if (dl.Count >= 2) Poly(ents, "CD_SEGNALETICA", dl, false, o);   // strisce di mezzeria
            foreach (var sl in el.StallMarkings())
                if (sl.Count >= 2) Poly(ents, "CD_SEGNALETICA", sl, false, o);   // stalli del parcheggio
            Poly(ents, LayerFor(el.Type), el.Polygon, true, o);
            box = box.Union(BBox.Of(el.Polygon));
            if (includeLabels)
            {
                var ip = LabelPoint(el);
                double h = Math.Clamp(el.Metrics.WMean * 0.25, 0.25, 0.8);
                Text(ents, "CD_TESTI", ip + new V2(0, h * 0.7), h, Ascii(el.Name), o);
                Text(ents, "CD_TESTI", ip - new V2(0, h * 0.9), h * 0.75,
                    $"L={F2(el.Metrics.Length)} m  A={F2(el.Metrics.Area)} m2  l={F2(el.Metrics.WMin)}-{F2(el.Metrics.WMax)} m", o);
            }
        }

        if (includeContext && data != null)
        {
            foreach (var l in data.Layers.Where(l => l.Role != 0))
            {
                string name = (LayerRole)l.Role switch
                {
                    LayerRole.Muri => "CTX_MURI",
                    LayerRole.Marciapiedi => "CTX_MARCIAPIEDI",
                    _ => "CTX_" + LayerName(l.Name)
                };
                layers.TryAdd(name, (LayerRole)l.Role == LayerRole.Muri ? 252 : 9);
                foreach (var r in l.Rings) Poly(ents, name, r.Pts, r.Closed, o);
            }
        }

        if (box.IsEmpty) box = new BBox(0, 0, 1, 1);
        var sb = new StringBuilder();
        G(sb, 0, "SECTION"); G(sb, 2, "HEADER");
        G(sb, 9, "$ACADVER"); G(sb, 1, "AC1009");
        G(sb, 9, "$INSUNITS"); G(sb, 70, "6");
        G(sb, 9, "$MEASUREMENT"); G(sb, 70, "1");
        G(sb, 9, "$LUNITS"); G(sb, 70, "2");
        G(sb, 9, "$LUPREC"); G(sb, 70, "3");
        G(sb, 9, "$EXTMIN"); G(sb, 10, N(box.MinX + o.X)); G(sb, 20, N(box.MinY + o.Y)); G(sb, 30, "0.0");
        G(sb, 9, "$EXTMAX"); G(sb, 10, N(box.MaxX + o.X)); G(sb, 20, N(box.MaxY + o.Y)); G(sb, 30, "0.0");
        G(sb, 0, "ENDSEC");

        G(sb, 0, "SECTION"); G(sb, 2, "TABLES");
        G(sb, 0, "TABLE"); G(sb, 2, "LTYPE"); G(sb, 70, "1");
        G(sb, 0, "LTYPE"); G(sb, 2, "CONTINUOUS"); G(sb, 70, "0"); G(sb, 3, "Solid line");
        G(sb, 72, "65"); G(sb, 73, "0"); G(sb, 40, "0.0");
        G(sb, 0, "ENDTAB");
        G(sb, 0, "TABLE"); G(sb, 2, "LAYER"); G(sb, 70, (layers.Count + 1).ToString(Ci));
        G(sb, 0, "LAYER"); G(sb, 2, "0"); G(sb, 70, "0"); G(sb, 62, "7"); G(sb, 6, "CONTINUOUS");
        foreach (var (name, col) in layers)
        {
            G(sb, 0, "LAYER"); G(sb, 2, name); G(sb, 70, "0"); G(sb, 62, col.ToString(Ci)); G(sb, 6, "CONTINUOUS");
        }
        G(sb, 0, "ENDTAB");
        G(sb, 0, "TABLE"); G(sb, 2, "STYLE"); G(sb, 70, "1");
        G(sb, 0, "STYLE"); G(sb, 2, "STANDARD"); G(sb, 70, "0"); G(sb, 40, "0.0"); G(sb, 41, "1.0");
        G(sb, 50, "0.0"); G(sb, 71, "0"); G(sb, 42, "0.5"); G(sb, 3, "txt"); G(sb, 4, "");
        G(sb, 0, "ENDTAB");
        G(sb, 0, "ENDSEC");

        G(sb, 0, "SECTION"); G(sb, 2, "ENTITIES");
        sb.Append(ents);
        G(sb, 0, "ENDSEC");
        G(sb, 0, "EOF");

        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }

    public static V2 LabelPoint(Element el)
    {
        var poly = Project.ToNts(el.Polygon);
        if (poly != null)
        {
            try
            {
                var g = poly.IsValid ? poly : poly.Buffer(0);
                var c = g.InteriorPoint;
                if (c != null && !c.IsEmpty) return new V2(c.X, c.Y);
            }
            catch { }
        }
        double L = PL.Length(el.Inner);
        return (PL.PointAt(el.Inner, L / 2) + PL.PointAt(el.Outer, PL.Length(el.Outer) / 2)) * 0.5;
    }

    static void Poly(StringBuilder sb, string layer, IReadOnlyList<V2> pts, bool closed, V2 o)
    {
        G(sb, 0, "POLYLINE"); G(sb, 8, layer); G(sb, 66, "1");
        G(sb, 10, "0.0"); G(sb, 20, "0.0"); G(sb, 30, "0.0");
        G(sb, 70, closed ? "1" : "0");
        foreach (var p in pts)
        {
            G(sb, 0, "VERTEX"); G(sb, 8, layer);
            G(sb, 10, N(p.X + o.X)); G(sb, 20, N(p.Y + o.Y)); G(sb, 30, "0.0");
        }
        G(sb, 0, "SEQEND"); G(sb, 8, layer);
    }

    static void Text(StringBuilder sb, string layer, V2 at, double h, string s, V2 o)
    {
        G(sb, 0, "TEXT"); G(sb, 8, layer);
        G(sb, 10, N(at.X + o.X)); G(sb, 20, N(at.Y + o.Y)); G(sb, 30, "0.0");
        G(sb, 40, N(h)); G(sb, 1, s); G(sb, 72, "1");
        G(sb, 11, N(at.X + o.X)); G(sb, 21, N(at.Y + o.Y)); G(sb, 31, "0.0");
    }

    static void G(StringBuilder sb, int code, string v) { sb.Append(code.ToString(Ci)).Append("\r\n").Append(v).Append("\r\n"); }
    static string N(double v) => v.ToString("0.0###", Ci);
    static string F2(double v) => v.ToString("0.00", Ci);

    public static string Ascii(string s)
    {
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch < 128 ? ch : '_');
        }
        return sb.ToString();
    }

    static string LayerName(string s)
    {
        var a = Ascii(s).ToUpperInvariant();
        var sb = new StringBuilder();
        foreach (var ch in a) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        var r = sb.ToString();
        return r.Length > 24 ? r[..24] : r;
    }
}

public static class CsvWriter
{
    public static void Write(string path, Project p)
    {
        var ci = CultureInfo.GetCultureInfo("it-IT");
        var sb = new StringBuilder();
        sb.AppendLine("Elemento;Tipo;Agganciato a;Lunghezza (m);Superficie (m²);Larghezza min (m);Larghezza max (m);Larghezza media (m)");
        foreach (var e in p.Elements)
        {
            var m = e.Metrics;
            sb.AppendLine(string.Join(";", e.Name, Element.TypeLabel(e.Type), p.EdgeName(e.Base),
                m.Length.ToString("0.00", ci), m.Area.ToString("0.00", ci), m.WMin.ToString("0.00", ci),
                m.WMax.ToString("0.00", ci), m.WMean.ToString("0.00", ci)));
        }
        foreach (var g in p.Elements.GroupBy(e => e.Type))
            sb.AppendLine($"TOTALE {Element.TypeLabel(g.Key)};;;{g.Sum(e => e.Metrics.Length).ToString("0.00", ci)};{g.Sum(e => e.Metrics.Area).ToString("0.00", ci)};;;");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
