using System.Text.Json;
using System.Text.Json.Serialization;
using CicloDraft.Geometry;
using CicloDraft.Model;

namespace CicloDraft.IO;

/// <summary>Salvataggio del progetto (.ciclo, JSON). Le coordinate sono assolute (CRS del GeoPackage).</summary>
public static class ProjectFile
{
    public class Dto
    {
        public int Version { get; set; } = 1;
        public string? GpkgPath { get; set; }
        public double[]? Origin { get; set; }
        public Dictionary<string, int> LayerRoles { get; set; } = new();
        /// <summary>Livelli nascosti alla vista.</summary>
        public List<string> HiddenLayers { get; set; } = new();
        public List<EdgeDto> Edges { get; set; } = new();
        public List<ElementDto> Elements { get; set; } = new();
        public int NextId { get; set; }
        public double ViewRotation { get; set; }
        public List<PhotoDto>? Photos { get; set; }
    }
    public class PhotoDto
    {
        public string Path { get; set; } = "";
        /// <summary>Percorso relativo alla cartella del progetto (se il progetto viene spostato).</summary>
        public string? RelPath { get; set; }
        public string Name { get; set; } = "";
    }
    public class EdgeDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<double[]> Points { get; set; } = new();
        /// <summary>Punti agganciati agli elementi (coordinate assolute).</summary>
        public List<EdgeAnchor>? Anchors { get; set; }
        public int Owner { get; set; } = -1;
    }
    public class ElementDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ElementType Type { get; set; }
        public ElementMode Mode { get; set; }
        public EdgeKind BaseKind { get; set; }
        public int BaseId { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public int Side { get; set; }
        public double Width { get; set; }
        public double Smooth { get; set; } = 1.0;
        public bool ToEnd { get; set; }
        public List<double[]> Profile { get; set; } = new();
        /// <summary>Variatori di sezione: [inizio, fine, variazione] in metri.</summary>
        public List<double[]>? Variators { get; set; }
        public EdgeKind? OtherKind { get; set; }
        public int? OtherId { get; set; }
        public bool Centered { get; set; }
        public int Dividers { get; set; } = -1;
        public DividerStyle DividerStyle { get; set; }
        public bool? StallLines { get; set; }
        public double? StallLength { get; set; }
    }

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string path, Project p, GpkgData? data)
    {
        V2 o = data?.Origin ?? new V2(0, 0);
        var dto = new Dto
        {
            GpkgPath = data?.Path,
            Origin = new[] { o.X, o.Y },
            NextId = p.NextId,
            ViewRotation = p.ViewRotation,
            Photos = p.Photos.Count == 0 ? null : p.Photos.Select(f => new PhotoDto
            {
                Path = f.Path, Name = f.Name,
                RelPath = Rel(Path.GetDirectoryName(Path.GetFullPath(path)), f.Path)
            }).ToList(),
            LayerRoles = data?.Layers.ToDictionary(l => l.Name, l => l.Role) ?? new(),
            HiddenLayers = data?.Layers.Where(l => !l.Visible).Select(l => l.Name).ToList() ?? new(),
            Edges = p.Edges.Select(e => new EdgeDto
            {
                Id = e.Id, Name = e.Name,
                Points = e.Points.Select(v => new[] { v.X + o.X, v.Y + o.Y }).ToList(),
                Anchors = e.Anchors?.Select(a => new EdgeAnchor { X = a.X + o.X, Y = a.Y + o.Y, El = a.El, S = a.S, Follow = a.Follow, Sd = a.Sd, Auto = a.Auto }).ToList(),
                Owner = e.Owner
            }).ToList(),
            Elements = p.Elements.Select(e => new ElementDto
            {
                Id = e.Id, Name = e.Name, Type = e.Type, Mode = e.Mode,
                BaseKind = e.Base.Kind, BaseId = e.Base.Id, Start = e.Start, End = e.End,
                Side = e.Side, Width = e.Width, Smooth = e.Smooth, ToEnd = e.ToEnd,
                Profile = e.Profile.Select(w => new[] { w.T, w.W }).ToList(),
                Variators = e.Variators.Count == 0 ? null : e.Variators.Select(v => new[] { v.T1, v.T2, v.Delta }).ToList(),
                OtherKind = e.Other?.Kind, OtherId = e.Other?.Id, Centered = e.Centered,
                Dividers = e.Dividers, DividerStyle = e.DividerStyle,
                StallLines = e.Type == ElementType.Parcheggio ? e.StallLines : null,
                StallLength = e.Type == ElementType.Parcheggio ? e.StallLength : null
            }).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto, Opts));
    }

    static string? Rel(string? dir, string file)
    {
        try { return dir == null ? null : Path.GetRelativePath(dir, file); } catch { return null; }
    }

    /// <summary>Foto del progetto: percorso salvato, oppure relativo alla cartella del progetto, oppure accanto al progetto.</summary>
    public static void LoadPhotos(Dto dto, Project p, string projectPath, V2 origin)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";
        foreach (var f in dto.Photos ?? new())
        {
            string path = f.Path;
            if (!File.Exists(path) && f.RelPath != null && File.Exists(Path.Combine(dir, f.RelPath))) path = Path.GetFullPath(Path.Combine(dir, f.RelPath));
            if (!File.Exists(path) && File.Exists(Path.Combine(dir, Path.GetFileName(f.Path)))) path = Path.Combine(dir, Path.GetFileName(f.Path));
            var r = new PhotoInsertRef { Path = path, Name = f.Name };
            RefreshPhoto(r, origin);
            p.Photos.Add(r);
        }
    }

    public static string PhotoSidecar(string photo) => Path.ChangeExtension(photo, null) + ".fotoinserimento.json";

    /// <summary>Rilegge dai file accanto alla foto la fotocamera e il numero di punti.</summary>
    public static void RefreshPhoto(PhotoInsertRef r, V2 origin)
    {
        r.Missing = !File.Exists(r.Path);
        r.Cam = null; r.Pairs = 0;
        try
        {
            var sc = PhotoSidecar(r.Path);
            if (!File.Exists(sc)) return;
            var f = PhotoInsertFile.Load(sc);
            r.Pairs = f.Pairs.Count;
            if (f.Camera is { } c)
            {
                r.Cam = new V2(c.X - origin.X, c.Y - origin.Y);
                r.Yaw = c.Yaw;
                r.HFov = c.W > 0 && c.F > 0 ? c.HFov : 60;
            }
        }
        catch { }
    }

    public static Dto Read(string path) =>
        JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Opts) ?? throw new InvalidDataException("File di progetto non valido");

    /// <summary>Riempie il progetto dal DTO, convertendo in coordinate locali rispetto a origin.</summary>
    public static Project ToProject(Dto dto, V2 origin)
    {
        var p = new Project { NextId = Math.Max(1, dto.NextId), ViewRotation = dto.ViewRotation };
        foreach (var e in dto.Edges)
        {
            var target = p.Edges.FirstOrDefault(x => x.Id == e.Id);
            if (target == null) { target = new RoadEdge { Id = e.Id }; p.Edges.Add(target); }
            target.Name = e.Name;
            target.Points = e.Points.Select(a => new V2(a[0] - origin.X, a[1] - origin.Y)).ToList();
            target.Anchors = e.Anchors?.Select(a => new EdgeAnchor { X = a.X - origin.X, Y = a.Y - origin.Y, El = a.El, S = a.S, Follow = a.Follow, Sd = a.Sd == 0 ? 1 : a.Sd, Auto = a.Auto }).ToList();
            target.Owner = e.Owner;
        }
        foreach (var d in dto.Elements)
        {
            p.Elements.Add(new Element
            {
                Id = d.Id, Name = d.Name, Type = d.Type, Mode = d.Mode,
                Base = new EdgeRef(d.BaseKind, d.BaseId), Start = d.Start, End = d.End,
                Side = d.Side == 0 ? 1 : d.Side, Width = d.Width, Smooth = d.Smooth, ToEnd = d.ToEnd,
                Profile = d.Profile.Select(a => new WidthPoint { T = a[0], W = a[1] }).ToList(),
                Variators = d.Variators?.Where(a => a.Length >= 3).Select(a => new SectionVariator { T1 = a[0], T2 = a[1], Delta = a[2] }).ToList() ?? new(),
                Other = d.OtherKind is { } k && d.OtherId is { } id ? new EdgeRef(k, id) : null,
                Centered = d.Centered, Dividers = d.Dividers, DividerStyle = d.DividerStyle,
                StallLines = d.StallLines ?? true, StallLength = d.StallLength is > 0 ? d.StallLength.Value : 5.0
            });
            p.NextId = Math.Max(p.NextId, d.Id + 1);
        }
        p.Regenerate();
        return p;
    }
}
