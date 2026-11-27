using CicloDraft.Geometry;
using CicloDraft.IO;

namespace CicloDraft.Model;

public enum Mode { Idle, Trace, PickBase, PickRange, SetWidth, PickOther, EditLine, PickVar, PickSection }

/// <summary>Punto agganciato: su un muro/livello dei dati (Layer, Ring) oppure su una linea del progetto (Line, Key).</summary>
public record SnapInfo(V2 P, GpkgLayer? Layer, int Ring, double RingStation, bool Vertex, Ring? Line = null, string? Key = null)
{
    public Ring? Geom => Line ?? Layer?.Rings[Ring];
    public bool SameLine(SnapInfo o) => Key != null ? Key == o.Key : Layer != null && Layer == o.Layer && Ring == o.Ring;
}

/// <summary>Stato e logica di interazione, indipendenti dall'interfaccia grafica.</summary>
public partial class Editor
{
    public Project Project = new();
    public GpkgData? Data;
    public Mode Mode;
    public string Message = "";

    // Tracciamento bordi
    public int TraceEdgeId;
    public List<V2> TracePts = new();
    public List<SnapInfo?> TraceSnaps = new();
    public List<bool> TraceAuto = new();
    public bool SnapOn = true, FollowOn = true;
    /// <summary>Finito il disegno l'elemento si salva subito (larghezza predefinita, lato proposto): niente passaggio intermedio.
    /// Il programma lo attiva; il server MCP e le prove lo lasciano spento per impostare larghezza e lato prima di salvare.</summary>
    public bool AutoCommit;

    // Nuovo elemento (bozza)
    public ElementType DraftType = ElementType.Marciapiede;
    public ElementMode DraftMode = ElementMode.Offset;
    public EdgeRef? DraftBase;
    public double DraftStart, DraftEnd;
    public bool RangeFixed;
    public double DraftWidth = 1.5;
    public double DraftSmooth = 1.0;
    public int DraftSide = 1;
    public EdgeRef? DraftOther;
    /// <summary>Si sta disegnando sulla mappa la linea (bordo o interasse) di un nuovo elemento.</summary>
    public bool DrawingElement;
    public bool DraftCentered;
    /// <summary>Il nuovo elemento è un poligono chiuso (non un tracciato).</summary>
    public bool DraftPolygon;

    // ── modifica della linea (punti chiave) ──
    public int EditEdgeId = -1;
    public int EditSel = -1;
    /// <summary>Si aggiungono punti in coda (+1) o in testa (-1); 0 = nessuno.</summary>
    public int EditExtend;
    Mode _editReturn = Mode.Idle;
    public RoadEdge? EditEdge => EditEdgeId >= 0 ? Project.FindEdge(EditEdgeId) : null;
    public Element? Preview;

    // Puntatore
    public EdgeRef? HoverEdge;
    public V2? Mouse;
    public SnapInfo? Snap;

    // Selezione
    public Element? Selected;
    public int DragCp = -1;

    public event Action? StructureChanged;
    public event Action? Redraw;

    public void NotifyAll() { StructureChanged?.Invoke(); Redraw?.Invoke(); }
    public void NotifyRedraw() => Redraw?.Invoke();

    // ───────────────────────── Dati ─────────────────────────

    public void SetData(GpkgData d, Project? p = null)
    {
        Data = d;
        Project = p ?? new Project();
        Mode = Mode.Idle; Selected = null; Preview = null; TracePts.Clear(); TraceSnaps.Clear(); TraceAuto.Clear();
        Message = d.Geographic
            ? "Il file è in coordinate geografiche: è stato proiettato in metri locali. Per un DXF georiferito usa un GeoPackage in coordinate proiettate (es. EPSG:6707, 32632, 3003)."
            : "GeoPackage caricato. Assegna il ruolo ai livelli e confermali, poi scegli l'elemento da disegnare.";
        NotifyAll();
    }

    // ───────────────────────── Bordi strada ─────────────────────────

    public void StartTrace(int edgeId)
    {
        CancelDraft(false);
        Mode = Mode.Trace; TraceEdgeId = edgeId; TracePts.Clear(); TraceSnaps.Clear(); TraceAuto.Clear(); Selected = null;
        var name = Project.FindEdge(edgeId)?.Name ?? "bordo";
        Message = $"Traccia «{name}»: clic sui vertici delle case, doppio clic o Invio per terminare.";
        NotifyAll();
    }

    public void TraceClick(V2 w, double tol)
    {
        var s = FindSnap(w, tol);
        var p = s?.P ?? w;
        if (TracePts.Count > 0 && V2.Dist(TracePts[^1], p) < 1e-6) return;
        if (FollowOn && s != null && TraceSnaps.Count > 0 && TraceSnaps[^1] is { } prev)
        {
            foreach (var v in FollowPath(prev, s))
            {
                TracePts.Add(v); TraceSnaps.Add(null); TraceAuto.Add(true);
            }
        }
        TracePts.Add(p); TraceSnaps.Add(s); TraceAuto.Add(false);
        NotifyAll();
    }

    public void TraceUndo()
    {
        if (TracePts.Count == 0) return;
        void Pop() { TracePts.RemoveAt(TracePts.Count - 1); TraceSnaps.RemoveAt(TraceSnaps.Count - 1); TraceAuto.RemoveAt(TraceAuto.Count - 1); }
        Pop();
        // rimuove anche i vertici inseriti automaticamente dal "segui facciata"
        while (TraceAuto.Count > 0 && TraceAuto[^1]) Pop();
        NotifyAll();
    }

    public void TraceFinish()
    {
        if (Mode != Mode.Trace) return;
        var pts = PL.Dedup(TracePts);
        bool polyLine = (DrawingElement && DraftPolygon) || (Project.FindEdge(TraceEdgeId) is { Owner: >= 0 } oe && Project.Find(oe.Owner) is { Mode: ElementMode.Polygon });
        if (polyLine && pts.Count >= 2 && V2.Dist(pts[0], pts[^1]) < 1e-6) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < (polyLine ? 3 : 2)) { Message = polyLine ? "Servono almeno tre punti per un poligono." : "Servono almeno due punti per una linea."; NotifyAll(); return; }
        var edge = Project.FindEdge(TraceEdgeId);
        if (edge == null) { TraceCancel(); return; }
        edge.Points = pts;
        edge.Anchors = BuildAnchors(edge.Id);
        if (DrawingElement)
        {
            bool poly = DraftPolygon;
            EnterWidthStep(edge);
            // un poligono chiuso si salva subito (il tipo si cambia poi da «Elemento selezionato»)
            if (poly && Mode == Mode.SetWidth && Preview is { Error: null }) Commit();
            return;
        }
        // linea di un elemento ridisegnata: l'elemento la segue per intero
        if (edge.Owner >= 0 && Project.Find(edge.Owner) is { } owner)
        {
            owner.Start = 0; owner.End = PL.Length(edge.Points); owner.ToEnd = true; owner.Profile.Clear(); owner.Variators.Clear();
        }
        Project.Regenerate();
        Mode = Mode.Idle; TracePts.Clear(); TraceSnaps.Clear(); TraceAuto.Clear();
        if (edge.Owner >= 0 && Project.Find(edge.Owner) is { } own2)
        {
            Selected = own2;
            Message = $"Linea di {own2.Name} ridisegnata: {PL.Length(edge.Points):0.00} m.";
            NotifyAll();
            return;
        }
        Message = $"{edge.Name}: {PL.Length(pts):0.00} m. " + (Project.Edges.All(e => e.Points.Count >= 2)
            ? (Project.Elements.Count == 0 ? "Ora crea il primo elemento: scegli il tipo e il bordo di aggancio." : "Ora puoi agganciarvi nuovi elementi.")
            : $"Traccia anche {string.Join(", ", Project.Edges.Where(e => e.Points.Count < 2).Select(e => "«" + e.Name + "»"))}.");
        NotifyAll();
    }

    /// <summary>
    /// Punti del bordo appena tracciato: quelli cliccati sul bordo esterno di un elemento restano agganciati a lui,
    /// e il tratto seguito lungo il suo contorno si ricalcolerà quando l'elemento cambia.
    /// </summary>
    List<EdgeAnchor>? BuildAnchors(int edgeId)
    {
        var res = new List<EdgeAnchor>();
        bool any = false;
        for (int i = 0; i < TracePts.Count; i++)
        {
            var p = TracePts[i];
            // l'aggancio si decide dalla posizione: il punto sta sul bordo esterno di un elemento?
            var host = OuterHost(p, edgeId);
            if (TraceAuto[i])
            {
                // vertice aggiunto da «segui la facciata»: se segue il contorno dell'elemento a cui sono agganciati
                // il clic prima e quello dopo, si ricalcola con lui; altrimenti resta fisso
                int nextClick = i + 1; while (nextClick < TracePts.Count && TraceAuto[nextClick]) nextClick++;
                if (host is { } h && res.LastOrDefault() is { El: >= 0 } last && last.El == h.e.Id && last.Sd == h.sd
                    && nextClick < TracePts.Count && OuterHost(TracePts[nextClick], edgeId) is { } hn && hn.e.Id == h.e.Id && hn.sd == h.sd)
                {
                    last.Follow = true; continue;
                }
                host = null;
            }
            int el = -1, sdv = 1; double s = 0;
            if (host is { } hh) { el = hh.e.Id; sdv = hh.sd; s = PL.Project(hh.e.Guide, p).Station; any = true; }
            res.Add(new EdgeAnchor { X = p.X, Y = p.Y, El = el, S = s, Sd = sdv, Auto = TraceAuto[i] && el < 0 });
        }
        // due clic consecutivi sullo stesso elemento con «segui la facciata» attivo: il tratto segue il contorno anche se dritto
        for (int i = 0; i + 1 < res.Count; i++)
            if (FollowOn && res[i].El >= 0 && res[i].El == res[i + 1].El && res[i].Sd == res[i + 1].Sd) res[i].Follow = true;
        return res;
    }

    /// <summary>Elemento (a larghezza fissa) sul cui contorno sta il punto (bordo esterno, o uno dei due lati se è a interasse).</summary>
    (Element e, int sd)? OuterHost(V2 p, int edgeId)
    {
        (Element, int)? best = null; double bd = 0.05;
        foreach (var e in Project.Shown)
        {
            if (e.Error != null || e.Mode != ElementMode.Offset || e.Outer.Count < 2) continue;
            foreach (int sd in e.Centered ? new[] { 1, -1 } : new[] { 1 })
            {
                var line = Project.SideLine(e, sd);
                if (line.Count < 2) continue;
                double d = PL.Project(line, p).Distance;
                if (d < bd && !Project.ElementDependsOnEdge(e, edgeId)) { bd = d; best = (e, sd); }
            }
        }
        return best;
    }

    // ───────────────────────── Modifica della linea ─────────────────────────

    /// <summary>Linea modificabile ora: quella appena disegnata (in anteprima) o quella dell'elemento selezionato.</summary>
    public RoadEdge? EditableLine()
    {
        if (Mode == Mode.SetWidth && DraftBase is { Kind: EdgeKind.Road } db && Project.FindEdge(db.Id) is { Owner: -2 } d) return d;
        if (Mode == Mode.Idle && Selected is { } s && s.Base.Kind == EdgeKind.Road && Project.FindEdge(s.Base.Id) is { } e && e.Owner == s.Id) return e;
        return null;
    }

    public void StartEditLine()
    {
        var edge = EditableLine();
        if (edge == null) { Message = "Seleziona un elemento disegnato sulla mappa (o disegnane uno) per modificarne la linea."; NotifyAll(); return; }
        // linea senza punti chiave memorizzati: i suoi vertici diventano i punti chiave
        edge.Anchors ??= edge.Points.Select(p => new EdgeAnchor { X = p.X, Y = p.Y }).ToList();
        _editReturn = Mode;
        EditEdgeId = edge.Id; EditSel = -1; EditExtend = 0;
        Mode = Mode.EditLine;
        Message = EditIsPolygon
            ? "Modifica il poligono: trascina un punto per spostarlo, Canc per eliminarlo, doppio clic sul contorno per inserirne uno, doppio clic su un punto per farlo curvo."
            : "Modifica la linea: trascina un punto per spostarlo, Canc per eliminarlo, doppio clic sulla linea per inserirne uno, doppio clic su un punto per farlo curvo, «Prosegui» per aggiungerne all'inizio o alla fine.";
        NotifyAll();
    }

    public void FinishEditLine()
    {
        if (Mode != Mode.EditLine) return;
        EditExtend = 0; EditSel = -1;
        Mode = _editReturn;
        var edge = EditEdge; EditEdgeId = -1;
        if (Mode == Mode.SetWidth) BuildPreview();
        Message = edge != null ? $"Linea modificata: {PL.Length(edge.Points):0.00} m." : "Modifica terminata.";
        NotifyAll();
    }

    /// <summary>Punto chiave che sta in p (con aggancio a un elemento se cade sul suo contorno).</summary>
    EdgeAnchor MakeAnchor(V2 p, int edgeId)
    {
        var a = new EdgeAnchor { X = p.X, Y = p.Y };
        if (OuterHost(p, edgeId) is { } h && !(Project.FindEdge(edgeId) is { Owner: >= 0 } oe && oe.Owner == h.e.Id))
        { a.El = h.e.Id; a.Sd = h.sd; a.S = PL.Project(h.e.Guide, p).Station; }
        return a;
    }

    /// <summary>Dopo ogni modifica: i punti della linea si ricalcolano dai punti chiave, poi l'elemento.</summary>
    void ApplyLineEdit()
    {
        var edge = EditEdge; if (edge?.Anchors == null) return;
        var an = edge.Anchors;
        // segue il contorno solo tra due punti consecutivi agganciati allo stesso elemento e lato
        for (int i = 0; i < an.Count; i++)
            if (an[i].Follow && !(i + 1 < an.Count && an[i].El >= 0 && an[i + 1].El == an[i].El && an[i + 1].Sd == an[i].Sd)) an[i].Follow = false;
        var pts = new List<V2>();
        Project.UpdateLinkedEdges();
        if (an.All(a => a.El < 0) && !an.Any(a => a.Curve)) edge.Points = PL.Dedup(an.Select(a => new V2(a.X, a.Y)).ToList());
        if (edge.Owner >= 0 && Project.Find(edge.Owner) is { } own) { own.Start = 0; own.End = PL.Length(edge.Points); own.ToEnd = true; }
        Project.Regenerate();
        if (_editReturn == Mode.SetWidth) { DraftStart = 0; DraftEnd = PL.Length(edge.Points); BuildPreview(); }
    }

    public int HitKeyPoint(V2 w, double tol)
    {
        var an = EditEdge?.Anchors; if (an == null) return -1;
        int best = -1; double bd = tol;
        for (int i = 0; i < an.Count; i++) { if (an[i].Auto) continue; double d = V2.Dist(new V2(an[i].X, an[i].Y), w); if (d < bd) { bd = d; best = i; } }
        return best;
    }

    /// <summary>
    /// Ricalcola i tratti che seguono un muro attorno al punto chiave k: via i vertici automatici tra k e i punti chiave
    /// vicini, poi, se i due punti stanno sullo stesso muro (e «segui il contorno» è attivo), si rimettono quelli del percorso.
    /// Restituisce il nuovo indice di k.
    /// </summary>
    int Refollow(List<EdgeAnchor> an, int k, bool follow)
    {
        if (k < 0 || k >= an.Count) return k;
        int p = k - 1; while (p >= 0 && an[p].Auto) p--;
        int n = k + 1; while (n < an.Count && an[n].Auto) n++;
        // vertici automatici tra p e k, e tra k e n
        if (n - k - 1 > 0) an.RemoveRange(k + 1, n - k - 1);
        if (k - p - 1 > 0) { an.RemoveRange(p + 1, k - p - 1); k = p + 1; }
        if (!follow) return k;
        List<EdgeAnchor> Path(EdgeAnchor a, EdgeAnchor b)
        {
            if (a.Curve || b.Curve) return new();   // accanto a un punto in curva il tratto resta curvo
            if (a.El >= 0 && b.El >= 0) return new();   // tra elementi: lo segue già l'aggancio
            var sa = FindSnap(new V2(a.X, a.Y), 0.02); var sb = FindSnap(new V2(b.X, b.Y), 0.02);
            if (sa == null || sb == null || !sa.SameLine(sb) || sa.Key != null) return new();
            return FollowPath(sa, sb).Select(v => new EdgeAnchor { X = v.X, Y = v.Y, Auto = true }).ToList();
        }
        if (k + 1 < an.Count) an.InsertRange(k + 1, Path(an[k], an[k + 1]));
        if (k - 1 >= 0) { var pre = Path(an[k - 1], an[k]); an.InsertRange(k, pre); k += pre.Count; }
        return k;
    }

    public void SelectKeyPoint(int i) { EditSel = i; NotifyAll(); }

    /// <summary>La linea in modifica è il contorno di un poligono (chiuso: niente inizio e fine).</summary>
    public bool EditIsPolygon => EditEdge is { } e && (e.Owner >= 0 && Project.Find(e.Owner) is { Mode: ElementMode.Polygon } || (e.Owner == -2 && DraftPolygon));

    /// <summary>Punti chiave non automatici prima e dopo i (nel poligono si gira attorno).</summary>
    (EdgeAnchor? prev, EdgeAnchor? next) KeyNeighbours(List<EdgeAnchor> an, int i)
    {
        bool closed = EditIsPolygon; int n = an.Count;
        EdgeAnchor? Walk(int d)
        {
            for (int k = 1; k < n; k++)
            {
                int j = i + d * k;
                if (closed) j = ((j % n) + n) % n; else if (j < 0 || j >= n) return null;
                if (!an[j].Auto) return an[j];
            }
            return null;
        }
        return (Walk(-1), Walk(1));
    }

    /// <summary>Doppio clic su un punto della linea o del poligono: diventa un punto in curva (Bézier) o torna spigolo.</summary>
    public void ToggleCurve(int i)
    {
        var an = EditEdge?.Anchors; if (an == null || i < 0 || i >= an.Count || an[i].Auto) return;
        var a = an[i];
        if (a.Curve) { a.Curve = false; a.Hx = a.Hy = 0; }
        else
        {
            var (pv, nx) = KeyNeighbours(an, i);
            var P = new V2(a.X, a.Y);
            var pp = pv != null ? new V2(pv.X, pv.Y) : P; var np = nx != null ? new V2(nx.X, nx.Y) : P;
            // tangente parallela alla congiungente dei vicini, lunga un terzo del lato più corto (niente cappi)
            var dir = (np - pp).Norm();
            double dp = pv != null ? V2.Dist(P, pp) : double.MaxValue, dn = nx != null ? V2.Dist(P, np) : double.MaxValue;
            double len = Math.Min(dp, dn); if (double.IsInfinity(len) || len > 1e9) len = Math.Max(V2.Dist(P, pp), V2.Dist(P, np));
            if (dir.Len < 1e-9) dir = new V2(1, 0);
            var h = dir * Math.Max(0.05, len / 3);
            a.Curve = true; a.Hx = h.X; a.Hy = h.Y;
            // i due lati del punto non seguono più muri o elementi: diventano curvi
            i = Refollow(an, i, false);
            a.Follow = false;
            for (int k = i - 1; k >= 0; k--) if (!an[k].Auto) { an[k].Follow = false; break; }
            if (EditIsPolygon && an.Count > 0) an[^1].Follow = an[^1] == a ? false : an[^1].Follow;
        }
        EditSel = i;
        ApplyLineEdit();
        Message = a.Curve ? "Punto in curva: trascina le maniglie per cambiare la curva; doppio clic per tornare allo spigolo." : "";
        NotifyAll();
    }

    /// <summary>Maniglia di una curva sotto il punto: (indice del punto, +1 = verso il successivo, −1 = verso il precedente).</summary>
    public (int i, int sign)? HitCurveHandle(V2 w, double tol)
    {
        var an = EditEdge?.Anchors; if (an == null) return null;
        for (int i = 0; i < an.Count; i++)
        {
            if (!an[i].Curve) continue;
            var P = new V2(an[i].X, an[i].Y); var H = new V2(an[i].Hx, an[i].Hy);
            if (V2.Dist(P + H, w) < tol) return (i, 1);
            if (V2.Dist(P - H, w) < tol) return (i, -1);
        }
        return null;
    }

    public void MoveCurveHandle(int i, int sign, V2 w, bool final)
    {
        var an = EditEdge?.Anchors; if (an == null || i < 0 || i >= an.Count) return;
        var a = an[i]; var h = (w - new V2(a.X, a.Y)) * sign;
        a.Hx = h.X; a.Hy = h.Y;
        EditSel = i;
        ApplyLineEdit();
        if (final) NotifyAll(); else NotifyRedraw();
    }

    /// <summary>Trascinamento: il punto segue il mouse (agganciandosi); al rilascio si decide l'aggancio all'elemento.</summary>
    public void MoveKeyPoint(int i, V2 w, double tol, bool final)
    {
        var edge = EditEdge; var an = edge?.Anchors; if (an == null || i < 0 || i >= an.Count) return;
        var p = FindSnap(w, tol)?.P ?? w;
        var na = final ? MakeAnchor(p, edge!.Id) : new EdgeAnchor { X = p.X, Y = p.Y };
        na.Follow = an[i].Follow && na.El >= 0;
        na.Curve = an[i].Curve; na.Hx = an[i].Hx; na.Hy = an[i].Hy;
        an[i] = na;
        EditSel = Refollow(an, i, final && FollowOn);
        ApplyLineEdit();
        if (final) NotifyAll(); else NotifyRedraw();
    }

    public void DeleteKeyPoint()
    {
        var edge = EditEdge; var an = edge?.Anchors; if (an == null || EditSel < 0 || EditSel >= an.Count) return;
        bool poly = edge!.Owner >= 0 && Project.Find(edge.Owner) is { Mode: ElementMode.Polygon } || (edge.Owner == -2 && DraftPolygon);
        if (an.Count(a => !a.Auto) <= (poly ? 3 : 2)) { Message = poly ? "Un poligono deve avere almeno tre punti." : "La linea deve avere almeno due punti."; NotifyAll(); return; }
        int k = Refollow(an, EditSel, false);   // via anche i vertici automatici attorno
        an.RemoveAt(k);
        // il tratto tra i due vicini: se stanno sullo stesso muro torna a seguirlo
        int nk = Math.Min(k, an.Count - 1); while (nk > 0 && an[nk].Auto) nk--;
        if (FollowOn && nk >= 0) Refollow(an, nk, true);
        EditSel = -1;
        ApplyLineEdit();
        Message = $"Punto eliminato: la linea ora ha {an.Count(a => !a.Auto)} punti chiave.";
        NotifyAll();
    }

    /// <summary>Doppio clic sulla linea: nuovo punto chiave in quel punto, tra i due vicini.</summary>
    public bool InsertKeyPoint(V2 w, double tol)
    {
        var edge = EditEdge; var an = edge?.Anchors; if (an == null || edge!.Points.Count < 2) return false;
        var pr = PL.Project(edge.Points, w);
        if (pr.Distance > tol) return false;
        int at = an.Count;
        for (int i = 0; i < an.Count; i++)
            if (PL.Project(edge.Points, new V2(an[i].X, an[i].Y)).Station > pr.Station) { at = i; break; }
        if (at == 0) at = 1;
        var na = MakeAnchor(pr.Point, edge.Id);
        if (an[at - 1].Follow && an[at - 1].El == na.El) na.Follow = at < an.Count && an[at].El == na.El;
        an.Insert(at, na);
        EditSel = at;
        if (na.El < 0) EditSel = Refollow(an, at, FollowOn);
        ApplyLineEdit();
        Message = "Punto inserito: trascinalo dove serve.";
        NotifyAll();
        return true;
    }

    public void StartExtend(int end)
    {
        if (Mode != Mode.EditLine) return;
        EditExtend = end; EditSel = -1;
        Message = end > 0 ? "Clic sulla mappa per aggiungere punti alla fine della linea; Esc per smettere."
                          : "Clic sulla mappa per aggiungere punti all'inizio della linea; Esc per smettere.";
        NotifyAll();
    }

    /// <summary>Punto aggiunto in testa o in coda, con «segui il contorno» verso il punto vicino.</summary>
    public void ExtendClick(V2 w, double tol)
    {
        var edge = EditEdge; var an = edge?.Anchors; if (an == null || EditExtend == 0) return;
        var s = FindSnap(w, tol);
        var p = s?.P ?? w;
        var na = MakeAnchor(p, edge!.Id);
        var nb = EditExtend > 0 ? an[^1] : an[0];
        var nbP = new V2(nb.X, nb.Y);
        var extra = new List<EdgeAnchor>();
        if (FollowOn)
        {
            if (na.El >= 0 && nb.El == na.El && nb.Sd == na.Sd) { if (EditExtend > 0) nb.Follow = true; else na.Follow = true; }
            else if (s != null && FindSnap(nbP, 0.02) is { } sb && sb.SameLine(s))
                extra.AddRange((EditExtend > 0 ? FollowPath(sb, s) : FollowPath(s, sb)).Select(v => new EdgeAnchor { X = v.X, Y = v.Y, Auto = true }));
        }
        if (EditExtend > 0) { an.AddRange(extra); an.Add(na); EditSel = an.Count - 1; }
        else { an.InsertRange(0, extra); an.Insert(0, na); EditSel = 0; }
        ApplyLineEdit();
        NotifyAll();
    }

    /// <summary>Cambia l'elemento che si sta aggiungendo (durante il disegno o con l'anteprima già visibile).</summary>
    public void SetDraftType(ElementType t)
    {
        if (DraftType == t) return;
        var old = DraftType;
        DraftType = t;
        // la larghezza segue il nuovo elemento, se non era stata cambiata a mano
        if (Math.Abs(DraftWidth - Element.DefaultWidth(old)) < 1e-9) DraftWidth = Element.DefaultWidth(t);
        if (Mode is Mode.SetWidth or Mode.PickOther) BuildPreview();
        Message = $"Elemento cambiato in «{Element.TypeLabel(t)}»" + (Mode == Mode.SetWidth ? $", larghezza {DraftWidth:0.00} m." : ".");
        NotifyAll();
    }

    /// <summary>Linea del nuovo elemento pronta: si passa alla larghezza con l'anteprima.</summary>
    void EnterWidthStep(RoadEdge edge)
    {
        DrawingElement = false;
        Project.Regenerate();
        TracePts.Clear(); TraceSnaps.Clear(); TraceAuto.Clear();
        DraftMode = DraftPolygon ? ElementMode.Polygon : ElementMode.Offset; DraftOther = null;
        DraftBase = new EdgeRef(EdgeKind.Road, edge.Id);
        DraftStart = 0; DraftEnd = PL.Length(edge.Points); RangeFixed = true;
        // lungo i muri si ignorano i piccoli gradini delle facciate; lungo altri elementi si segue la linea esatta
        DraftSmooth = edge.IsLinked ? 0 : 1.0;
        Mode = Mode.SetWidth;
        DraftSide = DraftCentered ? 1 : SuggestSide();
        BuildPreview();
        Message = DraftPolygon
            ? $"Poligono di {Preview?.Metrics.Area ?? 0:0.00} m² (perimetro {Preview?.Metrics.Length ?? 0:0.00} m). Premi «Salva» per confermarlo."
            : DraftCentered
            ? $"Interasse di {PL.Length(edge.Points):0.00} m. Imposta la larghezza (metà per parte) e premi «Salva»."
            : $"Bordo di {PL.Length(edge.Points):0.00} m. Imposta la larghezza, controlla da che parte va («Inverti lato») e premi «Salva».";
        if (AutoCommit && Preview is { Error: null }) { Commit(); return; }
        NotifyAll();
    }

    /// <summary>Mentre si disegna un tracciato: l'elemento sul cui contorno è agganciato il primo punto (se c'è).</summary>
    public (Element e, int sd)? FirstPointHost =>
        Mode == Mode.Trace && DrawingElement && !DraftPolygon && TracePts.Count >= 1 && TraceSnaps.Count >= 1 && TraceSnaps[0]?.Key?.StartsWith("el:") == true
            ? OuterHost(TracePts[0], TraceEdgeId) : null;

    /// <summary>
    /// «Lungo come …»: la linea del nuovo elemento diventa tutto il contorno (lato esterno) dell'elemento a cui è agganciato
    /// il primo punto, da un capo all'altro (o tutto il giro, se è attorno a un edificio), e lo segue quando cambia.
    /// </summary>
    public void MatchFirstHostLength()
    {
        if (FirstPointHost is not { } h) return;
        var edge = Project.FindEdge(TraceEdgeId); if (edge == null) return;
        var e = h.e; double L = PL.Length(e.Guide);
        bool closed = e.Guide.Count >= 4 && V2.Dist(e.Guide[0], e.Guide[^1]) < 1e-6;
        // per un contorno chiuso si mettono più punti, così ogni tratto segue il lato giusto
        var stations = closed ? new[] { 0, L / 4, L / 2, 3 * L / 4, L } : new[] { 0, L };
        var an = new List<EdgeAnchor>();
        foreach (var st in stations)
        {
            var p = Project.AnchorPoint(e, st, h.sd) ?? PL.PointAt(e.Guide, st);
            an.Add(new EdgeAnchor { X = p.X, Y = p.Y, El = e.Id, S = st, Sd = h.sd, Follow = true });
        }
        an[^1].Follow = false;
        edge.Anchors = an;
        edge.Points = an.Select(a => new V2(a.X, a.Y)).ToList();
        Project.UpdateLinkedEdges();
        EnterWidthStep(edge);
        Message = $"Linea lunga come {e.Name} ({PL.Length(edge.Points):0.00} m), agganciata al suo contorno. " + Message;
        NotifyAll();
    }

    /// <summary>Si può affiancare un nuovo elemento all'elemento selezionato (solo elementi tracciati con larghezza).</summary>
    public bool CanAddAlongside => Mode == Mode.Idle && Selected is { Mode: ElementMode.Offset, Error: null } s && s.Guide.Count >= 2 && s.Outer.Count >= 2;

    /// <summary>
    /// «Aggiungi a destra / a sinistra»: nuovo elemento tracciato, lungo quanto quello selezionato, appoggiato al suo contorno
    /// dal lato scelto (destra e sinistra secondo il verso in cui è stata disegnata la linea, dall'inizio alla fine).
    /// La linea del nuovo elemento resta agganciata al contorno e lo segue se l'elemento cambia.
    /// </summary>
    public void AddAlongside(bool left)
    {
        if (!CanAddAlongside || Selected is not { } e) return;
        int dir = left ? 1 : -1;
        // contorno dal lato scelto: per un elemento a interasse Inner sta a sinistra e Outer a destra;
        // per uno disegnato dal bordo Outer sta dal lato dell'elemento, Inner (la linea disegnata) dall'altro
        int sd = e.Centered ? (left ? -1 : 1) : (dir == e.Side ? 1 : -1);
        double L = PL.Length(e.Guide);
        bool closed = e.Guide.Count >= 4 && V2.Dist(e.Guide[0], e.Guide[^1]) < 1e-6;
        var stations = closed ? new[] { 0, L / 4, L / 2, 3 * L / 4, L } : new[] { 0, L };
        var an = new List<EdgeAnchor>();
        foreach (var st in stations)
        {
            var p = Project.AnchorPoint(e, st, sd) ?? PL.PointAt(e.Guide, st);
            an.Add(new EdgeAnchor { X = p.X, Y = p.Y, El = e.Id, S = st, Sd = sd, Follow = true });
        }
        an[^1].Follow = false;
        var edge = Project.AddEdge("Linea");
        edge.Anchors = an;
        edge.Points = an.Select(a => new V2(a.X, a.Y)).ToList();
        Project.UpdateLinkedEdges();
        var t = e.Type;
        var ne = new Element
        {
            Type = t, Mode = ElementMode.Offset, Base = new EdgeRef(EdgeKind.Road, edge.Id),
            Start = 0, End = PL.Length(edge.Points), ToEnd = true, Side = dir,
            Width = Element.DefaultWidth(t), Smooth = 0, Name = Project.NewName(t)
        };
        ne.Id = Project.NextId++;
        edge.Owner = ne.Id; edge.Name = "Linea di " + ne.Name;
        ne.Group = e.Group;
        Project.Elements.Add(ne);
        Project.Regenerate();
        Selected = ne;
        // avviso se dal lato scelto c'era già un altro elemento
        var over = new List<string>();
        if (ne.Error == null && Project.ToNts(ne.Polygon) is { IsValid: true } pn)
            foreach (var o in Project.Elements)
                if (o != ne && o != e && o.Error == null && Project.ToNts(o.Polygon) is { IsValid: true } po && pn.Intersection(po).Area > 0.05 * Math.Max(0.01, Math.Min(pn.Area, po.Area)))
                    over.Add(o.Name);
        Message = ne.Error != null
            ? $"{ne.Name} aggiunto a {(left ? "sinistra" : "destra")} di {e.Name}, ma: {ne.Error}"
            : $"{ne.Name} aggiunto a {(left ? "sinistra" : "destra")} di {e.Name} ({ne.Metrics.Length:0.00} m, {ne.Metrics.Area:0.00} m²). Cambia tipo, larghezza o linea qui sotto."
              + (over.Count > 0 ? $" Attenzione: si sovrappone a {string.Join(", ", over)} (da quel lato c'era già un elemento: puoi eliminarlo o usare l'altro lato)." : "");
        NotifyAll();
    }

    public void TraceCancel()
    {
        if (DrawingElement)
        {
            DrawingElement = false;
            var tmp = Project.FindEdge(TraceEdgeId);
            if (tmp is { Owner: -2 }) Project.Edges.Remove(tmp);
        }
        Mode = Mode.Idle; TracePts.Clear(); TraceSnaps.Clear(); TraceAuto.Clear(); Message = "Tracciamento annullato.";
        NotifyAll();
    }

    public SnapInfo? FindSnap(V2 w, double tol)
    {
        if (!SnapOn || Data == null) return null;
        SnapInfo? bestV = null; double dv = tol;
        SnapInfo? bestS = null; double ds = tol * 0.8;
        // linee su cui agganciarsi: muri e livelli dei dati, poi i contorni degli elementi disegnati e gli altri bordi
        var sources = Data.RingsWithRole(LayerRole.Muri, LayerRole.Marciapiedi, LayerRole.Sfondo, LayerRole.Edifici3D)
            .Select(x => (x.layer, x.idx, x.ring, key: (string?)null))
            .Concat(ProjectSnapLines().Select(x => ((GpkgLayer?)null, -1, x.ring, key: (string?)x.key)));
        foreach (var (layer, idx, r, key) in sources)
        {
            if (!r.Box.Contains(w, tol)) continue;
            var pts = r.Pts; int n = pts.Count;
            double acc = 0;
            int segs = r.Closed ? n : n - 1;
            for (int i = 0; i < n; i++)
            {
                double d = V2.Dist(pts[i], w);
                if (d < dv) { dv = d; bestV = new SnapInfo(pts[i], layer, idx, acc, true, key != null ? r : null, key); }
                if (i < segs)
                {
                    var a = pts[i]; var b = pts[(i + 1) % n]; var ab = b - a;
                    double l2 = V2.Dot(ab, ab), len = Math.Sqrt(l2);
                    if (l2 > 1e-12)
                    {
                        double t = Math.Clamp(V2.Dot(w - a, ab) / l2, 0, 1);
                        var q = a + ab * t;
                        double dq = V2.Dist(q, w);
                        if (dq < ds) { ds = dq; bestS = new SnapInfo(q, layer, idx, acc + t * len, false, key != null ? r : null, key); }
                    }
                    acc += len;
                }
            }
        }
        // un vertice vince sul lato su cui si trova; ma se il clic cade su un'altra linea più vicina
        // (es. il bordo esterno di un marciapiede a pochi metri dallo spigolo del muro) vince quella
        if (bestV != null && bestS != null && !bestV.SameLine(bestS) && ds < dv * 0.5) return bestS;
        return bestV ?? bestS;
    }

    /// <summary>Contorni degli elementi (es. il bordo esterno di un marciapiede attorno a un edificio) e bordi già tracciati.</summary>
    IEnumerable<(Ring ring, string key)> ProjectSnapLines()
    {
        int busy = Mode == Mode.Trace ? TraceEdgeId : Mode == Mode.EditLine ? EditEdgeId : -999;
        foreach (var el in Project.Shown)
            if (el.Error == null && el.Polygon.Count >= 3 && !(el.Base.Kind == EdgeKind.Road && el.Base.Id == busy))
                yield return (new Ring { Pts = el.Polygon, Closed = true, Box = BBox.Of(el.Polygon) }, "el:" + el.Id);
        foreach (var e in Project.Edges)
        {
            if (e.Points.Count < 2 || e.Id == busy) continue;
            bool closed = e.Points.Count >= 3 && V2.Dist(e.Points[0], e.Points[^1]) < 0.01;
            var pts = closed ? e.Points.Take(e.Points.Count - 1).ToList() : e.Points;
            yield return (new Ring { Pts = pts, Closed = closed, Box = BBox.Of(pts) }, "edge:" + e.Id);
        }
    }

    static double Mod(double a, double m) => ((a % m) + m) % m;

    /// <summary>Vertici della facciata tra due punti agganciati allo stesso edificio (percorso più breve).</summary>
    public static List<V2> FollowPath(SnapInfo a, SnapInfo b)
    {
        var res = new List<V2>();
        if (!a.SameLine(b) || a.Geom is not { } r) return res;
        var pts = r.Pts; int n = pts.Count;
        var cum = new double[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + V2.Dist(pts[i - 1], pts[i]);
        const double eps = 1e-6;
        if (!r.Closed)
        {
            var idx = Enumerable.Range(0, n).Where(i => cum[i] > Math.Min(a.RingStation, b.RingStation) + eps
                                                     && cum[i] < Math.Max(a.RingStation, b.RingStation) - eps);
            idx = a.RingStation < b.RingStation ? idx.OrderBy(i => cum[i]) : idx.OrderByDescending(i => cum[i]);
            res.AddRange(idx.Select(i => pts[i]));
            return res;
        }
        double P = cum[n - 1] + V2.Dist(pts[n - 1], pts[0]);
        double fwd = Mod(b.RingStation - a.RingStation, P), bwd = P - fwd;
        if (Math.Min(fwd, bwd) > 300) return res;
        if (fwd <= bwd)
            res.AddRange(Enumerable.Range(0, n).Select(i => (i, m: Mod(cum[i] - a.RingStation, P)))
                .Where(x => x.m > eps && x.m < fwd - eps).OrderBy(x => x.m).Select(x => pts[x.i]));
        else
            res.AddRange(Enumerable.Range(0, n).Select(i => (i, m: Mod(a.RingStation - cum[i], P)))
                .Where(x => x.m > eps && x.m < bwd - eps).OrderBy(x => x.m).Select(x => pts[x.i]));
        return res;
    }

    public void AddRoadEdge()
    {
        if (Mode != Mode.Idle) CancelDraft(false);
        var e = Project.AddEdge();
        StartTrace(e.Id);
    }

    public void RenameEdge(int id, string name)
    {
        if (Project.FindEdge(id) is { } e && !string.IsNullOrWhiteSpace(name) && e.Name != name.Trim())
        {
            e.Name = name.Trim(); NotifyAll();
        }
    }

    public bool DeleteEdge(int id)
    {
        var e = Project.FindEdge(id);
        if (e == null) return false;
        var users = Project.UsersOfEdge(id);
        if (users.Count > 0)
        {
            Message = $"«{e.Name}» non si può eliminare: è il bordo di aggancio di {string.Join(", ", users.Select(u => u.Name))}. Elimina prima quegli elementi.";
            NotifyAll(); return false;
        }
        if (Mode == Mode.Trace && TraceEdgeId == id) TraceCancel();
        Project.Edges.Remove(e);
        Message = $"«{e.Name}» eliminato.";
        NotifyAll();
        return true;
    }

    // ───────────────────────── Bordi selezionabili ─────────────────────────

    public IEnumerable<(EdgeRef r, List<V2> pts)> PickableEdges()
    {
        foreach (var e in Project.Edges)
            if (e.Points.Count >= 2 && e.Owner != -2 && Project.IsShown(e) && !(e.Owner >= 0 && Project.Find(e.Owner) is { Centered: true }))
                yield return (new EdgeRef(EdgeKind.Road, e.Id), e.Points);
        foreach (var el in Project.Shown)
            if (el.Error == null && el.Mode is ElementMode.Offset or ElementMode.Polygon && el.Outer.Count >= 2)
            {
                yield return (new EdgeRef(EdgeKind.Outer, el.Id), el.Outer);
                if (el.Centered && el.Inner.Count >= 2) yield return (new EdgeRef(EdgeKind.Inner, el.Id), el.Inner);
            }
    }

    public (EdgeRef r, Projection pr)? NearestEdge(V2 w, double tol, EdgeRef? exclude = null)
    {
        (EdgeRef, Projection)? best = null; double bd = tol;
        foreach (var (r, pts) in PickableEdges())
        {
            if (exclude is { } ex && ex == r) continue;
            var pr = PL.Project(pts, w);
            if (pr.Distance < bd) { bd = pr.Distance; best = (r, pr); }
        }
        return best;
    }

    // ───────────────────────── Nuovo elemento ─────────────────────────

    /// <summary>Nuovo elemento: si disegna sulla mappa il suo bordo (l'elemento sta da un lato) o il suo interasse.</summary>
    public void StartDrawElement(ElementType t, bool centered, bool polygon = false)
    {
        if (Mode == Mode.Trace) TraceCancel();
        CancelDraft(false);
        DraftType = t; DraftCentered = centered && !polygon; DraftPolygon = polygon; DraftMode = polygon ? ElementMode.Polygon : ElementMode.Offset;
        DraftWidth = Element.DefaultWidth(t);
        Selected = null;
        int n = 1; while (Project.Edges.Any(e => e.Name == $"Linea {n}")) n++;
        var edge = Project.AddEdge($"Linea {n}");
        edge.Owner = -2;
        StartTrace(edge.Id);
        DrawingElement = true;
        Message = (polygon ? $"Disegna il poligono di «{Element.TypeLabel(t)}»: clic su ogni vertice (si aggancia a muri ed elementi), doppio clic o Invio per chiuderlo."
                  : (centered ? $"Disegna l'interasse di «{Element.TypeLabel(t)}»" : $"Disegna il bordo di «{Element.TypeLabel(t)}»")
                  + ": clic per ogni punto (si aggancia a muri ed elementi), doppio clic o Invio per finire.");
        NotifyAll();
    }

    /// <summary>Ridisegna la linea (bordo o interasse) dell'elemento selezionato.</summary>
    public void RedrawSelectedLine()
    {
        if (Selected is not { } e || e.Base.Kind != EdgeKind.Road || Project.FindEdge(e.Base.Id) is not { Owner: >= 0 } edge || edge.Owner != e.Id) return;
        StartTrace(edge.Id);
        Message = $"Ridisegna la linea di {e.Name}: clic per ogni punto, doppio clic o Invio per finire, Esc per lasciarla com'era.";
        NotifyAll();
    }

    public void StartNewElement(ElementType t, ElementMode m)
    {
        if (!PickableEdges().Any()) { Message = "Prima disegna almeno un elemento: poi potrai riempire lo spazio tra due elementi."; NotifyAll(); return; }
        if (Mode == Mode.Trace) TraceCancel();
        DraftType = t; DraftMode = m; DraftPolygon = false; DraftBase = null; DraftOther = null; Preview = null; RangeFixed = false;
        DraftWidth = Element.DefaultWidth(t);
        Selected = null;
        Mode = Mode.PickBase;
        Message = "Clicca sul contorno del primo elemento, nel punto in cui deve iniziare il nuovo elemento.";
        NotifyAll();
    }

    public void ClickPick(V2 w, double tol)
    {
        switch (Mode)
        {
            case Mode.PickBase:
                BeginRangeAt(w, tol);
                break;
            case Mode.PickRange:
                if (!RangeFixed)
                {
                    var b = DraftBase is { } r ? Project.GetEdge(r) : null;
                    if (b != null) DraftEnd = PL.Project(b, w).Station;
                    RangeFixed = true;
                    Message = $"Tratto evidenziato: {Math.Abs(DraftEnd - DraftStart):0.00} m. Correggi i valori se serve, poi «Conferma tratto».";
                    NotifyAll();
                }
                else BeginRangeAt(w, tol);
                break;
            case Mode.PickOther:
                var hit = NearestEdge(w, tol, DraftBase);
                if (hit != null)
                {
                    DraftOther = hit.Value.r;
                    BuildPreview();
                    Message = Preview?.Error ?? $"Anteprima tra {Project.EdgeName(DraftBase!.Value)} e {Project.EdgeName(DraftOther.Value)}. Premi «Salva».";
                    if (AutoCommit && Preview is { Error: null }) { Commit(); break; }
                    NotifyAll();
                }
                break;
        }
    }

    void BeginRangeAt(V2 w, double tol)
    {
        var hit = NearestEdge(w, tol);
        if (hit == null) return;
        DraftBase = hit.Value.r;
        DraftStart = DraftEnd = hit.Value.pr.Station;
        RangeFixed = false;
        Mode = Mode.PickRange;
        Message = $"Agganciato a: {Project.EdgeName(DraftBase.Value)}. Muovi il mouse lungo il bordo e clicca dove finisce il tratto.";
        NotifyAll();
    }

    public void HoverRange(V2 w)
    {
        if (Mode != Mode.PickRange || RangeFixed || DraftBase is not { } r) return;
        var b = Project.GetEdge(r);
        if (b != null) DraftEnd = PL.Project(b, w).Station;
    }

    public double DraftBaseLength => DraftBase is { } r && Project.GetEdge(r) is { } b ? PL.Length(b) : 0;

    public void SetRange(double from, double to)
    {
        double L = DraftBaseLength;
        DraftStart = Math.Clamp(from, 0, L); DraftEnd = Math.Clamp(to, 0, L); RangeFixed = true;
        if (Mode is Mode.SetWidth or Mode.PickOther) BuildPreview();
        NotifyAll();
    }

    public void WholeEdge()
    {
        if (DraftBase == null) return;
        SetRange(0, DraftBaseLength);
    }

    public void ConfirmRange()
    {
        if (DraftBase == null || Math.Abs(DraftEnd - DraftStart) < 0.1) { Message = "Il tratto è troppo corto."; NotifyAll(); return; }
        if (DraftStart > DraftEnd) (DraftStart, DraftEnd) = (DraftEnd, DraftStart);
        RangeFixed = true;
        // Sui bordi strada (facciate) si ignorano i piccoli gradini; sui bordi disegnati si segue la linea esatta
        DraftSmooth = DraftBase.Value.Kind == EdgeKind.Road ? 1.0 : 0;
        if (DraftMode == ElementMode.Offset)
        {
            Mode = Mode.SetWidth;
            DraftSide = SuggestSide();
            BuildPreview();
            Message = "Imposta la larghezza e controlla l'anteprima. «Inverti lato» se l'elemento va dall'altra parte. Poi «Salva».";
        }
        else
        {
            Mode = Mode.PickOther;
            Message = "Clicca sul contorno del secondo elemento: il nuovo elemento riempirà lo spazio tra i due.";
        }
        NotifyAll();
    }

    public void SetDraftWidth(double w)
    {
        DraftWidth = Math.Max(0.05, w);
        if (Mode == Mode.SetWidth) { BuildPreview(); NotifyRedraw(); }
    }

    public void SetDraftSmooth(double v)
    {
        DraftSmooth = Math.Max(0, v);
        if (Mode == Mode.SetWidth) { BuildPreview(); NotifyRedraw(); }
    }

    public void SetSelectedSmooth(double v)
    {
        if (Selected is not { Mode: ElementMode.Offset } e) return;
        e.Smooth = Math.Max(0, v);
        e.Profile.Clear();
        Project.Regenerate(); NotifyAll();
    }

    public void FlipDraftSide()
    {
        if (DraftCentered && Preview is { Centered: true }) return;
        DraftSide = -DraftSide;
        BuildPreview(); NotifyAll();
    }

    public void BuildPreview()
    {
        if (DraftBase is not { } b) { Preview = null; return; }
        if (DraftMode == ElementMode.Fill && DraftOther == null) { Preview = null; return; }
        Preview = new Element
        {
            Id = -1, Type = DraftType, Mode = DraftMode, Base = b,
            Start = Math.Min(DraftStart, DraftEnd), End = Math.Max(DraftStart, DraftEnd),
            Side = DraftSide, Width = DraftWidth, Smooth = DraftSmooth, Other = DraftOther,
            Centered = DraftMode == ElementMode.Offset && DraftCentered && DraftBase is { Kind: EdgeKind.Road } bb && Project.FindEdge(bb.Id) is { Owner: -2 },
            ToEnd = Math.Max(DraftStart, DraftEnd) >= DraftBaseLength - 0.01,
            Name = Project.NewName(DraftType)
        };
        Project.Build(Preview);
    }

    public void Commit()
    {
        if (Preview == null) { Message = DraftMode == ElementMode.Fill ? "Scegli prima il secondo bordo." : "Nessuna anteprima da disegnare."; NotifyAll(); return; }
        if (Preview.Error != null) { Message = Preview.Error; NotifyAll(); return; }
        var e = Preview;
        e.Id = Project.NextId++;
        if (e.Base.Kind == EdgeKind.Road && Project.FindEdge(e.Base.Id) is { Owner: -2 } own) { own.Owner = e.Id; own.Name = "Linea di " + e.Name; }
        Adopt(e);
        Project.Elements.Add(e);
        Project.Regenerate();
        Selected = e; Preview = null; DraftBase = null; DraftOther = null;
        Mode = Mode.Idle;
        Message = $"{e.Name} disegnato: {e.Metrics.Area:0.00} m². Puoi modificarne la larghezza o aggiungere un nuovo elemento.";
        NotifyAll();
    }

    public void CancelDraft(bool notify = true)
    {
        if (Mode == Mode.PickSection) { Mode = Mode.Idle; SectionA = null; Message = "Sezione annullata."; }
        ClearPick();
        if (Mode == Mode.PickVar) { Mode = Mode.Idle; VarStart = null; Message = "Variatore annullato."; }
        if (Mode is Mode.PickBase or Mode.PickRange or Mode.SetWidth or Mode.PickOther)
        {
            // la linea disegnata per un elemento non confermato si butta
            if (DraftBase is { Kind: EdgeKind.Road } db && Project.FindEdge(db.Id) is { Owner: -2 } tmp) Project.Edges.Remove(tmp);
            Mode = Mode.Idle; Preview = null; DraftBase = null; DraftOther = null;
            Message = "Inserimento annullato.";
        }
        if (notify) NotifyAll();
    }

    bool Blocked(V2 q)
    {
        if (Data?.InsideBuilding(q) == true) return true;
        foreach (var e in Project.Shown)
            if (e.Polygon.Count >= 3 && PL.PointInRing(e.Polygon, q)) return true;
        return false;
    }

    /// <summary>Sceglie il lato dell'offset: verso la strada, lontano dai muri e dagli elementi già disegnati.</summary>
    public int SuggestSide()
    {
        if (DraftBase is not { } r) return 1;
        if (r.Kind == EdgeKind.Outer && Project.Find(r.Id) is { Mode: ElementMode.Offset } po) return po.Side;
        if (r.Kind == EdgeKind.Inner && Project.Find(r.Id) is { Mode: ElementMode.Offset } pi) return -pi.Side;
        var line = Project.GetEdge(r);
        if (line == null) return 1;
        var b = PL.Sub(line, Math.Min(DraftStart, DraftEnd), Math.Max(DraftStart, DraftEnd));
        double L = PL.Length(b);
        int Score(int side)
        {
            int c = 0;
            for (int k = 1; k <= 9; k++)
            {
                var P = PL.PointAt(b, L * k / 10); var n = PL.TangentAt(b, L * k / 10).LeftNormal;
                if (Blocked(P + n * (side * Math.Min(DraftWidth, 1.0) * 0.5))) c++;
            }
            return c;
        }
        int sl = Score(1), sr = Score(-1);
        if (sl != sr) return sl < sr ? 1 : -1;
        var mid = PL.PointAt(b, L / 2); var nm = PL.TangentAt(b, L / 2).LeftNormal;
        foreach (var e in Project.Edges)
            if (e.Points.Count >= 2 && !(r.Kind == EdgeKind.Road && r.Id == e.Id))
                return V2.Dot(PL.Project(e.Points, mid).Point - mid, nm) >= 0 ? 1 : -1;
        return 1;
    }

    // ───────────────────────── Selezione e modifica ─────────────────────────

    public void SelectAt(V2 w)
    {
        // tra gli elementi sotto il clic vince il più piccolo (come sulla mappa, dove sta sopra): un parcheggio dentro la carreggiata
        Selected = ElementAt(w);
        ClearPick();
        if (Selected != null) SelectedSection = null;
        NotifyAll();
    }

    public void Select(Element? e)
    {
        if (!ReferenceEquals(e, Selected)) { VarSel = -1; if (Mode == Mode.PickVar) { Mode = Mode.Idle; VarStart = null; } }
        if (e != null) SelectedSection = null;
        ClearPick();
        Selected = e; NotifyAll();
    }

    /// <summary>Linea trasversale all'elemento alla progressiva t (dal lato della linea al lato opposto).</summary>
    public (V2 a, V2 b) CrossLine(Element e, double t)
    {
        var P = PL.PointAt(e.Guide, t); var n = PL.TangentAt(e.Guide, t).LeftNormal; double w = e.WidthAt(t);
        return e.Centered ? (P + n * (w / 2), P - n * (w / 2)) : (P, P + n * (e.Side * w));
    }

    public V2 CpWorld(Element e, WidthPoint cp)
    {
        var P = PL.PointAt(e.Guide, cp.T);
        var n = PL.TangentAt(e.Guide, cp.T).LeftNormal;
        return P + n * (e.Side * cp.W);
    }

    public int HitCp(V2 w, double tol)
    {
        if (Selected is not { Mode: ElementMode.Offset } e) return -1;
        for (int i = 0; i < e.Profile.Count; i++)
            if (V2.Dist(CpWorld(e, e.Profile[i]), w) < tol) return i;
        return -1;
    }

    /// <summary>Doppio clic sul bordo esterno: spezza la linea inserendo un punto di larghezza.</summary>
    public bool TryAddCp(V2 w, double tol)
    {
        if (Selected is not { Mode: ElementMode.Offset, Error: null } e) return false;
        var po = PL.Project(e.Outer, w);
        if (po.Distance > tol) return false;
        double L = PL.Length(e.Guide);
        double t = PL.Project(e.Guide, po.Point).Station;
        if (e.Profile.Count == 0)
        {
            e.Profile.Add(new WidthPoint { T = 0, W = e.Width });
            e.Profile.Add(new WidthPoint { T = L, W = e.Width });
        }
        if (e.Profile.Any(p => Math.Abs(p.T - t) < 0.05)) return false;
        e.Profile.Add(new WidthPoint { T = t, W = e.WidthAt(t) });
        e.Profile.Sort((a, b) => a.T.CompareTo(b.T));
        Project.Regenerate();
        DragCp = e.Profile.FindIndex(p => Math.Abs(p.T - t) < 1e-9);
        Message = $"Bordo spezzato a {t:0.00} m. Trascina il punto per cambiare la larghezza in quel punto.";
        NotifyAll();
        return true;
    }

    public void MoveCp(V2 w)
    {
        if (Selected is not { Mode: ElementMode.Offset } e || DragCp < 0 || DragCp >= e.Profile.Count) return;
        var cp = e.Profile[DragCp];
        double L = PL.Length(e.Guide);
        double lo = DragCp > 0 ? e.Profile[DragCp - 1].T + 0.05 : 0;
        double hi = DragCp < e.Profile.Count - 1 ? e.Profile[DragCp + 1].T - 0.05 : L;
        bool endpoint = DragCp == 0 || DragCp == e.Profile.Count - 1;
        double t = endpoint ? cp.T : Math.Clamp(PL.Project(e.Guide, w).Station, lo, hi);
        var P = PL.PointAt(e.Guide, t); var n = PL.TangentAt(e.Guide, t).LeftNormal;
        double wd = V2.Dot(w - P, n) * e.Side;
        cp.T = t; cp.W = Math.Clamp(Math.Round(wd, 2), 0.05, 100);
        Project.Regenerate();
        Message = $"Punto a {cp.T:0.00} m: larghezza {cp.W:0.00} m";
        NotifyAll();
    }

    public void RemoveCp(int i)
    {
        if (Selected is not { } e || i < 0 || i >= e.Profile.Count) return;
        e.Profile.RemoveAt(i);
        if (e.Profile.Count < 2) { e.Width = e.Profile.Count == 1 ? e.Profile[0].W : e.Width; e.Profile.Clear(); }
        Project.Regenerate(); NotifyAll();
    }

    /// <summary>Cambia il tipo dell'elemento selezionato; se il nome era quello automatico («Marciapiede 2»), si aggiorna.</summary>
    public void SetSelectedType(ElementType t)
    {
        if (Selected is not { } e || e.Type == t) return;
        var old = e.Type;
        bool autoName = System.Text.RegularExpressions.Regex.IsMatch(e.Name, "^" + System.Text.RegularExpressions.Regex.Escape(Element.TypeLabel(old)) + @" \d+$");
        if (autoName) { e.Name = ""; e.Name = Project.NewName(t); }   // calcolato prima del cambio: l'elemento non conta se stesso
        e.Type = t;
        foreach (var edge in Project.Edges.Where(x => x.Owner == e.Id)) edge.Name = "Linea di " + e.Name;
        Project.Regenerate();
        Message = $"{e.Name}: tipo cambiato da «{Element.TypeLabel(old)}» a «{Element.TypeLabel(t)}». Forma e larghezza restano le stesse.";
        NotifyAll();
    }

    /// <summary>Strisce di mezzeria dell'elemento selezionato: numero (-1 = automatico) e tipo.</summary>
    public void SetSelectedDividers(int count, DividerStyle style)
    {
        if (Selected is not { SupportsDividers: true } e) return;
        if (e.Dividers == count && e.DividerStyle == style) return;
        e.Dividers = count; e.DividerStyle = style;
        int n = e.EffectiveDividers;
        Message = n == 0 ? $"{e.Name}: nessuna striscia di mezzeria."
            : $"{e.Name}: {n} {(n == 1 ? "striscia" : "strisce")} di mezzeria ({style.ToString().ToLowerInvariant()}), {n + 1} parti larghe {e.Metrics.WMean / (n + 1):0.00} m.";
        NotifyAll();
    }

    public void SetSelectedStalls(bool on, double length)
    {
        if (Selected is not { Type: ElementType.Parcheggio } e) return;
        e.StallLines = on; e.StallLength = Math.Clamp(Math.Round(length, 2), 0.5, 50);
        Project.Regenerate();
        Message = "";
        NotifyAll();
    }

    /// <summary>Ciclopedonale: bici e pedoni separati (striscia continua a metà) oppure percorso misto.</summary>
    public void SetSelectedSeparated(bool on)
    {
        if (Selected is not { Type: ElementType.Ciclopedonale } e || e.Separated == on) return;
        e.Separated = on; e.DividerStyle = DividerStyle.Continua;
        Project.Regenerate();
        Message = "";
        NotifyAll();
    }

    public void SetSelectedHeight(double h)
    {
        if (Selected is not { HasHeight: true } e) return;
        e.Height = Math.Clamp(Math.Round(h, 2), 0, 2);
        Message = "";
        NotifyAll();
    }

    public void SetSelectedWidth(double value)
    {
        if (Selected is not { Mode: ElementMode.Offset } e) return;
        value = Math.Max(0.05, value);
        double delta = value - e.Width;
        e.Width = value;
        foreach (var p in e.Profile) p.W = Math.Max(0.05, p.W + delta);
        Project.Regenerate(); NotifyAll();
    }

    public void FlipSelected()
    {
        if (Selected is not { Mode: ElementMode.Offset } e) return;
        e.Side = -e.Side; Project.Regenerate(); NotifyAll();
    }

    public void ResetProfile()
    {
        if (Selected is not { } e) return;
        e.Profile.Clear(); e.Variators.Clear(); Project.Regenerate(); NotifyAll();
    }

    // ───────────────────────── Variatori di sezione ─────────────────────────

    /// <summary>Durante la scelta del variatore: progressiva dell'inizio già cliccato (null = si sceglie l'inizio).</summary>
    public double? VarStart;
    /// <summary>Variatore da evidenziare (l'ultimo aggiunto o modificato).</summary>
    public int VarSel = -1;

    public bool CanAddVariator => Mode == Mode.Idle && Selected is { Mode: ElementMode.Offset, Error: null } s && s.Guide.Count >= 2;

    public void StartVariator()
    {
        if (!CanAddVariator) return;
        Mode = Mode.PickVar; VarStart = null;
        Message = "Variatore di sezione: clic sull'elemento nel punto in cui inizia il cambio di larghezza (Esc annulla).";
        NotifyAll();
    }

    /// <summary>Progressiva sulla linea guida dell'elemento selezionato più vicina a w.</summary>
    public double? StationOnSelected(V2 w) =>
        Selected is { Mode: ElementMode.Offset } e && e.Guide.Count >= 2 ? PL.Project(e.Guide, w).Station : null;

    public void ClickVar(V2 w)
    {
        if (Mode != Mode.PickVar || Selected is not { } e || StationOnSelected(w) is not { } t) return;
        if (VarStart is not { } t1)
        {
            VarStart = t;
            Message = $"Inizio a {t:0.00} m. Ora clic nel punto in cui finisce il cambio di larghezza (oltre quel punto vale la nuova larghezza).";
            NotifyAll();
            return;
        }
        if (Math.Abs(t - t1) < 0.2) { Message = "Il tratto del variatore è troppo corto: clic un po' più lontano dall'inizio."; NotifyAll(); return; }
        e.Variators.Add(new SectionVariator { T1 = t1, T2 = t, Delta = 0 });
        VarSel = e.Variators.Count - 1;
        Mode = Mode.Idle; VarStart = null;
        Project.Regenerate();
        Message = $"Variatore {VarSel + 1} di {e.Name}: da {t1:0.00} a {t:0.00} m. Usa + e − per allargare o restringere il tratto in uscita di 10 cm alla volta.";
        NotifyAll();
    }

    /// <summary>Maniglia del variatore: a metà della linea trasversale di inizio (end = false) o di fine (end = true).</summary>
    public V2 VarHandle(Element e, int i, bool end)
    {
        var (a, b) = CrossLine(e, end ? e.Variators[i].T2 : e.Variators[i].T1);
        return (a + b) * 0.5;
    }

    public (int i, bool end)? HitVarHandle(V2 w, double tol)
    {
        if (Mode != Mode.Idle || Selected is not { Mode: ElementMode.Offset, Error: null } e || e.Guide.Count < 2) return null;
        (int, bool)? best = null; double bd = tol * 1.3;
        for (int i = 0; i < e.Variators.Count; i++)
            foreach (var end in new[] { false, true })
            {
                double d = V2.Dist(VarHandle(e, i, end), w);
                if (d < bd) { bd = d; best = (i, end); }
            }
        return best;
    }

    /// <summary>Sposta l'inizio o la fine di un variatore lungo la linea (trascinando la maniglia sulla mappa).</summary>
    public void MoveVarHandle(int i, bool end, V2 w, bool final)
    {
        if (Selected is not { } e || i < 0 || i >= e.Variators.Count || StationOnSelected(w) is not { } t) return;
        var v = e.Variators[i];
        double other = end ? v.T1 : v.T2;
        // l'inizio e la fine restano ad almeno 20 cm e dalla stessa parte di prima
        double dir = Math.Sign(v.T2 - v.T1); if (dir == 0) dir = 1;
        if (end) t = dir > 0 ? Math.Max(t, other + 0.2) : Math.Min(t, other - 0.2);
        else t = dir > 0 ? Math.Min(t, other - 0.2) : Math.Max(t, other + 0.2);
        t = Math.Clamp(t, 0, PL.Length(e.Guide));
        if (end) v.T2 = Math.Round(t, 2); else v.T1 = Math.Round(t, 2);
        VarSel = i;
        Project.Regenerate();
        if (final)
        {
            Message = $"Variatore {i + 1}: da {v.T1:0.00} a {v.T2:0.00} m (tratto di {Math.Abs(v.T2 - v.T1):0.00} m).";
            NotifyAll();
        }
        else NotifyRedraw();
    }

    public void CancelVariator()
    {
        if (Mode != Mode.PickVar) return;
        Mode = Mode.Idle; VarStart = null;
        Message = "Variatore annullato.";
        NotifyAll();
    }

    /// <summary>Larghezza prima e dopo il variatore i dell'elemento selezionato.</summary>
    public (double before, double after) VariatorWidths(Element e, int i)
    {
        var v = e.Variators[i];
        double eps = 0.01 * Math.Sign(v.T2 - v.T1 == 0 ? 1 : v.T2 - v.T1);
        return (e.WidthAt(v.T1 - eps), e.WidthAt(v.T2 + eps));
    }

    public void ChangeVariator(int i, double delta)
    {
        if (Selected is not { } e || i < 0 || i >= e.Variators.Count) return;
        var v = e.Variators[i];
        double nd = Math.Round((v.Delta + delta) * 100) / 100;
        // la larghezza in uscita non scende sotto 10 cm
        var (before, _) = VariatorWidths(e, i);
        if (before + nd < 0.10 - 1e-9) { Message = "Il tratto in uscita non può essere più stretto di 10 cm."; NotifyAll(); return; }
        v.Delta = nd; VarSel = i;
        Project.Regenerate();
        var (b2, a2) = VariatorWidths(e, i);
        Message = $"Variatore {i + 1}: da {b2:0.00} m a {a2:0.00} m ({(nd >= 0 ? "+" : "")}{nd:0.00} m).";
        NotifyAll();
    }

    public void RemoveVariator(int i)
    {
        if (Selected is not { } e || i < 0 || i >= e.Variators.Count) return;
        e.Variators.RemoveAt(i); VarSel = -1;
        Project.Regenerate();
        Message = $"Variatore {i + 1} eliminato.";
        NotifyAll();
    }

    public void SetSelectedRange(double from, double to)
    {
        if (Selected is not { } e) return;
        var b = Project.GetEdge(e.Base); if (b == null) return;
        double L = PL.Length(b);
        e.Start = Math.Clamp(Math.Min(from, to), 0, L); e.End = Math.Clamp(Math.Max(from, to), 0, L);
        e.ToEnd = e.End >= L - 0.01;
        Project.Regenerate(); NotifyAll();
    }

    // ───────────────────────── Sezioni ─────────────────────────

    /// <summary>Sezione aperta (si vede il disegno in sezione e si possono trascinare A e B).</summary>
    public CrossSection? SelectedSection;
    /// <summary>Mentre si traccia una sezione: il punto A già cliccato.</summary>
    public V2? SectionA;

    public void StartSection()
    {
        if (Data == null) return;
        if (Mode == Mode.Trace) TraceCancel(); else if (Mode != Mode.Idle) CancelDraft(false);
        Selected = null; SelectedSection = null; SectionA = null;
        Mode = Mode.PickSection;
        Message = "Nuova sezione: clic sul punto A (da un lato della strada), poi sul punto B (dall'altro lato). Esc annulla.";
        NotifyAll();
    }

    public void ClickSection(V2 w, double tol)
    {
        if (Mode != Mode.PickSection) return;
        var p = FindSnap(w, tol)?.P ?? w;
        if (SectionA is not { } a) { SectionA = p; Message = "Ora clic sul punto B, dall'altra parte della strada."; NotifyAll(); return; }
        if (V2.Dist(a, p) < 0.5) { Message = "A e B sono troppo vicini: clic più lontano."; NotifyAll(); return; }
        int n = 1; while (Project.Sections.Any(s => s.Name == $"Sezione {n}")) n++;
        var sec = new CrossSection { Id = Project.NextId++, Name = $"Sezione {n}", A = a, B = p };
        Project.Sections.Add(sec);
        Mode = Mode.Idle; SectionA = null; Selected = null; SelectedSection = sec;
        var r = SectionCalc.Compute(sec, Project, Data);
        Message = $"{sec.Name}: {r.Pieces.Count} tratti di elementi, larghezza complessiva {r.ElementsWidth:0.00} m. «Inverti verso» cambia il lato da cui la guardi.";
        NotifyAll();
    }

    public void SelectSection(CrossSection? s) { SelectedSection = s; if (s != null) Selected = null; NotifyAll(); }

    public void FlipSection()
    {
        if (SelectedSection is not { } s) return;
        s.Flip = !s.Flip; Message = $"{s.Name}: ora la guardi dall'altro lato."; NotifyAll();
    }

    public void DeleteSection(CrossSection s)
    {
        Project.Sections.Remove(s);
        if (SelectedSection == s) SelectedSection = null;
        Message = $"{s.Name} eliminata."; NotifyAll();
    }

    /// <summary>Sezione la cui linea passa vicino a w.</summary>
    public CrossSection? HitSection(V2 w, double tol) =>
        Project.Sections.Select(s => (s, d: PL.Project(new List<V2> { s.A, s.B }, w).Distance)).Where(x => x.d < tol).OrderBy(x => x.d).Select(x => x.s).FirstOrDefault();

    /// <summary>Estremo (A = 0, B = 1) della sezione aperta vicino a w.</summary>
    public int HitSectionEnd(V2 w, double tol)
    {
        if (SelectedSection is not { } s) return -1;
        double da = V2.Dist(s.A, w), db = V2.Dist(s.B, w);
        if (Math.Min(da, db) > tol * 1.3) return -1;
        return da <= db ? 0 : 1;
    }

    public void MoveSectionEnd(int end, V2 w, double tol, bool final)
    {
        if (SelectedSection is not { } s) return;
        var p = FindSnap(w, tol)?.P ?? w;
        var other = end == 0 ? s.B : s.A;
        if (V2.Dist(p, other) < 0.5) return;
        if (end == 0) s.A = p; else s.B = p;
        if (final) { Message = $"{s.Name}: {s.Length:0.00} m."; NotifyAll(); } else NotifyRedraw();
    }

    // ───────────────────────── Conversione in poligono ─────────────────────────

    /// <summary>Si può convertire l'elemento selezionato in poligono? null = sì, altrimenti il motivo.</summary>
    public string? PolygonConversionBlocker()
    {
        if (Selected is not { } e) return "Nessun elemento selezionato.";
        if (e.Mode == ElementMode.Polygon) return $"{e.Name} è già un poligono.";
        if (e.Error != null || e.Polygon.Count < 3) return $"{e.Name} ha un errore e non ha una forma da convertire.";
        var deps = Project.Elements.Where(x => x != e && Project.DependsOn(x, e.Id) && (x.Base.Kind != EdgeKind.Road || x.Other is { Kind: not EdgeKind.Road })).ToList();
        if (deps.Count > 0) return $"Non posso convertire {e.Name}: {string.Join(", ", deps.Select(d => d.Name))} {(deps.Count == 1 ? "è riempito" : "sono riempiti")} usando i suoi lati. Elimina prima {(deps.Count == 1 ? "quell'elemento" : "quegli elementi")}.";
        return null;
    }

    /// <summary>Elementi le cui linee sono agganciate al contorno dell'elemento selezionato (dopo la conversione non lo seguiranno più).</summary>
    public List<Element> LinkedToSelected()
    {
        if (Selected is not { } e) return new();
        var ids = Project.Edges.Where(ed => ed.Anchors?.Any(a => a.El == e.Id) == true && ed.Owner != e.Id).Select(ed => ed.Owner).ToHashSet();
        return Project.Elements.Where(x => ids.Contains(x.Id)).ToList();
    }

    /// <summary>
    /// Converte l'elemento tracciato selezionato in poligono: il contorno attuale diventa il poligono (non si torna indietro).
    /// Larghezza, variatori, strisce e lato non valgono più; restano tipo, nome e superficie.
    /// </summary>
    public void ConvertSelectedToPolygon()
    {
        if (PolygonConversionBlocker() is { } why) { Message = why; NotifyAll(); return; }
        var e = Selected!;
        double areaBefore = e.Metrics.Area;
        // contorno attuale, chiuso implicitamente; i vertici quasi allineati (sotto il centimetro) si tolgono
        var ring = CleanRing(e.Polygon);
        FreezeLinksTo(e.Id);
        // la linea dell'elemento diventa il contorno del poligono
        MakePolygon(e, ring);
        VarSel = -1;
        Project.Regenerate();
        Message = $"{e.Name} è ora un poligono di {e.Polygon.Count} vertici ({e.Metrics.Area:0.00} m², prima {areaBefore:0.00} m²). Puoi spostarne i vertici con «Modifica la linea».";
        NotifyAll();
    }

    /// <summary>Le linee agganciate al contorno dell'elemento restano dove sono: si congelano com'è ora, compreso il tratto che seguiva il contorno.</summary>
    void FreezeLinksTo(int elId)
    {
        foreach (var ed in Project.Edges)
        {
            if (ed.Anchors is not { } an || !an.Any(x => x.El == elId) || ed.Points.Count < 2) continue;
            var keys = an.Where(x => !x.Auto).ToList();
            var frozen = new List<EdgeAnchor>();
            foreach (var p in ed.Points)
            {
                // i vertici che coincidono con un punto chiave restano punti chiave (agganciati ad altri elementi, se lo erano)
                var k = keys.FirstOrDefault(x => V2.Dist(new V2(x.X, x.Y), p) < 0.01);
                if (k != null && !frozen.Contains(k)) { if (k.El == elId) { k.El = -1; } k.Follow = false; frozen.Add(k); }
                else frozen.Add(new EdgeAnchor { X = p.X, Y = p.Y, Auto = true });
            }
            ed.Anchors = frozen;
        }
    }

    /// <summary>Rende l'elemento un poligono con il contorno dato (la sua linea diventa il contorno).</summary>
    void MakePolygon(Element e, List<V2> ring)
    {
        var closed = new List<V2>(ring) { ring[0] };
        var own = e.Base.Kind == EdgeKind.Road && Project.FindEdge(e.Base.Id) is { } be && be.Owner == e.Id ? be : null;
        if (own == null) { own = Project.AddEdge("Linea di " + e.Name); own.Owner = e.Id; }
        own.Points = ring; own.Anchors = null;
        e.Mode = ElementMode.Polygon; e.Base = new EdgeRef(EdgeKind.Road, own.Id); e.Other = null;
        e.Centered = false; e.Profile.Clear(); e.Variators.Clear(); e.Dividers = -1;
        e.Start = 0; e.End = PL.Length(closed); e.ToEnd = true; e.Side = 1;
    }

    public void DeleteSelected()
    {
        if (Selected is not { } e) return;
        var deps = Project.Elements.Where(x => x != e && Project.DependsOn(x, e.Id)).ToList();
        if (deps.Count > 0)
        {
            Message = $"Non posso eliminare {e.Name}: prima elimina gli elementi agganciati ({string.Join(", ", deps.Select(d => d.Name))}).";
            NotifyAll(); return;
        }
        Project.Elements.Remove(e);
        Project.Edges.RemoveAll(x => x.Owner == e.Id);
        Selected = null; Project.Regenerate();
        Message = $"{e.Name} eliminato.";
        NotifyAll();
    }

    public BBox Extent()
    {
        var b = Data?.Box ?? BBox.Empty;
        foreach (var e in Project.Edges) if (e.Points.Count > 0) b = b.Union(BBox.Of(e.Points));
        return b;
    }
}
