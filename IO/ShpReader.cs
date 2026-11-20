using System.Globalization;
using System.Text;
using CicloDraft.Geometry;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace CicloDraft.IO;

/// <summary>Campo di un DBF.</summary>
public record DbfField(string Name, char Type, int Length, int Decimals)
{
    /// <summary>Tipo SQLite/GeoPackage corrispondente.</summary>
    public string SqlType => Type switch
    {
        'N' when Decimals == 0 && Length <= 18 => "INTEGER",
        'N' or 'F' => "DOUBLE",
        'L' => "BOOLEAN",
        'D' => "DATE",
        _ => "TEXT"
    };
}

/// <summary>Lettore minimo di shapefile (punti, linee, poligoni, anche Z/M) con attributi DBF.</summary>
public class ShpReader
{
    public string Path { get; }
    public int ShapeType { get; private set; }
    public BBox Box { get; private set; }
    public int Count { get; private set; }
    public List<DbfField> Fields { get; } = new();
    public string? PrjWkt { get; private set; }

    static readonly GeometryFactory Gf = new();

    public ShpReader(string path)
    {
        Path = path;
        using var fs = File.OpenRead(path);
        var h = new byte[100];
        fs.ReadExactly(h);
        ShapeType = BitConverter.ToInt32(h, 32);
        Box = new BBox(BitConverter.ToDouble(h, 36), BitConverter.ToDouble(h, 44), BitConverter.ToDouble(h, 52), BitConverter.ToDouble(h, 60));
        var prj = System.IO.Path.ChangeExtension(path, ".prj");
        if (File.Exists(prj)) PrjWkt = File.ReadAllText(prj);
        var dbf = DbfPath;
        if (dbf != null)
        {
            using var ds = File.OpenRead(dbf);
            var dh = new byte[32];
            ds.ReadExactly(dh);
            Count = BitConverter.ToInt32(dh, 4);
            int headerLen = BitConverter.ToUInt16(dh, 8);
            var fh = new byte[headerLen - 32];
            ds.ReadExactly(fh);
            for (int i = 0; i + 32 <= fh.Length && fh[i] != 0x0D; i += 32)
            {
                var name = Encoding.ASCII.GetString(fh, i, 11).Split('\0')[0].Trim();
                Fields.Add(new DbfField(name, (char)fh[i + 11], fh[i + 16], fh[i + 17]));
            }
        }
        else
        {
            // senza DBF si contano i record dell'indice .shx
            var shx = System.IO.Path.ChangeExtension(path, ".shx");
            Count = File.Exists(shx) ? (int)((new FileInfo(shx).Length - 100) / 8) : 0;
        }
    }

    string? DbfPath
    {
        get
        {
            var d = System.IO.Path.ChangeExtension(Path, ".dbf");
            if (File.Exists(d)) return d;
            d = System.IO.Path.ChangeExtension(Path, ".DBF");
            return File.Exists(d) ? d : null;
        }
    }

    public string GeometryTypeName => (ShapeType % 10) switch
    {
        1 => "POINT",
        8 => "MULTIPOINT",
        3 => "MULTILINESTRING",
        5 => "MULTIPOLYGON",
        _ => "GEOMETRY"
    };

    public bool HasZ => ShapeType is 11 or 13 or 15 or 18;

    Encoding DbfEncoding()
    {
        var cpg = System.IO.Path.ChangeExtension(Path, ".cpg");
        if (File.Exists(cpg))
        {
            var t = File.ReadAllText(cpg).Trim().ToUpperInvariant();
            if (t.Contains("UTF")) return Encoding.UTF8;
        }
        return Encoding.Latin1;
    }

    /// <summary>Legge tutti i record: geometria e valori degli attributi (nell'ordine di Fields).</summary>
    public IEnumerable<(NtsGeometry? geom, object?[] attrs)> Read()
    {
        using var fs = File.OpenRead(Path);
        using var br = new BinaryReader(fs);
        fs.Position = 100;
        FileStream? ds = null; int recLen = 0; var enc = DbfEncoding();
        var dbf = DbfPath;
        if (dbf != null)
        {
            ds = File.OpenRead(dbf);
            var dh = new byte[32]; ds.ReadExactly(dh);
            int headerLen = BitConverter.ToUInt16(dh, 8);
            recLen = BitConverter.ToUInt16(dh, 10);
            ds.Position = headerLen;
        }
        try
        {
            while (fs.Position + 8 <= fs.Length)
            {
                var rh = br.ReadBytes(8);
                int len = (rh[4] << 24 | rh[5] << 16 | rh[6] << 8 | rh[7]) * 2;
                var content = br.ReadBytes(len);
                NtsGeometry? g = null;
                try { g = ParseShape(content); } catch { }
                object?[] attrs = Array.Empty<object?>();
                if (ds != null)
                {
                    var rec = new byte[recLen];
                    if (ds.Read(rec, 0, recLen) == recLen) attrs = ParseRecord(rec, enc);
                }
                yield return (g, attrs);
            }
        }
        finally { ds?.Dispose(); }
    }

    object?[] ParseRecord(byte[] rec, Encoding enc)
    {
        var v = new object?[Fields.Count];
        int off = 1;
        for (int i = 0; i < Fields.Count; i++)
        {
            var f = Fields[i];
            var raw = enc.GetString(rec, off, f.Length).Trim();
            off += f.Length;
            if (raw.Length == 0 || raw.All(c => c == '*')) { v[i] = null; continue; }
            switch (f.SqlType)
            {
                case "INTEGER":
                    v[i] = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null; break;
                case "DOUBLE":
                    v[i] = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null; break;
                case "BOOLEAN":
                    v[i] = "YyTt".Contains(raw[0]) ? 1 : "NnFf".Contains(raw[0]) ? 0 : null; break;
                case "DATE":
                    v[i] = raw.Length == 8 ? $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}" : raw; break;
                default:
                    v[i] = raw; break;
            }
        }
        return v;
    }

    static NtsGeometry? ParseShape(byte[] c)
    {
        if (c.Length < 4) return null;
        int type = BitConverter.ToInt32(c, 0);
        if (type == 0) return null;
        int baseType = type % 10;
        bool hasZ = type is 11 or 13 or 15 or 18;
        if (baseType == 1)
        {
            double x = BitConverter.ToDouble(c, 4), y = BitConverter.ToDouble(c, 12);
            double z = hasZ ? BitConverter.ToDouble(c, 20) : Coordinate.NullOrdinate;
            return Gf.CreatePoint(hasZ ? new CoordinateZ(x, y, z) : new Coordinate(x, y));
        }
        if (baseType == 8)
        {
            int n = BitConverter.ToInt32(c, 36);
            var pts = new Point[n];
            for (int i = 0; i < n; i++) pts[i] = Gf.CreatePoint(new Coordinate(BitConverter.ToDouble(c, 40 + 16 * i), BitConverter.ToDouble(c, 48 + 16 * i)));
            return Gf.CreateMultiPoint(pts);
        }
        if (baseType != 3 && baseType != 5) return null;
        int numParts = BitConverter.ToInt32(c, 36), numPoints = BitConverter.ToInt32(c, 40);
        var parts = new int[numParts + 1];
        for (int i = 0; i < numParts; i++) parts[i] = BitConverter.ToInt32(c, 44 + 4 * i);
        parts[numParts] = numPoints;
        int ptsOff = 44 + 4 * numParts;
        int zOff = ptsOff + 16 * numPoints + 16;
        var seqs = new List<Coordinate[]>();
        for (int p = 0; p < numParts; p++)
        {
            var arr = new Coordinate[parts[p + 1] - parts[p]];
            for (int i = parts[p], k = 0; i < parts[p + 1]; i++, k++)
            {
                double x = BitConverter.ToDouble(c, ptsOff + 16 * i), y = BitConverter.ToDouble(c, ptsOff + 16 * i + 8);
                arr[k] = hasZ && zOff + 8 * i + 8 <= c.Length ? new CoordinateZ(x, y, BitConverter.ToDouble(c, zOff + 8 * i)) : new Coordinate(x, y);
            }
            seqs.Add(arr);
        }
        if (baseType == 3)
            return Gf.CreateMultiLineString(seqs.Where(s => s.Length >= 2).Select(s => Gf.CreateLineString(s)).ToArray());

        // Poligoni: anelli orari = contorni esterni, antiorari = buchi
        var shells = new List<(LinearRing ring, List<LinearRing> holes)>();
        var holes = new List<LinearRing>();
        foreach (var s in seqs)
        {
            if (s.Length < 4) continue;
            var arr = s;
            if (!arr[0].Equals2D(arr[^1])) arr = arr.Append(arr[0].Copy()).ToArray();
            var lr = Gf.CreateLinearRing(arr);
            if (Orientation.IsCCW(lr.CoordinateSequence)) holes.Add(lr); else shells.Add((lr, new List<LinearRing>()));
        }
        if (shells.Count == 0) // file con orientamento non standard
        {
            shells.AddRange(holes.Select(h => (h, new List<LinearRing>())));
            holes.Clear();
        }
        foreach (var h in holes)
        {
            var hp = Gf.CreatePolygon(h);
            var owner = shells.FirstOrDefault(sh => Gf.CreatePolygon(sh.ring).Contains(hp.InteriorPoint));
            if (owner.ring != null) owner.holes.Add(h); else shells.Add((h, new List<LinearRing>()));
        }
        return Gf.CreateMultiPolygon(shells.Select(sh => Gf.CreatePolygon(sh.ring, sh.holes.ToArray())).ToArray());
    }
}
