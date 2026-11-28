using System.ComponentModel;
using System.Runtime.CompilerServices;
using CicloDraft.Geometry;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace CicloDraft.IO;

public enum SourceKind { Gpkg, Shp }

public record SourceColumn(string Name, string SqlType);

public record SrsInfo(int Id, string Name, string Org, int OrgId, string Definition)
{
    public string Label => Org == "NONE" || Id <= 0 ? "non definito" : $"{Org}:{OrgId} {Name}";
}

/// <summary>Un livello di un file sorgente (GeoPackage o shapefile), anche molto grande.</summary>
public class SourceLayer : INotifyPropertyChanged
{
    public string FilePath { get; init; } = "";
    public SourceKind Kind { get; init; }
    public string Table { get; init; } = "";
    public string GeomColumn { get; init; } = "";
    public string GeomType { get; init; } = "";
    public bool HasZ { get; set; }
    public bool HasM { get; set; }
    public SrsInfo? Srs { get; set; }
    public int Count { get; set; }
    public BBox Extent { get; set; } = BBox.Empty;   // coordinate assolute
    public string? RtreeTable { get; set; }
    public List<SourceColumn> Columns { get; } = new();
    public string? PkColumn { get; set; }
    public ShpReader? Shp { get; set; }

    // anteprima (coordinate locali)
    public List<Ring>? Preview;
    public bool PreviewLoading;
    public int ColorIndex { get; set; }
    public Avalonia.Media.IBrush Swatch => CicloDraft.Controls.ClipCanvas.LayerBrush(ColorIndex);

    public string Name => Kind == SourceKind.Shp ? System.IO.Path.GetFileNameWithoutExtension(FilePath) : Table;
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public bool IsPoint => GeomType.ToUpperInvariant().Contains("POINT");
    public string Info => $"{GeomType.ToLowerInvariant().Replace("multi", "multi-")} · {Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("it-IT"))} elementi · {FileName}";

    bool _include, _show;
    public bool Include { get => _include; set { if (_include != value) { _include = value; On(); } } }
    public bool Show { get => _show; set { if (_show != value) { _show = value; On(); } } }

    public int LastCount { get; set; }
    string _inArea = "";
    /// <summary>Numero di elementi nell'area (aggiornato dopo il calcolo).</summary>
    public string InArea { get => _inArea; set { if (_inArea != value) { _inArea = value; On(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class SourceData
{
    static string Q(string s) => GpkgReader.Q(s);

    public static List<SourceLayer> Open(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".shp") return new List<SourceLayer> { OpenShp(path) };
        if (ext == ".gpkg") return OpenGpkg(path);
        throw new InvalidDataException("Formato non supportato: usa un GeoPackage (.gpkg) o uno shapefile (.shp).");
    }

    static SourceLayer OpenShp(string path)
    {
        var r = new ShpReader(path);
        SrsInfo? srs = null;
        if (r.PrjWkt != null) srs = new SrsInfo(-100, PrjName(r.PrjWkt), "PRJ", 0, r.PrjWkt);
        var l = new SourceLayer
        {
            FilePath = path, Kind = SourceKind.Shp, Table = System.IO.Path.GetFileNameWithoutExtension(path),
            GeomType = r.GeometryTypeName, HasZ = r.HasZ, Count = r.Count, Extent = r.Box, Shp = r, Srs = srs
        };
        foreach (var f in r.Fields) l.Columns.Add(new SourceColumn(f.Name, f.SqlType));
        return l;
    }

    static string PrjName(string wkt)
    {
        int a = wkt.IndexOf('"'), b = a >= 0 ? wkt.IndexOf('"', a + 1) : -1;
        return a >= 0 && b > a ? wkt[(a + 1)..b] : "da file .prj";
    }

    static List<SourceLayer> OpenGpkg(string path)
    {
        var res = new List<SourceLayer>();
        using var con = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        con.Open();
        var srsMap = new Dictionary<int, SrsInfo>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT srs_id, srs_name, organization, organization_coordsys_id, definition FROM gpkg_spatial_ref_sys";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                srsMap[rd.GetInt32(0)] = new SrsInfo(rd.GetInt32(0), rd.IsDBNull(1) ? "" : rd.GetString(1),
                    rd.IsDBNull(2) ? "" : rd.GetString(2), rd.IsDBNull(3) ? 0 : rd.GetInt32(3), rd.IsDBNull(4) ? "undefined" : rd.GetString(4));
        }
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view')";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) tables.Add(rd.GetString(0));
        }
        var cols = new List<(string t, string c, string type, int srs, int z, int m, double? x0, double? y0, double? x1, double? y1)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = @"SELECT g.table_name, g.column_name, g.geometry_type_name, g.srs_id, g.z, g.m, c.min_x, c.min_y, c.max_x, c.max_y
                                FROM gpkg_geometry_columns g LEFT JOIN gpkg_contents c ON c.table_name = g.table_name ORDER BY g.table_name";
            using var rd = cmd.ExecuteReader();
            double? D(int i) => rd.IsDBNull(i) ? null : rd.GetDouble(i);
            while (rd.Read())
                cols.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.IsDBNull(4) ? 0 : rd.GetInt32(4),
                    rd.IsDBNull(5) ? 0 : rd.GetInt32(5), D(6), D(7), D(8), D(9)));
        }
        foreach (var c in cols)
        {
            if (!tables.Contains(c.t)) continue;
            var l = new SourceLayer
            {
                FilePath = path, Kind = SourceKind.Gpkg, Table = c.t, GeomColumn = c.c, GeomType = c.type,
                HasZ = c.z > 0, HasM = c.m > 0, Srs = srsMap.GetValueOrDefault(c.srs)
            };
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({Q(c.t)})";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string name = rd.GetString(1), type = rd.IsDBNull(2) ? "" : rd.GetString(2);
                    bool pk = rd.GetInt32(5) > 0;
                    if (name.Equals(c.c, StringComparison.OrdinalIgnoreCase)) continue;
                    if (pk && l.PkColumn == null && type.ToUpperInvariant().Contains("INT")) l.PkColumn = name;
                    l.Columns.Add(new SourceColumn(name, string.IsNullOrWhiteSpace(type) ? "TEXT" : type));
                }
            }
            string rt = $"rtree_{c.t}_{c.c}";
            if (tables.Contains(rt)) l.RtreeTable = rt;
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"SELECT count(*) FROM {Q(c.t)}";
                l.Count = Convert.ToInt32(cmd.ExecuteScalar());
            }
            if (l.RtreeTable != null)
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"SELECT min(minx), min(miny), max(maxx), max(maxy) FROM {Q(l.RtreeTable)}";
                using var rd = cmd.ExecuteReader();
                if (rd.Read() && !rd.IsDBNull(0)) l.Extent = new BBox(rd.GetDouble(0), rd.GetDouble(1), rd.GetDouble(2), rd.GetDouble(3));
            }
            else if (c.x0 is { } x0 && c.y0 is { } y0 && c.x1 is { } x1 && c.y1 is { } y1 && x1 >= x0)
                l.Extent = new BBox(x0, y0, x1, y1);
            res.Add(l);
        }
        return res;
    }

    /// <summary>Elementi del livello che possono stare nel riquadro (null = tutti). Restituisce geometria e attributi.</summary>
    public static IEnumerable<(NtsGeometry geom, object?[] attrs)> Features(SourceLayer l, BBox? filter, CancellationToken ct = default)
    {
        if (l.Kind == SourceKind.Shp)
        {
            foreach (var (g, a) in l.Shp!.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (g == null || g.IsEmpty) continue;
                if (filter is { } f && !Overlaps(g.EnvelopeInternal, f)) continue;
                yield return (g, a);
            }
            yield break;
        }

        using var con = new SqliteConnection($"Data Source={l.FilePath};Mode=ReadOnly");
        con.Open();
        using var cmd = con.CreateCommand();
        var attrCols = string.Concat(l.Columns.Select(c => ", t." + Q(c.Name)));
        if (filter is { } fb && l.RtreeTable != null && l.PkColumn != null)
        {
            cmd.CommandText = $@"SELECT t.{Q(l.GeomColumn)}{attrCols} FROM {Q(l.Table)} t
                                 WHERE t.{Q(l.PkColumn)} IN (SELECT id FROM {Q(l.RtreeTable)} WHERE maxx >= $x0 AND minx <= $x1 AND maxy >= $y0 AND miny <= $y1)";
            cmd.Parameters.AddWithValue("$x0", fb.MinX); cmd.Parameters.AddWithValue("$x1", fb.MaxX);
            cmd.Parameters.AddWithValue("$y0", fb.MinY); cmd.Parameters.AddWithValue("$y1", fb.MaxY);
        }
        else cmd.CommandText = $"SELECT t.{Q(l.GeomColumn)}{attrCols} FROM {Q(l.Table)} t";

        var reader = new WKBReader();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (rd.IsDBNull(0)) continue;
            var blob = (byte[])rd.GetValue(0);
            if (filter is { } f && EnvelopeFromHeader(blob) is { } env && !Overlaps(env, f)) continue;
            var g = GpkgReader.ParseBlob(blob, reader);
            if (g == null || g.IsEmpty) continue;
            if (filter is { } f2 && !Overlaps(g.EnvelopeInternal, f2)) continue;
            var a = new object?[l.Columns.Count];
            for (int i = 0; i < a.Length; i++) a[i] = rd.IsDBNull(i + 1) ? null : rd.GetValue(i + 1);
            yield return (g, a);
        }
    }

    static bool Overlaps(Envelope e, BBox b) => e.MaxX >= b.MinX && e.MinX <= b.MaxX && e.MaxY >= b.MinY && e.MinY <= b.MaxY;

    static Envelope? EnvelopeFromHeader(byte[] b)
    {
        if (b.Length < 40 || b[0] != (byte)'G' || b[1] != (byte)'P') return null;
        int env = (b[3] >> 1) & 7;
        if (env == 0) return null;
        bool le = (b[3] & 1) == 1;
        double D(int o) { var s = b.AsSpan(o, 8); return le ? BitConverter.ToDouble(s) : BitConverter.ToDouble(s.ToArray().Reverse().ToArray()); }
        return new Envelope(D(8), D(16), D(24), D(32));
    }

    /// <summary>Carica l'anteprima del livello in coordinate locali (rispetto a origin).</summary>
    public static List<Ring> LoadPreview(SourceLayer l, V2 origin, CancellationToken ct)
    {
        var rings = new List<(List<V2>, bool)>();
        var res = new List<Ring>();
        int n = 0;
        foreach (var (g, _) in Features(l, null, ct))
        {
            if (l.IsPoint)
            {
                foreach (var c in g.Coordinates)
                {
                    var p = new V2(c.X, c.Y) - origin;
                    res.Add(new Ring { Pts = new List<V2> { p, p }, Closed = false, Box = new BBox(p.X, p.Y, p.X, p.Y) });
                }
                continue;
            }
            rings.Clear();
            GpkgReader.Collect(g, rings);
            foreach (var (pts, closed) in rings)
            {
                var local = pts.Select(p => p - origin).ToList();
                if (local.Count < 2) continue;
                res.Add(new Ring { Pts = local, Closed = closed, Box = BBox.Of(local) });
            }
            if (++n % 2000 == 0) ct.ThrowIfCancellationRequested();
        }
        if (l.Extent.IsEmpty && res.Count > 0)
        {
            var b = BBox.Empty;
            foreach (var r in res) b = b.Union(r.Box);
            l.Extent = new BBox(b.MinX + origin.X, b.MinY + origin.Y, b.MaxX + origin.X, b.MaxY + origin.Y);
        }
        return res;
    }
}
