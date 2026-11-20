using System.Globalization;
using CicloDraft.Geometry;
using CicloDraft.IO;
using CicloDraft.Model;

namespace CicloDraft;

/// <summary>
/// Prova automatica del flusso completo senza interfaccia:
///   CicloDraft --selftest esempio.gpkg cartella_output
/// Usa il GeoPackage di esempio fornito (via con edifici su due lati).
/// </summary>
public static class SelfTest
{
    public static int Run(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string gpkg = a.Length > 0 ? a[0] : "esempio_via.gpkg";
        string outDir = a.Length > 1 ? a[1] : ".";
        Directory.CreateDirectory(outDir);

        var data = GpkgReader.Load(gpkg);
        Console.WriteLine($"CRS: {data.Crs}  geografico: {data.Geographic}  origine: {data.Origin}");
        foreach (var l in data.Layers) Console.WriteLine($"  livello {l.Name}: {l.GeomType}, {l.FeatureCount} el., ruolo {(LayerRole)l.Role}");

        var ed = new Editor();
        ed.SetData(data); ed.Project.EnsureLegacyEdges();
        Scenario(ed, data);

        Console.WriteLine();
        Console.WriteLine($"{"Elemento",-22}{"Lungh.",9}{"Sup.",10}{"Lmin",7}{"Lmax",7}{"Lmed",7}  errore");
        foreach (var e in ed.Project.Elements)
            Console.WriteLine($"{e.Name,-22}{e.Metrics.Length,9:0.00}{e.Metrics.Area,10:0.00}{e.Metrics.WMin,7:0.00}{e.Metrics.WMax,7:0.00}{e.Metrics.WMean,7:0.00}  {e.Error}");

        if (Environment.GetEnvironmentVariable("CD_DEBUG") == "1")
            foreach (var e in ed.Project.Elements)
            {
                Console.WriteLine($"{e.Name}: inner {e.Inner.Count} pts, guide {e.Guide.Count}, outer {e.Outer.Count}");
                double lin = PL.Length(e.Inner);
                for (double s = 0; s <= lin; s += 2)
                {
                    var P = PL.PointAt(e.Inner, s); var pr = PL.Project(e.Outer, P);
                    if (pr.Distance > e.Metrics.WMean + 0.4) Console.WriteLine($"   s={s:0.0} P={P + data.Origin} d={pr.Distance:0.00}");
                }
                if (e.Name.StartsWith("Pista")) Console.WriteLine("   outer: " + string.Join(" ", e.Outer.Select(v => (v + data.Origin).ToString())));
            }
        DxfWriter.Write(Path.Combine(outDir, "prova.dxf"), ed.Project, data, true, true);
        CsvWriter.Write(Path.Combine(outDir, "prova.csv"), ed.Project);
        ProjectFile.Save(Path.Combine(outDir, "prova.ciclo"), ed.Project, data);

        // Ricarico il progetto e confronto le superfici
        var dto = ProjectFile.Read(Path.Combine(outDir, "prova.ciclo"));
        var p2 = ProjectFile.ToProject(dto, data.Origin);
        double diff = ed.Project.Elements.Zip(p2.Elements).Max(z => Math.Abs(z.First.Metrics.Area - z.Second.Metrics.Area));
        Console.WriteLine($"Riapertura progetto: differenza massima superfici {diff:0.000000} m²");
        bool ok = ed.Project.Elements.All(e => e.Error == null) && diff < 1e-6;
        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FALLITO");
        return ok ? 0 : 1;
    }

    /// <summary>Esegue i passi dell'esempio (bordi, marciapiedi, ciclabile, carreggiata, modifiche).</summary>
    public static void Scenario(Editor ed, GpkgData data)
    {
        V2 L(double e, double n) => new V2(e, n) - data.Origin;
        double tol = 0.5;

        // Bordo sinistro: si clicca solo sugli spigoli estremi di ogni edificio,
        // "segui facciata" inserisce i vertici intermedi (rientranze comprese).
        ed.StartTrace(0);
        foreach (var p in new[] { (1514000.0, 5034008.0), (1514040.0, 5034008.0), (1514046.0, 5034008.0), (1514100.0, 5034008.0),
                                  (1514106.0, 5034008.0), (1514200.0, 5034008.0) })
            ed.TraceClick(L(p.Item1, p.Item2), tol);
        Console.WriteLine($"Bordo sinistro: {ed.TracePts.Count} vertici (cliccati 6)");
        ed.TraceFinish();

        ed.StartTrace(1);
        foreach (var p in new[] { (1514000.0, 5033992.0), (1514070.0, 5033992.0), (1514076.0, 5033992.0), (1514200.0, 5033992.0) })
            ed.TraceClick(L(p.Item1, p.Item2), tol);
        ed.TraceFinish();
        Console.WriteLine(ed.Message);

        // 1) Marciapiede sinistro sui primi 100 m del bordo sinistro
        ed.StartNewElement(ElementType.Marciapiede, ElementMode.Offset);
        ed.ClickPick(PL.PointAt(ed.Project.Edges[0].Points, 0), tol);
        ed.SetRange(0, 100);
        ed.ConfirmRange();
        Console.WriteLine($"Lato suggerito marciapiede sinistro: {ed.DraftSide} (atteso -1, verso la strada)");
        ed.SetDraftWidth(1.5);
        ed.Commit();
        var sx = ed.Selected!;

        // 2) Pista ciclabile agganciata al bordo esterno del marciapiede
        ed.StartNewElement(ElementType.Ciclabile, ElementMode.Offset);
        ed.ClickPick(sx.Outer[0], tol);
        ed.WholeEdge();
        ed.ConfirmRange();
        Console.WriteLine($"Lato suggerito ciclabile: {ed.DraftSide}");
        ed.SetDraftWidth(2.5);
        ed.Commit();
        var bike = ed.Selected!;

        // 3) Marciapiede destro sul bordo destro, primi 100 m
        ed.StartNewElement(ElementType.Marciapiede, ElementMode.Offset);
        ed.ClickPick(ed.Project.Edges[1].Points[0], tol);
        ed.SetRange(0, 100);
        ed.ConfirmRange();
        Console.WriteLine($"Lato suggerito marciapiede destro: {ed.DraftSide} (atteso +1)");
        ed.SetDraftWidth(1.75);
        ed.Commit();
        var dx = ed.Selected!;

        // 4) Carreggiata tra il marciapiede destro e la ciclabile
        ed.StartNewElement(ElementType.Carreggiata, ElementMode.Fill);
        ed.ClickPick(dx.Outer[0], tol);
        ed.WholeEdge();
        ed.ConfirmRange();
        ed.ClickPick(bike.Outer[0], tol);
        ed.Commit();
        var road = ed.Selected!;

        // 5) Modifiche: allargo il marciapiede sinistro di 0,5 m e spezzo il bordo esterno a 50 m
        ed.Select(sx);
        ed.SetSelectedWidth(2.0);
        var at50 = PL.PointAt(sx.Outer, 50);
        ed.TryAddCp(at50, tol);
        ed.MoveCp(at50 + new V2(0, -1.0)); // trascino 1 m verso la strada
        ed.DragCp = -1;

    }

    /// <summary>
    /// Esempio con curve: i punti cliccati (spigoli delle facciate) arrivano da un file JSON
    /// {"left":[[E,N],...],"right":[[E,N],...]} prodotto da make_sample_curve.py --clicks.
    /// </summary>
    public static void CurveScenario(Editor ed, GpkgData data, string clicksJson)
    {
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(clicksJson));
        List<V2> Pts(string k) => doc.RootElement.GetProperty(k).EnumerateArray()
            .Select(a => new V2(a[0].GetDouble(), a[1].GetDouble()) - data.Origin).ToList();
        double tol = 0.3;
        for (int side = 0; side < 2; side++)
        {
            ed.StartTrace(side);
            foreach (var p in Pts(side == 0 ? "left" : "right")) ed.TraceClick(p, tol);
            ed.TraceFinish();
        }
        Console.WriteLine($"Bordo sinistro {PL.Length(ed.Project.Edges[0].Points):0.00} m, {ed.Project.Edges[0].Points.Count} vertici; " +
                          $"bordo destro {PL.Length(ed.Project.Edges[1].Points):0.00} m, {ed.Project.Edges[1].Points.Count} vertici");

        Element Add(ElementType t, ElementMode m, V2 pick, double w, Func<V2>? other = null)
        {
            ed.StartNewElement(t, m);
            ed.ClickPick(pick, tol);
            ed.WholeEdge();
            ed.ConfirmRange();
            if (m == ElementMode.Offset) ed.SetDraftWidth(w);
            else ed.ClickPick(other!(), tol);
            ed.Commit();
            Console.WriteLine($"  {ed.Selected?.Name}: lato {ed.Selected?.Side}, {ed.Message}");
            return ed.Selected!;
        }
        var sx = Add(ElementType.Marciapiede, ElementMode.Offset, ed.Project.Edges[0].Points[1], 1.5);
        if (Environment.GetEnvironmentVariable("CICLODRAFT_NUOVI") == "1")
        {
            // marciapiede, corsia ciclabile, striscia gialla, corsia doppia fino al marciapiede destro
            var cc = Add(ElementType.CorsiaCiclabile, ElementMode.Offset, PL.PointAt(sx.Outer, 5), 1.5);
            var sep = Add(ElementType.Separatore, ElementMode.Offset, PL.PointAt(cc.Outer, 5), 0.5);
            var dx2 = Add(ElementType.Marciapiede, ElementMode.Offset, ed.Project.Edges[1].Points[1], 1.5);
            Add(ElementType.CorsiaDoppia, ElementMode.Fill, PL.PointAt(dx2.Outer, 5), 0, () => PL.PointAt(sep.Outer, 5));
            return;
        }
        var bike = Add(ElementType.Ciclabile, ElementMode.Offset, PL.PointAt(sx.Outer, 5), 2.5);
        var dx = Add(ElementType.Marciapiede, ElementMode.Offset, ed.Project.Edges[1].Points[1], 1.5);
        Add(ElementType.Carreggiata, ElementMode.Fill, PL.PointAt(dx.Outer, 5), 0, () => PL.PointAt(bike.Outer, 5));
    }

    /// <summary>
    /// CicloDraft --selftest-anello dati.gpkg cartella: bordo attorno a due edifici vicini, marciapiede tutto attorno a ciascuno,
    /// poi un nuovo bordo agganciato ai bordi esterni dei due marciapiedi e una ciclabile lungo quel bordo.
    /// </summary>
    public static int RunRing(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var data = GpkgReader.Load(a[0]);
        var ed = new Editor(); ed.SetData(data);
        var axis = data.RingsWithRole(LayerRole.Sfondo).Select(x => x.ring).Where(r => !r.Closed).OrderByDescending(r => PL.Length(r.Pts)).First().Pts;
        double AL = PL.Length(axis);
        var mid = PL.PointAt(axis, AL / 2); var nrm = PL.TangentAt(axis, AL / 2).LeftNormal;
        // due edifici dallo stesso lato (sinistro dell'asse), i più vicini al centro della via
        var blds = data.RingsWithRole(LayerRole.Muri).Select(x => x.ring).Where(r => r.Closed)
            .Select(r => (r, c: new V2(r.Pts.Average(p => p.X), r.Pts.Average(p => p.Y))))
            .Where(x => V2.Dot(x.c - mid, nrm) > 0)
            .OrderBy(x => V2.Dist(x.c, mid)).Take(2).OrderBy(x => PL.Project(axis, x.c).Station).ToList();
        double tol = 0.3;
        var walks = new List<Element>();
        int k = 0;
        bool seam = Environment.GetEnvironmentVariable("RING_SEAM") == "1";
        foreach (var (r, c) in blds)
        {
            ed.AddRoadEdge();
            var rp = r.Pts.ToList();
            if (seam)
            {
                // il giro parte dallo spigolo verso la strada: il punto di chiusura cade proprio dove si aggancia il nuovo bordo
                int k0 = Enumerable.Range(0, rp.Count).OrderBy(i => PL.Project(axis, rp[i]).Distance).First();
                rp = rp.Skip(k0).Concat(rp.Take(k0)).ToList();
            }
            foreach (var p in rp) ed.TraceClick(p, tol);
            ed.TraceClick(rp[0], tol);
            ed.TraceFinish();
            var edge = ed.Project.Edges[^1];
            ed.RenameEdge(edge.Id, $"Edificio {++k}");
            Console.WriteLine($"  {edge.Name}: {edge.Points.Count} vertici, chiuso {V2.Dist(edge.Points[0], edge.Points[^1]) < 0.01}");
            ed.StartNewElement(ElementType.Marciapiede, ElementMode.Offset);
            ed.ClickPick(PL.PointAt(edge.Points, 1), tol);
            ed.WholeEdge(); ed.ConfirmRange(); ed.SetDraftWidth(1.5);
            // verso l'esterno dell'edificio
            var tp = PL.PointAt(edge.Points, 1) + PL.TangentAt(edge.Points, 1).LeftNormal * ed.DraftSide;
            if (PL.PointInRing(r.Pts, tp)) ed.FlipDraftSide();
            ed.Commit();
            walks.Add(ed.Selected!);
            { var w0 = ed.Selected!; Console.WriteLine($"    guida chiusa {V2.Dist(w0.Guide[0], w0.Guide[^1]):0.000}, esterno chiuso {V2.Dist(w0.Outer[0], w0.Outer[^1]):0.000}, poligono valido {Project.ToNts(w0.Polygon)?.IsValid}, interno {w0.Inner[0]} esterno {w0.Outer[0]}…{w0.Outer[^1]}"); }
            Console.WriteLine($"  {ed.Selected!.Name}: {ed.Selected.Metrics.Area:0.0} m², errore {ed.Selected.Error}");
        }
        // nuovo bordo agganciato ai bordi esterni dei due marciapiedi (lato strada)
        ed.AddRoadEdge();
        foreach (var w in walks)
        {
            var ring = w.Polygon;
            double s0 = PL.Project(axis, new V2(ring.Average(p => p.X), ring.Average(p => p.Y))).Station;
            double ext = ring.Max(p => PL.Project(axis, p).Station) - ring.Min(p => PL.Project(axis, p).Station);
            foreach (double s in new[] { s0 - ext * 0.45, s0 + ext * 0.45 })
            {
                var target = PL.PointAt(axis, s);
                var near = Environment.GetEnvironmentVariable("RING_MID") == "1" ? PL.Project(w.Outer, target).Point : w.Outer.OrderBy(p => V2.Dist(p, target)).First();
                var snap = ed.FindSnap(near, tol);
                Console.WriteLine($"    clic su {near} → aggancio {snap?.Key ?? snap?.Layer?.Name ?? "nessuno"}");
                ed.TraceClick(near, tol);
            }
        }
        ed.TraceFinish();
        var ne = ed.Project.Edges[^1];
        ed.RenameEdge(ne.Id, "Fronte marciapiedi");
        Console.WriteLine($"  {ne.Name}: {PL.Length(ne.Points):0.00} m, {ne.Points.Count} vertici");
        ed.StartNewElement(ElementType.Ciclabile, ElementMode.Offset);
        ed.ClickPick(PL.PointAt(ne.Points, 1), tol); ed.WholeEdge(); ed.ConfirmRange(); ed.SetDraftWidth(2.0);
        // il lato lo sceglie il programma (lontano dai marciapiedi già disegnati)
        ed.Commit();
        var bike = ed.Selected!;
        Console.WriteLine($"  {bike.Name}: {bike.Metrics.Length:0.00} m, {bike.Metrics.Area:0.0} m², errore {bike.Error}");
        foreach (var w in walks)
        {
            double ov = Project.ToNts(w.Polygon)!.Buffer(0).Intersection(Project.ToNts(bike.Polygon)!.Buffer(0)).Area;
            Console.WriteLine($"  sovrapposizione {w.Name} / ciclabile: {ov:0.000} m²");
        }
        ProjectFile.Save(Path.Combine(a[1], "anello.ciclo"), ed.Project, data);
        Console.WriteLine($"  bordo agganciato: {ne.IsLinked} ({ne.Anchors?.Count(x => x.El >= 0)} punti agganciati)");
        // il primo marciapiede si allarga a 3 m: bordo e ciclabile devono seguirlo
        double WallDist()
        {
            var an = ne.Anchors!; var m = new V2((an[0].X + an[1].X) / 2, (an[0].Y + an[1].Y) / 2);
            var q = PL.Project(ne.Points, m).Point;
            return PL.Project(walks[0].Inner, q).Distance;
        }
        double before = WallDist();
        ed.Select(walks[0]); ed.SetSelectedWidth(3.0);
        double after = WallDist();
        Console.WriteLine($"  marciapiede 1 da 1,5 a 3 m: distanza del bordo dal muro {before:0.00} → {after:0.00} m; ciclabile {bike.Metrics.Area:0.0} m², errore {bike.Error}");
        bool ok = true;
        foreach (var w in walks)
        {
            double ov = Project.ToNts(w.Polygon)!.Buffer(0).Intersection(Project.ToNts(bike.Polygon)!.Buffer(0)).Area;
            Console.WriteLine($"  sovrapposizione {w.Name} / ciclabile dopo la modifica: {ov:0.000} m²");
            if (ov > 0.05) { var gi = Project.ToNts(w.Polygon)!.Buffer(0).Intersection(Project.ToNts(bike.Polygon)!.Buffer(0)); Console.WriteLine($"    buchi {Project.ToNts(w.Polygon)!.NumInteriorRings}"); Console.WriteLine($"    geometria {gi.AsText()[..Math.Min(400, gi.AsText().Length)]}"); Console.WriteLine($"    zona {gi.EnvelopeInternal}, marciapiede valido {new NetTopologySuite.Operation.Valid.IsValidOp(Project.ToNts(w.Polygon)).ValidationError}, guida {w.Guide.Count} pt, esterno {w.Outer.Count} pt chiuso {V2.Dist(w.Outer[0], w.Outer[^1]):0.000}, interno0 {w.Inner[0]} esterno0 {w.Outer[0]}, ciclabile valida {Project.ToNts(bike.Polygon)!.IsValid}"); }
            ok &= ov < 0.05;
        }
        double Dev()
        {
            // ogni punto del bordo deve stare sul bordo esterno di un marciapiede o sul tratto dritto che li unisce
            var an = ne.Anchors!; var gapA = new V2(an[1].X, an[1].Y); var gapB = new V2(an[2].X, an[2].Y);
            var gap = new List<V2> { gapA, gapB };
            return ne.Points.Max(q => Math.Min(Math.Min(PL.Project(walks[0].Outer, q).Distance, PL.Project(walks[1].Outer, q).Distance), PL.Project(gap, q).Distance));
        }
        Console.WriteLine($"  scostamento massimo del bordo dai marciapiedi: {Dev():0.000} m");
        ok &= Dev() < 0.02;
        ed.Select(walks[1]); ed.SetSelectedWidth(0.8);
        Console.WriteLine($"  marciapiede 2 da 1,5 a 0,8 m: scostamento massimo {Dev():0.000} m, ciclabile errore {bike.Error}");
        ok &= Dev() < 0.02;
        ProjectFile.Save(Path.Combine(a[1], "anello_3m.ciclo"), ed.Project, data);
        // riapertura: i punti agganciati restano
        var back = ProjectFile.ToProject(ProjectFile.Read(Path.Combine(a[1], "anello_3m.ciclo")), data.Origin);
        Console.WriteLine($"  riaperto: bordo agganciato {back.Edges[^1].IsLinked}");
        ok &= bike.Error == null && Math.Abs(after - before - 1.5) < 0.1 && back.Edges[^1].IsLinked;
        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FALLITO");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// CicloDraft --selftest-disegno dati.gpkg punti.json cartella: il nuovo flusso senza bordi. Ogni elemento si disegna
    /// dalla sua linea: marciapiedi dal bordo lungo le facciate, carreggiata dall'interasse, ciclabile dal bordo
    /// agganciato al marciapiede (che poi si allarga).
    /// </summary>
    public static int RunDraw(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var data = GpkgReader.Load(a[0]);
        var ed = new Editor(); ed.SetData(data);
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(a[1]));
        List<V2> Pts(string k) => doc.RootElement.GetProperty(k).EnumerateArray().Select(x => new V2(x[0].GetDouble(), x[1].GetDouble()) - data.Origin).ToList();
        double tol = 0.3;
        bool ok = true;
        Element Draw(ElementType t, bool centered, IEnumerable<V2> clicks, double w)
        {
            ed.StartDrawElement(t, centered);
            foreach (var c in clicks) ed.TraceClick(c, tol);
            ed.TraceFinish();
            ed.SetDraftWidth(w);
            ed.Commit();
            var e = ed.Selected!;
            Console.WriteLine($"  {e.Name}: {(e.Centered ? "interasse" : "bordo")}, {e.Metrics.Length:0.00} m, {e.Metrics.Area:0.0} m², largh. {e.Metrics.WMin:0.00}–{e.Metrics.WMax:0.00} {e.Error}");
            ok &= e.Error == null;
            return e;
        }
        var sx = Draw(ElementType.Marciapiede, false, Pts("left"), 1.5);
        var dx = Draw(ElementType.Marciapiede, false, Pts("right"), 1.5);
        var axis = data.RingsWithRole(LayerRole.Sfondo).Select(x => x.ring).Where(r => !r.Closed).OrderByDescending(r => PL.Length(r.Pts)).First().Pts;
        double AL = PL.Length(axis);
        var car = Draw(ElementType.Carreggiata, true, Enumerable.Range(0, 11).Select(k => PL.PointAt(axis, 20 + (AL - 40) * k / 10.0)), 6.0);
        ok &= Math.Abs(car.Metrics.WMean - 6.0) < 0.05 && Project.ToNts(car.Polygon)!.IsValid;
        // ciclabile: bordo agganciato al marciapiede sinistro (due clic sul suo bordo esterno, segue il contorno)
        double L1 = PL.Length(sx.Outer);
        var bike = Draw(ElementType.CorsiaCiclabile, false, new[] { PL.PointAt(sx.Outer, 30), PL.PointAt(sx.Outer, L1 - 30) }, 1.5);
        double Ov(Element x, Element y) => Project.ToNts(x.Polygon)!.Buffer(0).Intersection(Project.ToNts(y.Polygon)!.Buffer(0)).Area;
        Console.WriteLine($"  sovrapposizioni: marciapiede/ciclabile {Ov(sx, bike):0.000} m², ciclabile/carreggiata {Ov(bike, car):0.00} m², marciapiede dx/carreggiata {Ov(dx, car):0.00} m²");
        ok &= Ov(sx, bike) < 0.05;
        var lineBike = ed.Project.FindEdge(bike.Base.Id)!;
        Console.WriteLine($"  linea della ciclabile: proprietario {lineBike.Owner}, agganciata {lineBike.IsLinked}; linee nel progetto {ed.Project.Edges.Count}");
        ok &= lineBike.Owner == bike.Id && lineBike.IsLinked;
        double Gap() { var m = PL.PointAt(bike.Inner, PL.Length(bike.Inner) / 2); return PL.Project(sx.Inner, m).Distance; }
        double g0 = Gap();
        ed.Select(sx); ed.SetSelectedWidth(2.5);
        double g1 = Gap();
        Console.WriteLine($"  marciapiede sx da 1,5 a 2,5 m: la ciclabile passa da {g0:0.00} a {g1:0.00} m dal muro; sovrapposizione {Ov(sx, bike):0.000} m²");
        ok &= Math.Abs(g1 - g0 - 1.0) < 0.05 && Ov(sx, bike) < 0.05;
        // poligono: un'aiuola rettangolare 6 × 3 m con un lato appoggiato al marciapiede destro (agganciato)
        {
            double Ld = PL.Length(dx.Outer);
            var p0 = PL.PointAt(dx.Outer, Ld / 2); var tg = PL.TangentAt(dx.Outer, Ld / 2);
            var p1 = PL.PointAt(dx.Outer, Ld / 2 + 6);
            var nn = tg.LeftNormal * -dx.Side;   // verso la strada, lontano dal marciapiede
            if (PL.PointInRing(dx.Polygon, p0 + nn * 0.5)) nn = nn * -1;
            ed.StartDrawElement(ElementType.Aiuola, false, polygon: true);
            foreach (var c in new[] { p0, p1, p1 + nn * 3, p0 + nn * 3 }) ed.TraceClick(c, tol);
            ed.TraceFinish(); ed.Commit();
            var aiu = ed.Selected!;
            Console.WriteLine($"  {aiu.Name}: poligono {aiu.Mode == ElementMode.Polygon}, {aiu.Metrics.Area:0.00} m², perimetro {aiu.Metrics.Length:0.00} m {aiu.Error}");
            ok &= aiu.Mode == ElementMode.Polygon && Math.Abs(aiu.Metrics.Area - 18) < 1.0 && aiu.Error == null;
            double ovA = Ov(dx, aiu);
            ed.Select(dx); ed.SetSelectedWidth(2.5);
            Console.WriteLine($"  marciapiede dx allargato: l'aiuola lo segue {ed.Project.FindEdge(aiu.Base.Id)!.IsLinked}, sovrapposizione {Ov(dx, aiu):0.000} m² (prima {ovA:0.000})");
            ok &= Ov(dx, aiu) < 0.1;
        }
        // ridisegno della linea della carreggiata (più corta)
        ed.Select(car); ed.RedrawSelectedLine();
        foreach (var c in Enumerable.Range(0, 6).Select(k => PL.PointAt(axis, 40 + (AL - 80) * k / 5.0))) ed.TraceClick(c, tol);
        ed.TraceFinish();
        Console.WriteLine($"  carreggiata ridisegnata: {car.Metrics.Length:0.00} m (attesi {AL - 80:0.00})");
        ok &= Math.Abs(car.Metrics.Length - (AL - 80)) < 1;
        // eliminazione: la linea se ne va con l'elemento
        ed.Select(bike); ed.DeleteSelected();
        ok &= ed.Project.Edges.All(e => e.Owner != bike.Id);
        // modifica della linea della carreggiata: sposta, elimina, inserisci, prosegui
        {
            ed.Select(car); ed.StartEditLine();
            var an = ed.EditEdge!.Anchors!;
            int Keys() => an.Count(x => !x.Auto);
            var ki = Enumerable.Range(0, an.Count).Where(i => !an[i].Auto).ToList();
            int n0 = Keys(); double L0 = car.Metrics.Length;
            var mid = new V2(an[ki[2]].X, an[ki[2]].Y);
            ed.SelectKeyPoint(ki[2]); ed.MoveKeyPoint(ki[2], mid + new V2(0, 3), 0.01, true);
            bool moved = an.Any(x => !x.Auto && V2.Dist(new V2(x.X, x.Y), mid + new V2(0, 3)) < 0.01);
            ed.SelectKeyPoint(Enumerable.Range(0, an.Count).Where(i => !an[i].Auto).ElementAt(1)); ed.DeleteKeyPoint();
            bool deleted = Keys() == n0 - 1;
            var e2 = ed.EditEdge!.Points;
            bool inserted = ed.InsertKeyPoint(PL.PointAt(e2, PL.Length(e2) * 0.5), 0.05) && Keys() == n0;
            var last = new V2(an[^1].X, an[^1].Y); var dir = PL.TangentAt(e2, PL.Length(e2));
            ed.StartExtend(1); ed.ExtendClick(last + dir * 15, 0.01); ed.StartExtend(0);
            ed.FinishEditLine();
            Console.WriteLine($"  modifica linea carreggiata: spostato {moved}, eliminato {deleted}, inserito {inserted}, punti chiave {n0} → {Keys()} (con i vertici sul muro {an.Count}), lunghezza {L0:0.00} → {car.Metrics.Length:0.00} m, errore {car.Error}");
            ok &= moved && deleted && inserted && Keys() == n0 + 1 && car.Metrics.Length > L0 + 10 && car.Error == null && ed.Mode == Mode.Idle;
        }
        // modifica della linea appena disegnata, prima di confermare l'elemento
        {
            var pa = PL.PointAt(axis, 30); var pb = PL.PointAt(axis, 60);
            ed.StartDrawElement(ElementType.Separatore, true);
            ed.TraceClick(pa, tol); ed.TraceClick(pb, tol); ed.TraceFinish();
            ed.StartEditLine(); ed.StartExtend(1); ed.ExtendClick(PL.PointAt(axis, 90), 0.01); ed.FinishEditLine();
            bool backToPreview = ed.Mode == Mode.SetWidth;
            ed.Commit();
            var sep = ed.Selected!;
            Console.WriteLine($"  striscia allungata prima di confermarla: torna all'anteprima {backToPreview}, {sep.Metrics.Length:0.00} m (attesi circa 60)");
            ok &= backToPreview && Math.Abs(sep.Metrics.Length - 60) < 2;
        }
        // strisce di mezzeria: tre strisce continue sulla carreggiata (quattro corsie)
        {
            int auto0 = car.EffectiveDividers;
            ed.Select(car); ed.SetSelectedDividers(3, DividerStyle.Continua);
            var dl = car.DividerLines();
            var m = PL.PointAt(car.Guide, PL.Length(car.Guide) / 2);
            var d = dl.Select(l => PL.Project(l, m).Distance).OrderBy(x => x).ToList();
            Console.WriteLine($"  mezzerie carreggiata: automatiche {auto0}, ora {dl.Count} continue; distanze dall'interasse {string.Join(" / ", d.Select(x => x.ToString("0.00")))} m (attese 0 e 1,5)");
            ok &= auto0 == 1 && dl.Count == 3 && d[0] < 0.05 && Math.Abs(d[1] - 1.5) < 0.05 && Math.Abs(d[2] - 1.5) < 0.05;
        }
        // «lungo come»: il primo punto sul bordo del marciapiede sinistro, poi la linea prende tutta la sua lunghezza;
        // l'elemento si cambia con l'anteprima già visibile
        {
            ed.StartDrawElement(ElementType.Separatore, false);
            ed.TraceClick(PL.PointAt(sx.Outer, PL.Length(sx.Outer) / 3), tol);
            var fh = ed.FirstPointHost;
            ed.MatchFirstHostLength();
            bool inPreview = ed.Mode == Mode.SetWidth;
            ed.SetDraftType(ElementType.CorsiaCiclabile);
            double wAfter = ed.DraftWidth;
            ed.Commit();
            var lc = ed.Selected!;
            Console.WriteLine($"  lungo come {fh?.e.Name}: {lc.Name} {lc.Metrics.Length:0.00} m (marciapiede {sx.Metrics.Length:0.00} m), larghezza dopo il cambio {wAfter:0.00}, errore {lc.Error}");
            ok &= fh?.e == sx && inPreview && lc.Type == ElementType.CorsiaCiclabile && Math.Abs(wAfter - 1.5) < 1e-6
                  && Math.Abs(lc.Metrics.Length - PL.Length(sx.Outer)) < 1.0 && lc.Error == null;
            ed.Select(lc); ed.DeleteSelected();
        }
        // cambio di tipo: il marciapiede destro diventa una pista ciclabile, con la stessa forma
        double areaDx = dx.Metrics.Area;
        ed.Select(dx); ed.SetSelectedType(ElementType.Ciclabile);
        Console.WriteLine($"  cambio tipo: {dx.Name} ({Element.TypeLabel(dx.Type)}), superficie {areaDx:0.0} → {dx.Metrics.Area:0.0} m²");
        ok &= dx.Type == ElementType.Ciclabile && dx.Name.StartsWith("Pista ciclabile") && Math.Abs(areaDx - dx.Metrics.Area) < 0.01;
        Directory.CreateDirectory(a[2]);
        ProjectFile.Save(Path.Combine(a[2], "disegno.ciclo"), ed.Project, data);
        var back = ProjectFile.ToProject(ProjectFile.Read(Path.Combine(a[2], "disegno.ciclo")), data.Origin);
        var car2 = back.Elements.First(e => e.Type == ElementType.Carreggiata);
        ok &= car2.Dividers == 3 && car2.DividerStyle == DividerStyle.Continua;
        Console.WriteLine($"  riaperto: mezzerie {car2.Dividers} {car2.DividerStyle}");
        var r3 = Export3D.Write(Path.Combine(a[2], "disegno.glb"), ed.Project, data, new Export3DOptions());
        Console.WriteLine($"  GLB con le mezzerie: {r3.Triangles} triangoli");
        Console.WriteLine($"  riaperto: {back.Elements.Count} elementi, carreggiata a interasse {car2.Centered}, linea dell'elemento {back.FindEdge(car2.Base.Id)?.Owner == car2.Id}");
        ok &= car2.Centered && back.FindEdge(car2.Base.Id)?.Owner == car2.Id && back.Elements.All(e => e.Error == null);
        DxfWriter.Write(Path.Combine(a[2], "disegno.dxf"), ed.Project, data, true, true);
        int segn = File.ReadAllText(Path.Combine(a[2], "disegno.dxf")).Split("CD_SEGNALETICA").Length - 1;
        Console.WriteLine($"  DXF: {segn} riferimenti al layer CD_SEGNALETICA");
        ok &= segn >= 4;
        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FALLITO");
        return ok ? 0 : 1;
    }

    public static int RunCurve(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var data = GpkgReader.Load(a[0]);
        var ed = new Editor(); ed.SetData(data); ed.Project.EnsureLegacyEdges();
        CurveScenario(ed, data, a[1]);
        Console.WriteLine($"{"Elemento",-22}{"Lungh.",9}{"Sup.",10}{"Lmin",7}{"Lmax",7}{"Lmed",7}  errore");
        foreach (var e in ed.Project.Elements)
            Console.WriteLine($"{e.Name,-22}{e.Metrics.Length,9:0.00}{e.Metrics.Area,10:0.00}{e.Metrics.WMin,7:0.00}{e.Metrics.WMax,7:0.00}{e.Metrics.WMean,7:0.00}  {e.Error}");
        // controllo sovrapposizioni tra elementi
        var polys = ed.Project.Elements.Select(e => Project.ToNts(e.Polygon)).ToList();
        for (int i = 0; i < polys.Count; i++)
        {
            if (polys[i] is { } pi && !pi.IsValid) Console.WriteLine($"  poligono non valido: {ed.Project.Elements[i].Name}");
            for (int j = i + 1; j < polys.Count; j++)
                if (polys[i] is { } p1 && polys[j] is { } p2)
                {
                    double ov = p1.Buffer(0).Intersection(p2.Buffer(0)).Area;
                    if (ov > 0.05) Console.WriteLine($"  sovrapposizione {ed.Project.Elements[i].Name} / {ed.Project.Elements[j].Name}: {ov:0.00} m²");
                }
        }
        string outDir = a.Length > 2 ? a[2] : ".";
        DxfWriter.Write(Path.Combine(outDir, "curve.dxf"), ed.Project, data, true, true);
        var r3 = Export3D.Write(Path.Combine(outDir, "curve.glb"), ed.Project, data, new Export3DOptions());
        Console.WriteLine($"GLB: {r3.Triangles} triangoli, {r3.Buildings} edifici (altezza predefinita), {r3.Bytes / 1024} KB");
        ProjectFile.Save(Path.Combine(outDir, "curve.ciclo"), ed.Project, data);
        // vista da terra (foto grigia): controlla materiali e segnaletica
        var e0 = ed.Project.Elements.Last();
        var g0 = e0.Mode == ElementMode.Fill ? e0.MidLine() : e0.Guide;
        var p0 = PL.PointAt(g0, 15); var d0 = PL.TangentAt(g0, 15);
        var vc = new PhotoCamera { X = p0.X - d0.X * 8, Y = p0.Y - d0.Y * 8, Z = 1.6, Yaw = Math.Atan2(d0.Y, d0.X) * 180 / Math.PI, Pitch = -8, F = 800, W = 1600, H = 900 };
        var grey = new Rgba(1600, 900); for (int i = 0; i < grey.Px.Length; i += 4) { grey.Px[i] = 150; grey.Px[i + 1] = 152; grey.Px[i + 2] = 156; grey.Px[i + 3] = 255; }
        PhotoRender.Render(vc, grey, null, ed.Project, data, new PhotoRenderOptions { ShadowTransfer = 0 }).Composite.SavePng(Path.Combine(outDir, "curve_vista.png"));
        bool ok = ed.Project.Elements.Count >= 4 && ed.Project.Elements.All(e => e.Error == null);
        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FALLITO");
        return ok ? 0 : 1;
    }

    /// <summary>CicloDraft --clip-test uscita.gpkg sorgente1 [sorgente2 ...] : ritaglia sull'estensione dell'ultimo .shp (o del primo livello).</summary>
    public static int RunClip(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var layers = a.Skip(1).SelectMany(SourceData.Open).ToList();
        Console.WriteLine($"Aperti {layers.Count} livelli in {sw.ElapsedMilliseconds} ms");
        var shp = layers.LastOrDefault(l => l.Kind == SourceKind.Shp) ?? layers[0];
        var b = shp.Extent;
        Console.WriteLine($"Area: estensione di {shp.Name}: {b.MinX:0.0} {b.MinY:0.0} – {b.MaxX:0.0} {b.MaxY:0.0}");
        var want = (Environment.GetEnvironmentVariable("CD_LAYERS") ?? "EDIFC_CR_EDF_IS,AC_PED,AC_VEI,AC_CIC,EL_STR,PT_QUO").Split(',');
        var sel = layers.Where(l => l.Kind == SourceKind.Shp || want.Contains(l.Table)).ToList();
        var opt = new ClipOptions
        {
            Area = new List<V2> { new(b.MinX, b.MinY), new(b.MaxX, b.MinY), new(b.MaxX, b.MaxY), new(b.MinX, b.MaxY) },
            Margin = 20, Cut = Environment.GetEnvironmentVariable("CD_CUT") == "1", KeepBuildingsWhole = true,
            DefaultSrs = layers.FirstOrDefault(l => l.Srs is { Id: > 0 })?.Srs
        };
        sw.Restart();
        foreach (var l in sel) Console.WriteLine($"  {l.Name}: {GpkgClipper.CountInside(l, opt, default)} di {l.Count} nell'area  (indice: {(l.RtreeTable != null ? "sì" : "no")})");
        Console.WriteLine($"Conteggio in {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var res = GpkgClipper.Write(a[0], sel, opt, new Progress<string>(m => { }), default);
        Console.WriteLine($"Scritto {res.Path}: {res.Bytes / 1024.0:0} KB in {sw.ElapsedMilliseconds} ms");
        foreach (var r in res.Layers) Console.WriteLine($"  {r.OutName}: {r.Features}");
        var d = GpkgReader.Load(a[0]);
        Console.WriteLine($"Riaperto: CRS {d.Crs}; " + string.Join(", ", d.Layers.Select(l => $"{l.Name}={(LayerRole)l.Role}")));
        return 0;
    }

    /// <summary>
    /// Bordi della strada ricavati in automatico da un asse: dai punti dell'asse si cercano, a destra e a sinistra,
    /// le facciate più vicine (solo per le prove sui dati reali).
    /// </summary>
    public static (List<V2> left, List<V2> right) EdgesFromAxis(GpkgData data, List<V2> axis, double maxDist = 20)
    {
        var segs = new List<(V2 a, V2 b)>();
        var ab = BBox.Of(axis);
        foreach (var (_, _, r) in data.RingsWithRole(LayerRole.Muri))
        {
            if (!r.Box.Contains(new V2((ab.MinX + ab.MaxX) / 2, (ab.MinY + ab.MaxY) / 2), Math.Max(ab.MaxX - ab.MinX, ab.MaxY - ab.MinY) / 2 + maxDist)) continue;
            for (int i = 0; i < r.Pts.Count; i++)
            {
                int j = (i + 1) % r.Pts.Count;
                if (!r.Closed && j == 0) break;
                segs.Add((r.Pts[i], r.Pts[j]));
            }
        }
        List<V2> Side(int sgn)
        {
            var hits = new List<(double s, double d)>();
            double L = PL.Length(axis);
            for (double s = 0; s <= L; s += 1.0)
            {
                var p = PL.PointAt(axis, s); var n = PL.TangentAt(axis, s).LeftNormal * sgn;
                var q = p + n * maxDist;
                double best = double.MaxValue;
                foreach (var (a, b) in segs)
                    if (PL.SegIntersect(p, q, a, b, out var x)) best = Math.Min(best, V2.Dist(p, x));
                if (best < maxDist) hits.Add((s, best));
            }
            if (hits.Count == 0) return new List<V2>();
            // si scartano i vicoli e i cortili (distanze molto diverse dalla mediana) e si tiene il fronte continuo
            var med = hits.Select(h => h.d).OrderBy(d => d).ElementAt(hits.Count / 2);
            var pts = new List<V2>();
            for (double s = 0; s <= L; s += 1.0)
            {
                var near = hits.Where(h => Math.Abs(h.s - s) <= 6 && Math.Abs(h.d - med) < 2.0).ToList();
                double d = near.Count > 0 ? near.Min(h => h.d) : med;
                pts.Add(PL.PointAt(axis, s) + PL.TangentAt(axis, s).LeftNormal * (sgn * d));
            }
            return PL.Simplify(PL.Dedup(pts, 0.05), 0.15);
        }
        return (Side(1), Side(-1));
    }

    /// <summary>CicloDraft --export3d-test ritaglio.gpkg uscita.glb E N : progetto di prova sulla via con asse più vicino a (E,N).</summary>
    public static int RunExport3D(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var data = GpkgReader.Load(a[0]);
        var ed = new Editor(); ed.SetData(data); ed.Project.EnsureLegacyEdges();
        var target = new V2(double.Parse(a[2]), double.Parse(a[3])) - data.Origin;
        // asse stradale più vicino al punto indicato
        var axis = data.Layers.Where(l => l.Name.Contains("EL_STR", StringComparison.OrdinalIgnoreCase)).SelectMany(l => l.Rings)
            .Where(r => !r.Closed && PL.Length(r.Pts) > 60).OrderBy(r => PL.Project(r.Pts, target).Distance).First().Pts;
        Console.WriteLine($"Asse: {PL.Length(axis):0.0} m, {axis.Count} vertici");
        var (left, right) = EdgesFromAxis(data, axis);
        ed.Project.Edges[0].Points = left; ed.Project.Edges[1].Points = right;
        ed.Project.Regenerate();
        Console.WriteLine($"Bordi automatici: sinistro {PL.Length(left):0.0} m, destro {PL.Length(right):0.0} m");
        double tol = 0.3;
        Element Add(ElementType t, ElementMode m, V2 pick, double w, Func<V2>? other = null)
        {
            ed.StartNewElement(t, m);
            ed.ClickPick(pick, tol);
            ed.WholeEdge();
            ed.ConfirmRange();
            if (m == ElementMode.Offset) ed.SetDraftWidth(w);
            else ed.ClickPick(other!(), tol);
            ed.Commit();
            var e = ed.Selected!;
            Console.WriteLine($"  {e.Name}: lato {e.Side}, {e.Metrics.Area:0.0} m², largh. {e.Metrics.WMin:0.00}–{e.Metrics.WMax:0.00} m {e.Error}");
            return e;
        }
        var sx = Add(ElementType.Marciapiede, ElementMode.Offset, PL.PointAt(left, 3), 1.8);
        var bike = Add(ElementType.Ciclabile, ElementMode.Offset, PL.PointAt(sx.Outer, 3), 2.5);
        var dx = Add(ElementType.Marciapiede, ElementMode.Offset, PL.PointAt(right, 3), 1.6);
        var park = Add(ElementType.Parcheggio, ElementMode.Offset, PL.PointAt(dx.Outer, 3), 2.0);
        Add(ElementType.Carreggiata, ElementMode.Fill, PL.PointAt(park.Outer, 3), 0, () => PL.PointAt(bike.Outer, 3));
        ProjectFile.Save(Path.ChangeExtension(a[1], ".ciclo"), ed.Project, data);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = Export3D.Write(a[1], ed.Project, data, new Export3DOptions());
        Console.WriteLine($"GLB: {res.Bytes / 1048576.0:0.00} MB, {res.Triangles} triangoli, {res.Elements} elementi, {res.Buildings} volumi edificio, {res.Trees} alberi, {sw.ElapsedMilliseconds} ms");
        return 0;
    }

    /// <summary>CicloDraft --photo-test dati.gpkg progetto.ciclo foto.png punti.json cartella_uscita</summary>
    public static int RunPhoto(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var dto = ProjectFile.Read(a[1]);
        var data = GpkgReader.Load(a[0], dto.Origin is { Length: 2 } o ? new V2(o[0], o[1]) : null);
        var project = ProjectFile.ToProject(dto, data.Origin);
        var photo = Rgba.Load(a[2]);
        var pf = PhotoInsertFile.Load(a[3]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = CameraSolver.Solve(pf.Pairs, data.Origin, photo.W, photo.H, pf.CameraHeightHint);
        if (res == null) { Console.WriteLine("Punti insufficienti"); return 1; }
        var c = res.Camera;
        Console.WriteLine($"Fotocamera in {sw.ElapsedMilliseconds} ms: E {c.X + data.Origin.X:0.00} N {c.Y + data.Origin.Y:0.00} quota {c.Z:0.00} m, " +
                          $"direzione {c.Yaw:0.0}°, inclinazione {c.Pitch:0.0}°, rollio {c.Roll:0.0}°, focale {c.F:0} px (campo {c.HFov:0}°), errore medio {res.Rms:0.0} px {res.Warning}");
        for (int i = 0; i < pf.Pairs.Count; i++) Console.WriteLine($"  punto {i + 1} ({pf.Pairs[i].Kind}, {pf.Pairs[i].Note}): {res.PairResiduals[i]:0.0} px");
        Rgba? mask = pf.Mask != null && File.Exists(Path.Combine(Path.GetDirectoryName(a[3])!, pf.Mask)) ? Rgba.Load(Path.Combine(Path.GetDirectoryName(a[3])!, pf.Mask)) : null;
        Directory.CreateDirectory(a[4]);
        if (a.Length > 5 && a[5] == "--auto" && CarRemoval.FindModels() is { } md)
        {
            sw.Restart();
            var cr = CarRemoval.Run(photo, c, project, md, new Progress<string>(Console.WriteLine));
            foreach (var d in cr.Detections) Console.WriteLine($"  {d.Kind,-8} {d.Score:0.00} [{d.X0:0},{d.Y0:0},{d.X1:0},{d.Y1:0}] appoggio nel progetto {d.Inside}/{d.Contacts} -> {(d.Remove ? "RIMOSSA" : "resta")}");
            Console.WriteLine($"Rimozione auto in {sw.ElapsedMilliseconds} ms");
            CarRemoval.Overlay(photo, cr).SavePng(Path.Combine(a[4], "rilevamento.jpg"));
            cr.Clean.SavePng(Path.Combine(a[4], "senza_auto.png"));
            photo = cr.Clean;
            mask ??= new Rgba(photo.W, photo.H);
            for (int i = 0; i < photo.W * photo.H; i++) { if (cr.Front[i]) mask.Px[4 * i + 3] = 255; if (cr.Removed[i]) mask.Px[4 * i + 3] = 0; }
        }
        sw.Restart();
        var r = PhotoRender.Render(c, photo, mask, project, data, new PhotoRenderOptions());
        Console.WriteLine($"Fotoinserimento in {sw.ElapsedMilliseconds} ms");
        Directory.CreateDirectory(a[4]);
        r.Composite.SavePng(Path.Combine(a[4], "fotoinserimento.png"));
        r.Layer.SavePng(Path.Combine(a[4], "progetto_livello.png"));
        pf.Camera = PhotoCamera.FromArray(new[] { c.X + data.Origin.X, c.Y + data.Origin.Y, c.Z, c.Yaw, c.Pitch, c.Roll, c.F }, c.W, c.H);
        pf.Save(Path.Combine(a[4], "punti_risolti.json"));
        return 0;
    }
}
