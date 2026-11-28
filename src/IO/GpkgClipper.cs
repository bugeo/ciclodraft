using CicloDraft.Geometry;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace CicloDraft.IO;

public class ClipOptions
{
    /// <summary>Area di interesse in coordinate assolute (anello chiuso o aperto).</summary>
    public List<V2> Area { get; set; } = new();
    /// <summary>Margine aggiunto attorno all'area (m).</summary>
    public double Margin { get; set; }
    /// <summary>true = le geometrie vengono tagliate sul bordo; false = si tengono intere quelle che toccano l'area.</summary>
    public bool Cut { get; set; }
    /// <summary>Con Cut attivo, i livelli di edifici restano comunque interi (per ricalcarne i muri).</summary>
    public bool KeepBuildingsWhole { get; set; }
    /// <summary>Sistema di riferimento da dichiarare per i livelli che non ne hanno uno (es. shapefile senza .prj).</summary>
    public SrsInfo? DefaultSrs { get; set; }
}

public record ClipLayerResult(string SourceName, string OutName, int Features);

public record ClipResult(string Path, List<ClipLayerResult> Layers, long Bytes);

/// <summary>Scrive un GeoPackage con i soli livelli scelti, limitati all'area di interesse.</summary>
public static class GpkgClipper
{
    static readonly GeometryFactory Gf = new();
    static string Q(string s) => GpkgReader.Q(s);

    public static Polygon AreaPolygon(ClipOptions o)
    {
        var pts = o.Area.Select(p => new Coordinate(p.X, p.Y)).ToList();
        if (pts.Count < 3) throw new InvalidOperationException("Disegna prima l'area di interesse.");
        if (!pts[0].Equals2D(pts[^1])) pts.Add(pts[0].Copy());
        NtsGeometry g = Gf.CreatePolygon(pts.ToArray());
        if (!g.IsValid) g = g.Buffer(0);
        if (o.Margin > 0) g = g.Buffer(o.Margin, new NetTopologySuite.Operation.Buffer.BufferParameters { JoinStyle = NetTopologySuite.Operation.Buffer.JoinStyle.Mitre, MitreLimit = 3 });
        return g switch
        {
            Polygon p => p,
            MultiPolygon mp => (Polygon)mp.Geometries.OrderByDescending(x => x.Area).First(),
            _ => throw new InvalidOperationException("L'area disegnata non è valida.")
        };
    }

    /// <summary>Conta gli elementi di ogni livello che cadono nell'area (per l'anteprima del risultato).</summary>
    public static int CountInside(SourceLayer l, ClipOptions o, CancellationToken ct)
    {
        var area = AreaPolygon(o);
        var prep = PreparedGeometryFactory.Prepare(area);
        var env = area.EnvelopeInternal;
        int n = 0;
        foreach (var (g, _) in SourceData.Features(l, new BBox(env.MinX, env.MinY, env.MaxX, env.MaxY), ct))
            if (prep.Intersects(g)) n++;
        return n;
    }

    public static ClipResult Write(string outPath, IReadOnlyList<SourceLayer> layers, ClipOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        var area = AreaPolygon(o);
        var prep = PreparedGeometryFactory.Prepare(area);
        var env = area.EnvelopeInternal;
        var box = new BBox(env.MinX, env.MinY, env.MaxX, env.MaxY);

        var tmp = outPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        var results = new List<ClipLayerResult>();
        SqliteConnection.ClearAllPools();
        using (var con = new SqliteConnection($"Data Source={tmp};Pooling=False"))
        {
            con.Open();
            Exec(con, "PRAGMA application_id = 1196444487");
            Exec(con, "PRAGMA user_version = 10300");
            Exec(con, "PRAGMA journal_mode = OFF");
            Exec(con, "PRAGMA synchronous = OFF");
            CreateMeta(con);

            var srsWritten = new HashSet<int>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int idx = 0;
            foreach (var l in layers)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                progress?.Report($"Livello {idx} di {layers.Count}: {l.Name}…");
                var srs = l.Srs is { Id: > 0 or -100 } s0 ? s0 : o.DefaultSrs;
                int srsId = WriteSrs(con, srs, srsWritten);

                string outName = SafeName(l.Name);
                for (int k = 2; used.Contains(outName); k++) outName = SafeName(l.Name) + "_" + k;
                used.Add(outName);

                bool cut = o.Cut && !(o.KeepBuildingsWhole && GpkgReader.IsBuildingName(l.Name));
                // raccolta degli elementi nell'area
                var feats = new List<(NtsGeometry g, object?[] a)>();
                foreach (var (g0, a) in SourceData.Features(l, box, ct))
                {
                    if (!prep.Intersects(g0)) continue;
                    var g = g0;
                    if (cut && !prep.ContainsProperly(g0))
                    {
                        try { g = area.Intersection(g0.IsValid ? g0 : g0.Buffer(0)); } catch { g = g0; }
                        g = KeepDimension(g, g0.Dimension);
                        if (g == null || g.IsEmpty) continue;
                    }
                    feats.Add((g, a));
                }

                if (feats.Count == 0) { results.Add(new ClipLayerResult(l.Name, "", 0)); continue; }
                string type = l.GeomType.ToUpperInvariant();
                if (cut && feats.Any(f => f.g is GeometryCollection) && !type.StartsWith("MULTI") && type != "GEOMETRY")
                    type = "MULTI" + type;
                bool hasZ = l.HasZ || feats.Any(f => !double.IsNaN(f.g.Coordinate?.Z ?? double.NaN));
                bool hasM = false; // la M si perde nel ritaglio: non la si dichiara

                // tabella
                var colDefs = new List<string> { "\"fid\" INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL", $"\"geom\" {type}" };
                var outCols = new List<string>();
                foreach (var c in l.Columns)
                {
                    var name = c.Name;
                    if (name.Equals("fid", StringComparison.OrdinalIgnoreCase) || name.Equals("geom", StringComparison.OrdinalIgnoreCase)) name = "src_" + name;
                    outCols.Add(name);
                    colDefs.Add($"{Q(name)} {c.SqlType}");
                }
                Exec(con, $"CREATE TABLE {Q(outName)} ({string.Join(", ", colDefs)})");

                var writer = new WKBWriter(ByteOrder.LittleEndian, false, hasZ, hasM) { HandleOrdinates = hasZ ? Ordinates.XYZ : Ordinates.XY };
                var lbox = BBox.Empty;
                using (var tx = con.BeginTransaction())
                {
                    using var ins = con.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = $"INSERT INTO {Q(outName)} (\"geom\"{string.Concat(outCols.Select(c => ", " + Q(c)))}) VALUES ($g{string.Concat(outCols.Select((_, i) => $", $a{i}"))})";
                    var pg = ins.Parameters.Add("$g", SqliteType.Blob);
                    var pa = outCols.Select((_, i) => ins.Parameters.Add($"$a{i}", SqliteType.Text)).ToArray();
                    foreach (var (g, a) in feats)
                    {
                        var gg = type.StartsWith("MULTI") ? ToMulti(g) : g;
                        var e = gg.EnvelopeInternal;
                        lbox = lbox.Union(new BBox(e.MinX, e.MinY, e.MaxX, e.MaxY));
                        pg.Value = Blob(gg, srsId, writer);
                        for (int i = 0; i < pa.Length; i++)
                        {
                            pa[i].SqliteType = a[i] switch
                            {
                                null => SqliteType.Text,
                                long or int or short or byte or bool => SqliteType.Integer,
                                double or float or decimal => SqliteType.Real,
                                byte[] => SqliteType.Blob,
                                _ => SqliteType.Text
                            };
                            pa[i].Value = a[i] switch { null => DBNull.Value, double dn when !double.IsFinite(dn) => DBNull.Value, float fn when !float.IsFinite(fn) => DBNull.Value, DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss"), _ => a[i] };
                        }
                        ins.ExecuteNonQuery();
                    }
                    tx.Commit();
                }

                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO gpkg_contents (table_name, data_type, identifier, description, min_x, min_y, max_x, max_y, srs_id)
                                        VALUES ($t, 'features', $t, $d, $x0, $y0, $x1, $y1, $s)";
                    cmd.Parameters.AddWithValue("$t", outName);
                    cmd.Parameters.AddWithValue("$d", $"Ritaglio di {l.Name} ({l.FileName})");
                    cmd.Parameters.AddWithValue("$x0", lbox.IsEmpty ? DBNull.Value : lbox.MinX);
                    cmd.Parameters.AddWithValue("$y0", lbox.IsEmpty ? DBNull.Value : lbox.MinY);
                    cmd.Parameters.AddWithValue("$x1", lbox.IsEmpty ? DBNull.Value : lbox.MaxX);
                    cmd.Parameters.AddWithValue("$y1", lbox.IsEmpty ? DBNull.Value : lbox.MaxY);
                    cmd.Parameters.AddWithValue("$s", srsId);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO gpkg_geometry_columns VALUES ($t, 'geom', $type, $s, $z, $m)";
                    cmd.Parameters.AddWithValue("$t", outName);
                    cmd.Parameters.AddWithValue("$type", type);
                    cmd.Parameters.AddWithValue("$s", srsId);
                    cmd.Parameters.AddWithValue("$z", hasZ ? 1 : 0);
                    cmd.Parameters.AddWithValue("$m", 0);
                    cmd.ExecuteNonQuery();
                }
                results.Add(new ClipLayerResult(l.Name, outName, feats.Count));
            }
            progress?.Report("Compattazione del file…");
            Exec(con, "VACUUM");
        }
        SqliteConnection.ClearAllPools();
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
        return new ClipResult(outPath, results, new FileInfo(outPath).Length);
    }

    static NtsGeometry? KeepDimension(NtsGeometry g, Dimension dim)
    {
        // l'intersezione può produrre pezzi di dimensione inferiore (punti di contatto): si scartano
        if (g is GeometryCollection gc && g.GetType() == typeof(GeometryCollection))
        {
            var parts = gc.Geometries.Where(x => x.Dimension == dim && !x.IsEmpty).ToArray();
            if (parts.Length == 0) return null;
            return Gf.BuildGeometry(parts.SelectMany(p => p is GeometryCollection c ? c.Geometries : new[] { p }));
        }
        return g.Dimension == dim ? g : null;
    }

    static NtsGeometry ToMulti(NtsGeometry g) => g switch
    {
        Polygon p => Gf.CreateMultiPolygon(new[] { p }),
        LineString ls => Gf.CreateMultiLineString(new[] { ls }),
        Point pt => Gf.CreateMultiPoint(new[] { pt }),
        _ => g
    };

    static byte[] Blob(NtsGeometry g, int srsId, WKBWriter w)
    {
        var wkb = w.Write(g);
        var e = g.EnvelopeInternal;
        var b = new byte[8 + 32 + wkb.Length];
        b[0] = (byte)'G'; b[1] = (byte)'P'; b[2] = 0;
        b[3] = 0b0000_0011; // envelope XY, little endian
        BitConverter.GetBytes(srsId).CopyTo(b, 4);
        BitConverter.GetBytes(e.MinX).CopyTo(b, 8);
        BitConverter.GetBytes(e.MaxX).CopyTo(b, 16);
        BitConverter.GetBytes(e.MinY).CopyTo(b, 24);
        BitConverter.GetBytes(e.MaxY).CopyTo(b, 32);
        wkb.CopyTo(b, 40);
        return b;
    }

    static string SafeName(string n)
    {
        var s = new string(n.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (s.Length == 0 || char.IsDigit(s[0])) s = "L_" + s;
        if (s.StartsWith("gpkg_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("rtree_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            s = "L_" + s;
        return s;
    }

    static int WriteSrs(SqliteConnection con, SrsInfo? srs, HashSet<int> written)
    {
        if (srs == null) return -1;
        int id = srs.Id;
        if (srs.Org == "PRJ")
        {
            // shapefile con .prj: si cerca un codice EPSG nel testo, altrimenti si registra come definizione libera
            var m = System.Text.RegularExpressions.Regex.Match(srs.Definition, "AUTHORITY\\[\"EPSG\",\\s*\"?(\\d+)\"?\\]\\s*\\]\\s*$");
            id = m.Success ? int.Parse(m.Groups[1].Value) : 900001;
            srs = srs with { Id = id, Org = m.Success ? "EPSG" : "NONE", OrgId = m.Success ? id : id };
        }
        if (id <= 0 || written.Contains(id)) return id;
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO gpkg_spatial_ref_sys VALUES ($n, $id, $o, $oid, $d, NULL)";
        cmd.Parameters.AddWithValue("$n", string.IsNullOrEmpty(srs.Name) ? $"EPSG:{id}" : srs.Name);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$o", srs.Org);
        cmd.Parameters.AddWithValue("$oid", srs.OrgId);
        cmd.Parameters.AddWithValue("$d", string.IsNullOrEmpty(srs.Definition) ? "undefined" : srs.Definition);
        cmd.ExecuteNonQuery();
        written.Add(id);
        return id;
    }

    static void CreateMeta(SqliteConnection con)
    {
        Exec(con, @"CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER NOT NULL PRIMARY KEY, organization TEXT NOT NULL,
                    organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT)");
        Exec(con, @"INSERT INTO gpkg_spatial_ref_sys VALUES
                    ('Undefined cartesian SRS', -1, 'NONE', -1, 'undefined', 'undefined cartesian coordinate reference system'),
                    ('Undefined geographic SRS', 0, 'NONE', 0, 'undefined', 'undefined geographic coordinate reference system'),
                    ('WGS 84 geodetic', 4326, 'EPSG', 4326, 'GEOGCS[""WGS 84"",DATUM[""WGS_1984"",SPHEROID[""WGS 84"",6378137,298.257223563,AUTHORITY[""EPSG"",""7030""]],AUTHORITY[""EPSG"",""6326""]],PRIMEM[""Greenwich"",0,AUTHORITY[""EPSG"",""8901""]],UNIT[""degree"",0.0174532925199433,AUTHORITY[""EPSG"",""9122""]],AXIS[""Latitude"",NORTH],AXIS[""Longitude"",EAST],AUTHORITY[""EPSG"",""4326""]]', 'longitude/latitude coordinates in decimal degrees on the WGS 84 spheroid')");
        Exec(con, @"CREATE TABLE gpkg_contents (table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE, description TEXT DEFAULT '',
                    last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE,
                    srs_id INTEGER, CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id))");
        Exec(con, @"CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL,
                    srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL, CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name),
                    CONSTRAINT fk_gc_tn FOREIGN KEY (table_name) REFERENCES gpkg_contents(table_name),
                    CONSTRAINT fk_gc_srs FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys (srs_id))");
    }

    static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
