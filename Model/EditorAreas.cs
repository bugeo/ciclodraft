using CicloDraft.Geometry;
using CicloDraft.IO;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

namespace CicloDraft.Model;

/// <summary>
/// Aree libere (doppio clic su una parte della mappa senza elementi → «Crea elemento») e
/// selezione multipla (Maiusc+clic) per unire più elementi in un solo poligono.
/// </summary>
public partial class Editor
{
    static readonly GeometryFactory AreaGf = new();

    /// <summary>Parti scelte sulla mappa (doppio clic, poi Ctrl+clic per aggiungerne): aree dei livelli o spazi liberi.</summary>
    public readonly List<List<V2>> AreaParts = new();
    /// <summary>Elementi disegnati inclusi nella scelta (verranno uniti nel nuovo poligono).</summary>
    public readonly List<Element> MultiSel = new();
    /// <summary>Contorni uniti della scelta: uno per ogni gruppo di parti che si toccano.</summary>
    public readonly List<List<V2>> PickedRings = new();
    public bool HasPick => AreaParts.Count > 0 || MultiSel.Count > 0;
    /// <summary>Le parti scelte racchiudono un vuoto (un elemento non può avere buchi).</summary>
    public bool PickHole;

    /// <summary>L'elemento sotto il punto: il più piccolo (sulla mappa sta sopra agli altri).</summary>
    public Element? ElementAt(V2 w) =>
        Project.Shown.Where(e => e.Polygon.Count >= 3 && PL.PointInRing(e.Polygon, w)).OrderBy(e => e.Metrics.Area).FirstOrDefault();

    static Polygon? ToPoly(IReadOnlyList<V2> ring)
    {
        if (ring.Count < 3) return null;
        var cs = ring.Select(p => new Coordinate(p.X, p.Y)).Append(new Coordinate(ring[0].X, ring[0].Y)).ToArray();
        try { var g = AreaGf.CreatePolygon(cs); return g.IsValid ? g : g.Buffer(0) as Polygon; } catch { return null; }
    }

    static List<V2> RingOf(Polygon p) =>
        p.ExteriorRing.Coordinates.Take(p.ExteriorRing.NumPoints - 1).Select(c => new V2(c.X, c.Y)).ToList();

    /// <summary>Toglie i doppioni e i vertici quasi allineati (sotto il centimetro); il contorno resta chiuso implicitamente.</summary>
    static List<V2> CleanRing(List<V2> pts)
    {
        var ring = PL.Dedup(new List<V2>(pts));
        if (ring.Count >= 2 && V2.Dist(ring[0], ring[^1]) < 1e-6) ring.RemoveAt(ring.Count - 1);
        var simp = PL.Simplify(new List<V2>(ring) { ring[0] }, 0.01);
        if (simp.Count >= 4) { simp.RemoveAt(simp.Count - 1); ring = simp; }
        return ring;
    }

    /// <summary>Spazio libero attorno al punto, delimitato da edifici ed elementi (entro 60 m).</summary>
    List<V2>? FreeSpaceAt(V2 w)
    {
        if (Data?.InsideBuilding(w) == true) return null;
        const double R = 60;
        var box = new BBox(w.X - R, w.Y - R, w.X + R, w.Y + R);
        var obstacles = new List<NetTopologySuite.Geometries.Geometry>();
        if (Data != null)
            foreach (var (_, _, r) in Data.RingsWithRole(LayerRole.Muri))
                if (r.Closed && r.Box.Intersects(box) && ToPoly(r.Pts) is { } p) obstacles.Add(p);
        foreach (var e in Project.Shown)
            if (e.Polygon.Count >= 3 && ToPoly(e.Polygon) is { } p && p.EnvelopeInternal.Intersects(new Envelope(box.MinX, box.MaxX, box.MinY, box.MaxY))) obstacles.Add(p);
        NetTopologySuite.Geometries.Geometry area = AreaGf.ToGeometry(new Envelope(box.MinX, box.MaxX, box.MinY, box.MaxY));
        try
        {
            if (obstacles.Count > 0) area = area.Difference(CascadedPolygonUnion.Union(obstacles));
        }
        catch { return null; }
        var pt = AreaGf.CreatePoint(new Coordinate(w.X, w.Y));
        for (int i = 0; i < area.NumGeometries; i++)
            if (area.GetGeometryN(i) is Polygon pg && pg.Contains(pt) && pg.Area > 0.05)
                return CleanRing(RingOf(pg));
        return null;
    }

    // ───────────────────────── Scelta delle aree ─────────────────────────

    /// <summary>Area della mappa sotto il punto: il più piccolo poligono dei livelli visibili (non gli edifici),
    /// altrimenti lo spazio libero tra edifici ed elementi disegnati.</summary>
    List<V2>? AreaAt(V2 w)
    {
        if (Data != null)
        {
            var best = Data.Layers
                .Where(l => l.Visible && l.Role != 0 && l.Role != (int)LayerRole.Muri && l.Role != (int)LayerRole.Edifici3D)
                .SelectMany(l => l.Rings)
                .Where(r => r.Closed && r.Pts.Count >= 3 && r.Box.Contains(w) && PL.PointInRing(r.Pts, w))
                .Select(r => (r, a: Math.Abs(PL.RingArea(r.Pts))))
                .Where(x => x.a > 0.05)
                .OrderBy(x => x.a).FirstOrDefault();
            if (best.r != null) return CleanRing(best.r.Pts);
        }
        return FreeSpaceAt(w);
    }

    /// <summary>
    /// Doppio clic (add = false) comincia una nuova scelta; Ctrl+clic (add = true) aggiunge o toglie la parte sotto il punto.
    /// Sopra un elemento disegnato si sceglie l'elemento, altrove l'area della mappa.
    /// </summary>
    public void PickAt(V2 w, bool add)
    {
        SelectedSection = null; VarSel = -1;
        GroupSel.Clear();
        if (!add) { AreaParts.Clear(); MultiSel.Clear(); }
        // (un elemento già selezionato non entra nella scelta delle aree: gli elementi disegnati vanno nei gruppi)
        Selected = null;
        if (ElementAt(w) is { } e)
        {
            if (!MultiSel.Remove(e)) MultiSel.Add(e);
        }
        else
        {
            int i = AreaParts.FindIndex(r => PL.PointInRing(r, w));
            if (i >= 0) AreaParts.RemoveAt(i);
            else if (AreaAt(w) is { } ring) AreaParts.Add(ring);
        }
        UpdatePick();
        Message = HasPick && PickBlocker() is { } why && why != "Niente di scelto." ? why : "";
        NotifyAll();
    }

    void UpdatePick()
    {
        PickedRings.Clear(); PickHole = false;
        var polys = AreaParts.Select(ToPoly).Concat(MultiSel.Select(e => ToPoly(e.Polygon)))
            .Where(p => p != null).Cast<NetTopologySuite.Geometries.Geometry>().ToList();
        if (polys.Count == 0) return;
        try
        {
            var u = polys.Count == 1 ? polys[0] : CascadedPolygonUnion.Union(polys);
            // piccole fessure tra parti accostate (sotto i 5 cm) si chiudono
            if (polys.Count > 1) u = u.Buffer(0.05).Buffer(-0.05);
            for (int i = 0; i < u.NumGeometries; i++)
                if (u.GetGeometryN(i) is Polygon pg && pg.Area > 0.05)
                {
                    PickedRings.Add(CleanRing(RingOf(pg)));
                    if (pg.InteriorRings.Any(h => Math.Abs(NetTopologySuite.Algorithm.Area.OfRing(h.CoordinateSequence)) > 1.0)) PickHole = true;
                }
        }
        catch { PickedRings.Clear(); }
    }

    public void ClearPick() { AreaParts.Clear(); MultiSel.Clear(); PickedRings.Clear(); PickHole = false; GroupSel.Clear(); }

    /// <summary>Perché non si possono creare gli elementi dalla scelta (null = si può).</summary>
    public string? PickBlocker()
    {
        if (!HasPick || PickedRings.Count == 0) return "Niente di scelto.";
        if (MultiSel.FirstOrDefault(e => e.Error != null || e.Polygon.Count < 3) is { } bad) return $"{bad.Name} ha un errore.";
        var ids = MultiSel.Select(e => e.Id).ToHashSet();
        var deps = Project.Elements.Where(x => !ids.Contains(x.Id) && MultiSel.Any(m => Project.DependsOn(x, m.Id))
                                               && (x.Base.Kind != EdgeKind.Road || x.Other is { Kind: not EdgeKind.Road })).ToList();
        if (deps.Count > 0) return $"{string.Join(", ", deps.Select(d => d.Name))} {(deps.Count == 1 ? "è riempito" : "sono riempiti")} usando i lati degli elementi scelti: elimina prima {(deps.Count == 1 ? "quell'elemento" : "quegli elementi")}.";
        if (PickHole) return "Le parti scelte racchiudono un vuoto: aggiungilo con Ctrl+clic.";
        return null;
    }

    /// <summary>
    /// Crea gli elementi dalla scelta: le parti che si toccano si uniscono e ogni gruppo diventa un elemento poligono
    /// del tipo indicato. Gli elementi disegnati inclusi nella scelta vengono sostituiti dai nuovi.
    /// </summary>
    public void CreateFromPick(ElementType t)
    {
        if (PickBlocker() is { } why) { Message = why; NotifyAll(); return; }
        int group = MultiSel.FirstOrDefault()?.Group ?? -1;
        foreach (var m in MultiSel) FreezeLinksTo(m.Id);
        foreach (var m in MultiSel) { Project.Elements.Remove(m); Project.Edges.RemoveAll(x => x.Owner == m.Id); }
        var made = new List<Element>();
        foreach (var ring in PickedRings)
        {
            var e = new Element { Id = Project.NextId++, Type = t, Name = Project.NewName(t), Width = Element.DefaultWidth(t) };
            Adopt(e);
            if (group >= 0 && e.Group == 0) e.Group = group;
            Project.Elements.Add(e);
            MakePolygon(e, new List<V2>(ring));
            made.Add(e);
        }
        ClearPick();
        Project.Regenerate();
        Selected = made.Count == 1 ? made[0] : null;
        Message = made.Count > 1 ? $"Creati {made.Count} elementi." : "";
        NotifyAll();
    }
}
