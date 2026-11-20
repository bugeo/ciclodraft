using System.ComponentModel;
using System.Runtime.CompilerServices;
using CicloDraft.Geometry;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CicloDraft.IO;

public enum LayerRole { Nascosto = 0, Muri = 1, Marciapiedi = 2, Sfondo = 3, Edifici3D = 4 }

/// <summary>Poligono (con eventuali buchi) in coordinate locali, con l'altezza letta dagli attributi (NaN se assente).</summary>
public record LocalPolygon(Polygon Poly, double Height);

public class Ring
{
    public List<V2> Pts = new();
    public bool Closed;
    public BBox Box;
}

public class GpkgLayer : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string GeomType { get; set; } = "";
    public int FeatureCount { get; set; }
    public List<Ring> Rings { get; } = new();
    /// <summary>Poligoni completi (per il 3D), coordinate locali.</summary>
    public List<LocalPolygon> Polys { get; } = new();
    /// <summary>Punti (es. alberi), coordinate locali.</summary>
    public List<V2> Points { get; } = new();
    public bool IsTrees => GpkgReader.IsTreeName(Name);
    /// <summary>Colonna con l'altezza degli edifici, se trovata.</summary>
    public string? HeightColumn { get; set; }
    public BBox Box = BBox.Empty;
    public string Info => $"{GeomType.ToLowerInvariant()} · {FeatureCount} elementi" + (HeightColumn != null ? $" · altezze: {HeightColumn}" : "");

    int _role;
    /// <summary>0 nascosto, 1 muri abitazioni, 2 marciapiedi esistenti, 3 sfondo, 4 volumi degli edifici per il 3D.</summary>
    public int Role
    {
        get => _role;
        set { if (_role != value) { _role = value; OnChanged(); } }
    }

    bool _visible = true;
    /// <summary>Disegnato sulla mappa (il ruolo resta valido anche se il livello è nascosto alla vista).</summary>
    public bool Visible
    {
        get => _visible;
        set { if (_visible != value) { _visible = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class GpkgData
{
    public string Path = "";
    public List<GpkgLayer> Layers = new();
    /// <summary>Origine locale: coordinate assolute = locali + Origin.</summary>
    public V2 Origin;
    public string Crs = "";
    public bool Geographic;
    public BBox Box = BBox.Empty;

    public IEnumerable<(GpkgLayer layer, int idx, Ring ring)> RingsWithRole(params LayerRole[] roles)
    {
        foreach (var l in Layers)
            if (roles.Contains((LayerRole)l.Role))
                for (int i = 0; i < l.Rings.Count; i++) yield return (l, i, l.Rings[i]);
    }

    public bool InsideBuilding(V2 p)
    {
        foreach (var (_, _, r) in RingsWithRole(LayerRole.Muri))
            if (r.Closed && r.Box.Contains(p) && PL.PointInRing(r.Pts, p)) return true;
        return false;
    }
}

public static class GpkgReader
{
    const double R = 6378137.0;

    public static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    public static GpkgData Load(string path, V2? originOverride = null)
    {
        var data = new GpkgData { Path = path };
        using var con = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        con.Open();

        var cols = new List<(string table, string col, string type, int srs)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT table_name, column_name, geometry_type_name, srs_id FROM gpkg_geometry_columns";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) cols.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3)));
        }
        if (cols.Count == 0) throw new InvalidDataException("Il file non contiene livelli vettoriali (gpkg_geometry_columns è vuota).");

        // Sistema di riferimento (si usa quello del primo livello)
        int srs = cols[0].srs;
        string def = "";
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT srs_name, organization, organization_coordsys_id, definition FROM gpkg_spatial_ref_sys WHERE srs_id=$id";
            cmd.Parameters.AddWithValue("$id", srs);
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
            {
                def = rd.IsDBNull(3) ? "" : rd.GetString(3);
                data.Crs = $"{(rd.IsDBNull(1) ? "" : rd.GetString(1))}:{(rd.IsDBNull(2) ? "" : rd.GetValue(2))} {(rd.IsDBNull(0) ? "" : rd.GetString(0))}".Trim();
            }
        }
        var d = def.TrimStart().ToUpperInvariant();
        data.Geographic = srs == 4326 || d.StartsWith("GEOGCS") || d.StartsWith("GEOGCRS") || d.StartsWith("GEODCRS");
        if (cols.Select(c => c.srs).Distinct().Count() > 1)
            data.Crs += " (attenzione: i livelli hanno sistemi di riferimento diversi)";

        var reader = new WKBReader();
        var raw = new List<(GpkgLayer layer, List<(List<V2> pts, bool closed)> rings)>();
        var rawPolys = new Dictionary<GpkgLayer, List<(Polygon p, double h)>>();
        var rawPoints = new Dictionary<GpkgLayer, List<(double x, double y)>>();
        foreach (var (table, col, type, _) in cols)
        {
            var layer = new GpkgLayer { Name = table, GeomType = type };
            var rings = new List<(List<V2>, bool)>();
            if (type.ToUpperInvariant().Contains("POINT"))
            {
                if (IsTreeName(table))
                {
                    // alberi: servono per il 3D
                    var pts = new List<(double, double)>();
                    using var pc = con.CreateCommand();
                    pc.CommandText = $"SELECT {Q(col)} FROM {Q(table)}";
                    using var prd = pc.ExecuteReader();
                    while (prd.Read())
                    {
                        if (prd.IsDBNull(0)) continue;
                        var g = ParseBlob((byte[])prd.GetValue(0), reader);
                        if (g == null || g.IsEmpty) continue;
                        layer.FeatureCount++;
                        foreach (var c in g.Coordinates) pts.Add((c.X, c.Y));
                    }
                    rawPoints[layer] = pts;
                }
                else
                {
                    // gli altri punti non servono per disegnare: si contano soltanto
                    using var cc = con.CreateCommand();
                    cc.CommandText = $"SELECT count(*) FROM {Q(table)}";
                    layer.FeatureCount = Convert.ToInt32(cc.ExecuteScalar());
                }
                raw.Add((layer, rings));
                continue;
            }
            layer.HeightColumn = FindHeightColumn(con, table);
            // livelli "a classi" (es. base DBT per la pianificazione: una sola tabella con edifici, marciapiedi, carreggiate…):
            // si dividono in un livello per classe, così a ciascuno si può dare il suo ruolo
            var classCol = FindClassColumn(con, table);
            var groups = new Dictionary<string, (GpkgLayer layer, List<(List<V2>, bool)> rings, List<(Polygon, double)> polys)>();
            (GpkgLayer layer, List<(List<V2>, bool)> rings, List<(Polygon, double)> polys) Group(string? key)
            {
                key ??= "";
                if (groups.TryGetValue(key, out var gr)) return gr;
                var l = classCol == null ? layer : new GpkgLayer { Name = $"{ClassLabel(key)} · {table}", GeomType = type, HeightColumn = layer.HeightColumn };
                gr = (l, classCol == null ? rings : new List<(List<V2>, bool)>(), new List<(Polygon, double)>());
                return groups[key] = gr;
            }
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT {Q(col)}, {(layer.HeightColumn != null ? Q(layer.HeightColumn) : "NULL")}, {(classCol != null ? Q(classCol) : "NULL")} FROM {Q(table)}";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (rd.IsDBNull(0)) continue;
                var blob = (byte[])rd.GetValue(0);
                var g = ParseBlob(blob, reader);
                if (g == null || g.IsEmpty) continue;
                var gr = Group(rd.IsDBNull(2) ? null : Convert.ToString(rd.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
                gr.layer.FeatureCount++;
                Collect(g, gr.rings);
                double h = double.NaN;
                if (layer.HeightColumn != null && !rd.IsDBNull(1))
                    try { h = Convert.ToDouble(rd.GetValue(1), System.Globalization.CultureInfo.InvariantCulture); } catch { }
                for (int i = 0; i < g.NumGeometries; i++)
                    if (g.GetGeometryN(i) is Polygon pg && !pg.IsEmpty) gr.polys.Add((pg, h));
            }
            if (groups.Count == 0) Group(null);
            foreach (var gr in groups.OrderBy(k => k.Key).Select(k => k.Value))
            {
                raw.Add((gr.layer, gr.rings));
                rawPolys[gr.layer] = gr.polys;
            }
        }

        // Coordinate geografiche: proiezione locale equirettangolare in metri
        Func<double, double, V2> proj = (x, y) => new V2(x, y);
        if (data.Geographic)
        {
            var all = BBox.Of(raw.SelectMany(r => r.rings.SelectMany(x => x.pts)));
            double lon0 = (all.MinX + all.MaxX) / 2, lat0 = (all.MinY + all.MaxY) / 2;
            double k = Math.Cos(lat0 * Math.PI / 180);
            proj = (x, y) => new V2(R * k * (x - lon0) * Math.PI / 180, R * (y - lat0) * Math.PI / 180);
            foreach (var (_, rings) in raw)
                foreach (var (pts, _) in rings)
                    for (int i = 0; i < pts.Count; i++)
                        pts[i] = new V2(R * k * (pts[i].X - lon0) * Math.PI / 180, R * (pts[i].Y - lat0) * Math.PI / 180);
        }

        var box = BBox.Of(raw.SelectMany(r => r.rings.SelectMany(x => x.pts)));
        data.Origin = originOverride ?? (box.IsEmpty ? new V2(0, 0)
            : new V2(Math.Round((box.MinX + box.MaxX) / 2 / 100) * 100, Math.Round((box.MinY + box.MaxY) / 2 / 100) * 100));

        foreach (var (layer, rings) in raw)
        {
            foreach (var (pts, closed) in rings)
            {
                var local = pts.Select(p => p - data.Origin).ToList();
                if (closed && local.Count > 1 && V2.Dist(local[0], local[^1]) < 1e-9) local.RemoveAt(local.Count - 1);
                if (local.Count < 2) continue;
                var r = new Ring { Pts = local, Closed = closed, Box = BBox.Of(local) };
                layer.Rings.Add(r);
                layer.Box = layer.Box.Union(r.Box);
            }
            if (rawPolys.TryGetValue(layer, out var lp))
                foreach (var (pg, h) in lp)
                {
                    var o = data.Origin;
                    LinearRing Loc(LineString ls) => Gf.CreateLinearRing(ls.Coordinates.Select(c => { var v = proj(c.X, c.Y) - o; return new Coordinate(v.X, v.Y); }).ToArray());
                    try { layer.Polys.Add(new LocalPolygon(Gf.CreatePolygon(Loc(pg.ExteriorRing), pg.InteriorRings.Select(Loc).ToArray()), h)); } catch { }
                }
            if (rawPoints.TryGetValue(layer, out var rp))
                foreach (var (x, y) in rp) layer.Points.Add(proj(x, y) - data.Origin);
            data.Box = data.Box.Union(layer.Box);
            layer.Role = (int)GuessRole(layer);
            data.Layers.Add(layer);
        }
        return data;
    }

    static readonly GeometryFactory Gf = new();

    // nomi tipici della colonna con l'altezza dell'edificio (DBGT: UN_VOL_AV = altezza della unità volumetrica)
    static readonly string[] HeightNames = { "UN_VOL_AV", "ALTEZZA", "ALTEZZA_M", "ALT_GRONDA", "H_GRONDA", "ALTEZZA_GRONDA", "HEIGHT", "BUILDING_HEIGHT", "ALTEZZA_EDIFICIO", "H_EDIFICIO", "H", "ALT", "HGT" };

    /// <summary>Etichetta breve di una classe: senza il codice numerico iniziale (es. 020102_EDIFC → EDIFC).</summary>
    static string ClassLabel(string key)
    {
        if (key.Length == 0) return "senza classe";
        var k = System.Text.RegularExpressions.Regex.Replace(key, @"^[0-9]+[_\- ]+", "");
        return k.Length > 0 ? k : key;
    }

    static readonly string[] ClassNames = { "codice_classe", "dataset", "classe", "class", "categoria", "layer_name" };

    /// <summary>Colonna che distingue classi diverse nella stessa tabella (da 2 a 40 valori distinti).</summary>
    static string? FindClassColumn(SqliteConnection con, string table)
    {
        var cols = new List<string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({Q(table)})";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) cols.Add(rd.GetString(1));
        }
        foreach (var n in ClassNames)
        {
            var c = cols.FirstOrDefault(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (c == null) continue;
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT count(DISTINCT {Q(c)}) FROM {Q(table)}";
            var k = Convert.ToInt32(cmd.ExecuteScalar());
            if (k >= 2 && k <= 40) return c;
        }
        return null;
    }

    static string? FindHeightColumn(SqliteConnection con, string table)
    {
        var cols = new List<string>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Q(table)})";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) cols.Add(rd.GetString(1));
        foreach (var n in HeightNames)
        {
            var c = cols.FirstOrDefault(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (c != null) return c;
        }
        return null;
    }

    public static bool IsTreeName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("albero") || n.Contains("alberi") || n.Contains("tree") || n.Contains("alberat");
    }

    static readonly string[] BuildingWords = { "edific", "edifc", "edi_min", "fabbric", "build", "muri", "muro", "casa", "case", "costruz", "unita_vol", "uvol", "un_vol" };

    /// <summary>Il nome del livello indica edifici (muri delle abitazioni)?</summary>
    public static bool IsBuildingName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("edifc") && n.EndsWith("_me")) return false;
        return BuildingWords.Any(n.Contains);
    }

    static LayerRole GuessRole(GpkgLayer l)
    {
        if (l.GeomType.ToUpperInvariant().Contains("POINT")) return l.IsTrees ? LayerRole.Sfondo : LayerRole.Nascosto;
        var n = l.Name.ToLowerInvariant();
        // DBGT: EDIFC_CR_EDF_IS = edificio, ingombro al suolo; EDIFC_..._ME = massimo estradosso (tetti)
        if (n.StartsWith("edifc") && n.EndsWith("_me")) return LayerRole.Nascosto;
        // DBGT: le unità volumetriche hanno l'altezza → edifici per il 3D (per ricalcare i muri si usano gli EDIFC)
        if ((n.Contains("un_vol") || n.Contains("unita_vol") || n.Contains("uvol")) && l.HeightColumn != null) return LayerRole.Edifici3D;
        if (IsBuildingName(l.Name)) return LayerRole.Muri;
        if (new[] { "marciap", "sidewalk", "pedon", "footway", "ac_ped" }.Any(n.Contains)) return LayerRole.Marciapiedi;
        if (n.EndsWith("_sr")) return LayerRole.Nascosto; // DBGT: copie delle superfici di riferimento
        return LayerRole.Sfondo;
    }

    public static NetTopologySuite.Geometries.Geometry? ParseBlob(byte[] b, WKBReader reader)
    {
        try
        {
            if (b.Length > 8 && b[0] == (byte)'G' && b[1] == (byte)'P')
            {
                int flags = b[3];
                int env = ((flags >> 1) & 7) switch { 1 => 32, 2 => 48, 3 => 48, 4 => 64, _ => 0 };
                int off = 8 + env;
                var wkb = new byte[b.Length - off];
                Buffer.BlockCopy(b, off, wkb, 0, wkb.Length);
                return reader.Read(wkb);
            }
            return reader.Read(b); // WKB semplice
        }
        catch { return null; }
    }

    public static void Collect(NetTopologySuite.Geometries.Geometry g, List<(List<V2>, bool)> rings)
    {
        switch (g)
        {
            case Polygon p:
                rings.Add((Pts(p.ExteriorRing), true));
                foreach (var h in p.InteriorRings) rings.Add((Pts(h), true));
                break;
            case LinearRing lr:
                rings.Add((Pts(lr), true));
                break;
            case LineString ls:
                rings.Add((Pts(ls), ls.IsClosed && ls.NumPoints > 3));
                break;
            case GeometryCollection gc:
                foreach (var c in gc.Geometries) Collect(c, rings);
                break;
        }
    }

    static List<V2> Pts(LineString l) => l.Coordinates.Select(c => new V2(c.X, c.Y)).ToList();
}
