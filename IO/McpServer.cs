using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CicloDraft.Geometry;
using CicloDraft.Model;

namespace CicloDraft.IO;

/// <summary>
/// Server MCP (Model Context Protocol) su stdin/stdout: «CicloDraft --mcp».
/// Un assistente AI può aprire un progetto, leggere elementi e misure, modificarli, disegnarne di nuovi
/// ed esportare DXF, CSV e 3D, con lo stesso codice del programma.
/// I messaggi sono JSON-RPC 2.0, uno per riga. Tutto ciò che non è protocollo va su stderr.
/// Le coordinate scambiate sono quelle del sistema di riferimento del GeoPackage (assolute, in metri).
/// </summary>
public static class McpServer
{
    const string Version = "1.0.0";
    static readonly Editor Ed = new();
    static string? _projectPath;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run()
    {
        CultureInfo.CurrentCulture = Inv;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        // qualunque scrittura «normale» su Console finisce su stderr, per non sporcare il protocollo
        Console.SetOut(Console.Error);
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch { Send(stdout, Error(null, -32700, "JSON non valido")); continue; }
            if (msg is JsonArray batch)
            {
                var replies = new JsonArray();
                foreach (var m in batch) if (Handle(m) is { } r) replies.Add(r);
                if (replies.Count > 0) Send(stdout, replies);
            }
            else if (Handle(msg) is { } reply) Send(stdout, reply);
        }
        return 0;
    }

    static void Send(StreamWriter w, JsonNode n) => w.WriteLine(n.ToJsonString());

    static JsonNode? Handle(JsonNode? msg)
    {
        if (msg is not JsonObject o) return Error(null, -32600, "Richiesta non valida");
        var id = o["id"]?.DeepClone();
        string method = o["method"]?.GetValue<string>() ?? "";
        bool isNotification = id == null;
        try
        {
            switch (method)
            {
                case "initialize":
                {
                    string pv = o["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = pv,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "ciclodraft", ["title"] = "CicloDraft", ["version"] = Version },
                        ["instructions"] =
                            "CicloDraft disegna piste ciclabili, marciapiedi e carreggiate su un GeoPackage. " +
                            "Prima apri un progetto (.ciclo) con apri_progetto, oppure un GeoPackage con apri_geopackage. " +
                            "Le coordinate sono in metri nel sistema di riferimento del GeoPackage. " +
                            "Le modifiche restano in memoria finché non chiami salva_progetto."
                    });
                }
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = ToolList() });
                case "tools/call":
                {
                    string name = o["params"]?["name"]?.GetValue<string>() ?? "";
                    var args = o["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    JsonObject content;
                    try { content = ToolResult(CallTool(name, args), false); }
                    catch (ToolError te) { content = ToolResult(te.Message, true); }
                    catch (Exception ex) { content = ToolResult("Errore: " + ex.Message, true); }
                    return Result(id, content);
                }
                default:
                    return isNotification ? null : Error(id, -32601, "Metodo non disponibile: " + method);
            }
        }
        catch (Exception ex) { return isNotification ? null : Error(id, -32603, ex.Message); }
    }

    static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    static JsonObject ToolResult(object payload, bool isError)
    {
        string text = payload is string s ? s : JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
    }

    sealed class ToolError(string message) : Exception(message);

    // ───────────────────────── strumenti ─────────────────────────

    static JsonObject Prop(string type, string description, JsonArray? enumValues = null)
    {
        var p = new JsonObject { ["type"] = type, ["description"] = description };
        if (enumValues != null) p["enum"] = enumValues;
        return p;
    }

    static JsonObject Tool(string name, string title, string description, JsonObject props, string[] required, bool readOnly = false, bool destructive = false)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return new JsonObject
        {
            ["name"] = name, ["title"] = title, ["description"] = description, ["inputSchema"] = schema,
            ["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = destructive }
        };
    }

    static JsonArray TypeEnum() => new(Enum.GetNames<ElementType>().Select(n => (JsonNode)n).ToArray());

    static JsonArray ToolList() => new(
        Tool("apri_progetto", "Apri progetto", "Apre un progetto CicloDraft (.ciclo) con il suo GeoPackage. Restituisce un riepilogo.",
            new JsonObject
            {
                ["percorso"] = Prop("string", "Percorso del file .ciclo"),
                ["geopackage"] = Prop("string", "Percorso del GeoPackage, se non è dove lo indica il progetto (facoltativo)")
            }, new[] { "percorso" }),
        Tool("apri_geopackage", "Apri GeoPackage", "Inizia un progetto nuovo e vuoto su un GeoPackage (ruoli dei livelli proposti dal nome).",
            new JsonObject { ["percorso"] = Prop("string", "Percorso del file .gpkg") }, new[] { "percorso" }),
        Tool("elenco_livelli", "Livelli", "Livelli del GeoPackage aperto: nome, tipo di geometria, numero di elementi, ruolo e visibilità.",
            new JsonObject(), Array.Empty<string>(), readOnly: true),
        Tool("elenco_elementi", "Elementi", "Tutti gli elementi del progetto con tipo, modalità, larghezza, estensione, superficie e larghezze minima/massima/media.",
            new JsonObject(), Array.Empty<string>(), readOnly: true),
        Tool("dettaglio_elemento", "Dettaglio elemento", "Tutti i dati di un elemento: misure, linea (inizio e fine), variatori di sezione, strisce, stalli del parcheggio.",
            new JsonObject { ["id"] = Prop("integer", "Id dell'elemento (da elenco_elementi)") }, new[] { "id" }, readOnly: true),
        Tool("totali", "Totali per tipo", "Superficie e lunghezza totali per tipo di elemento.",
            new JsonObject(), Array.Empty<string>(), readOnly: true),
        Tool("disegna_elemento", "Disegna elemento",
            "Disegna un nuovo elemento da una lista di punti [E, N]. modalita: «bordo» (l'elemento sta da un lato della linea), «interasse» (metà per parte) o «poligono» (contorno chiuso, almeno 3 punti).",
            new JsonObject
            {
                ["tipo"] = Prop("string", "Tipo di elemento", TypeEnum()),
                ["modalita"] = Prop("string", "Come interpretare i punti", new JsonArray("bordo", "interasse", "poligono")),
                ["punti"] = new JsonObject { ["type"] = "array", ["description"] = "Punti [E, N] in metri, nel sistema di riferimento del GeoPackage", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 } },
                ["larghezza"] = Prop("number", "Larghezza in metri (facoltativa: predefinita per il tipo)"),
                ["lato"] = Prop("string", "Solo per «bordo»: lato dell'elemento guardando la linea dal primo all'ultimo punto (facoltativo: scelto in automatico, lontano dagli edifici)", new JsonArray("sinistra", "destra")),
                ["aggancia"] = Prop("boolean", "Aggancia i punti a muri ed elementi vicini (predefinito: no)"),
                ["nome"] = Prop("string", "Nome (facoltativo)")
            }, new[] { "tipo", "modalita", "punti" }),
        Tool("modifica_elemento", "Modifica elemento", "Cambia nome, tipo, larghezza, lato, strisce di mezzeria o stalli di un elemento. Indica solo i campi da cambiare.",
            new JsonObject
            {
                ["id"] = Prop("integer", "Id dell'elemento"),
                ["nome"] = Prop("string", "Nuovo nome"),
                ["tipo"] = Prop("string", "Nuovo tipo", TypeEnum()),
                ["larghezza"] = Prop("number", "Larghezza in metri"),
                ["inverti_lato"] = Prop("boolean", "Sposta l'elemento dall'altra parte della sua linea (solo elementi disegnati dal bordo)"),
                ["strisce"] = Prop("integer", "Numero di strisce di mezzeria; -1 = automatico"),
                ["stile_strisce"] = Prop("string", "Tipo di strisce", new JsonArray("Tratteggiata", "Continua", "DoppiaContinua")),
                ["linee_stalli"] = Prop("boolean", "Parcheggio: mostra le linee degli stalli"),
                ["altezza"] = Prop("number", "Marciapiede o aiuola: altezza sopra la strada (m)"),
                ["lunghezza_stalli"] = Prop("number", "Parcheggio: lunghezza dello stallo in metri")
            }, new[] { "id" }),
        Tool("aggiungi_variatore", "Aggiungi variatore di sezione",
            "Tra le progressive «inizio» e «fine» (metri lungo la linea dell'elemento) la larghezza cambia gradualmente di «variazione» metri e resta cambiata dopo la fine.",
            new JsonObject
            {
                ["id"] = Prop("integer", "Id dell'elemento"),
                ["inizio"] = Prop("number", "Progressiva di inizio (m)"),
                ["fine"] = Prop("number", "Progressiva di fine (m)"),
                ["variazione"] = Prop("number", "Variazione di larghezza in metri (positiva = allarga)")
            }, new[] { "id", "inizio", "fine", "variazione" }),
        Tool("elimina_variatore", "Elimina variatore", "Elimina un variatore di sezione di un elemento.",
            new JsonObject { ["id"] = Prop("integer", "Id dell'elemento"), ["numero"] = Prop("integer", "Numero del variatore (1 = il primo)") },
            new[] { "id", "numero" }, destructive: true),
        Tool("affianca_elemento", "Aggiungi elemento affiancato",
            "Crea un nuovo elemento accanto a quello indicato, lungo come lui e agganciato al suo contorno (destra/sinistra guardando la linea dall'inizio alla fine).",
            new JsonObject
            {
                ["id"] = Prop("integer", "Id dell'elemento di partenza"),
                ["lato"] = Prop("string", "Lato", new JsonArray("sinistra", "destra")),
                ["tipo"] = Prop("string", "Tipo del nuovo elemento (facoltativo: come quello di partenza)", TypeEnum()),
                ["larghezza"] = Prop("number", "Larghezza del nuovo elemento (facoltativa)")
            }, new[] { "id", "lato" }),
        Tool("elimina_elemento", "Elimina elemento", "Elimina un elemento dal progetto.",
            new JsonObject { ["id"] = Prop("integer", "Id dell'elemento") }, new[] { "id" }, destructive: true),
        Tool("salva_progetto", "Salva progetto", "Salva il progetto (.ciclo). Senza percorso sovrascrive il file aperto.",
            new JsonObject { ["percorso"] = Prop("string", "Percorso del file .ciclo (facoltativo)") }, Array.Empty<string>()),
        Tool("esporta_dxf", "Esporta DXF", "Esporta il progetto in DXF (R12, metri, coordinate del GeoPackage).",
            new JsonObject
            {
                ["percorso"] = Prop("string", "File .dxf da scrivere"),
                ["contesto"] = Prop("boolean", "Includi muri e livelli di sfondo (predefinito: sì)"),
                ["etichette"] = Prop("boolean", "Includi i testi con nome e misure (predefinito: sì)")
            }, new[] { "percorso" }),
        Tool("esporta_csv", "Esporta CSV", "Esporta le misure di tutti gli elementi in CSV.",
            new JsonObject { ["percorso"] = Prop("string", "File .csv da scrivere") }, new[] { "percorso" }),
        Tool("esporta_3d", "Esporta 3D", "Esporta la scena 3D (glTF binario .glb) con edifici, progetto, segnaletica e alberi.",
            new JsonObject
            {
                ["percorso"] = Prop("string", "File .glb da scrivere"),
                ["texture"] = Prop("boolean", "Materiali realistici con texture (predefinito: sì)"),
                ["altezza_edifici"] = Prop("number", "Altezza degli edifici senza altezza nei dati (m, predefinita 10)")
            }, new[] { "percorso" })
    );

    static object CallTool(string name, JsonObject a)
    {
        switch (name)
        {
            case "apri_progetto": return OpenProject(Str(a, "percorso"), OptStr(a, "geopackage"));
            case "apri_geopackage":
            {
                var path = Str(a, "percorso");
                if (!File.Exists(path)) throw new ToolError("GeoPackage non trovato: " + path);
                var d = GpkgReader.Load(path);
                Ed.SetData(d); _projectPath = null;
                return new { aperto = path, sistema_di_riferimento = d.Crs, livelli = Layers() };
            }
            case "elenco_livelli": Need(); return Layers();
            case "elenco_elementi": Need(); return Ed.Project.Elements.Select(Summary).ToList();
            case "dettaglio_elemento": Need(); return Detail(El(a));
            case "totali":
                Need();
                return Ed.Project.Elements.Where(e => e.Error == null).GroupBy(e => e.Type)
                    .Select(g => new { tipo = g.Key.ToString(), etichetta = Element.TypeLabel(g.Key), elementi = g.Count(), superficie_m2 = R(g.Sum(e => e.Metrics.Area)), lunghezza_m = R(g.Sum(e => e.Metrics.Length)) }).ToList();
            case "disegna_elemento": Need(); return Draw(a);
            case "modifica_elemento": Need(); return Modify(a);
            case "aggiungi_variatore":
            {
                Need(); var e = El(a);
                if (e.Mode != ElementMode.Offset) throw new ToolError("I variatori si usano solo sugli elementi tracciati con una larghezza.");
                double L = PL.Length(e.Guide);
                double t1 = Num(a, "inizio"), t2 = Num(a, "fine"), dv = Num(a, "variazione");
                if (t1 < 0 || t1 > L || t2 < 0 || t2 > L) throw new ToolError($"Le progressive devono stare tra 0 e {L:0.00} m.");
                if (Math.Abs(t2 - t1) < 0.2) throw new ToolError("Inizio e fine devono distare almeno 20 cm.");
                e.Variators.Add(new SectionVariator { T1 = t1, T2 = t2, Delta = Math.Round(dv, 3) });
                Ed.Project.Regenerate();
                return Detail(e);
            }
            case "elimina_variatore":
            {
                Need(); var e = El(a); int n = (int)Num(a, "numero");
                if (n < 1 || n > e.Variators.Count) throw new ToolError($"L'elemento ha {e.Variators.Count} variatori.");
                e.Variators.RemoveAt(n - 1); Ed.Project.Regenerate();
                return Detail(e);
            }
            case "affianca_elemento":
            {
                Need(); var e = El(a);
                Ed.Selected = e; Ed.Mode = Mode.Idle;
                if (!Ed.CanAddAlongside) throw new ToolError("Si può affiancare solo a un elemento tracciato con una larghezza, senza errori.");
                string lato = Str(a, "lato");
                int before = Ed.Project.Elements.Count;
                Ed.AddAlongside(lato == "sinistra");
                if (Ed.Project.Elements.Count == before || Ed.Selected is not { } ne) throw new ToolError(Ed.Message);
                if (OptStr(a, "tipo") is { } tp) Ed.SetSelectedType(ParseType(tp));
                if (OptNum(a, "larghezza") is { } w) Ed.SetSelectedWidth(w);
                return new { messaggio = Ed.Message, elemento = Detail(ne) };
            }
            case "elimina_elemento":
            {
                Need(); var e = El(a);
                Ed.Selected = e; Ed.Mode = Mode.Idle;
                int before = Ed.Project.Elements.Count;
                Ed.DeleteSelected();
                if (Ed.Project.Elements.Count == before) throw new ToolError(Ed.Message);
                return new { eliminato = e.Name, elementi_rimasti = Ed.Project.Elements.Count };
            }
            case "salva_progetto":
            {
                Need();
                var path = OptStr(a, "percorso") ?? _projectPath ?? throw new ToolError("Indica il percorso del file .ciclo.");
                ProjectFile.Save(path, Ed.Project, Ed.Data);
                _projectPath = path;
                return new { salvato = path, elementi = Ed.Project.Elements.Count };
            }
            case "esporta_dxf":
            {
                Need(); var path = Str(a, "percorso");
                DxfWriter.Write(path, Ed.Project, Ed.Data, OptBool(a, "contesto") ?? true, OptBool(a, "etichette") ?? true);
                return new { esportato = path, dimensione_byte = new FileInfo(path).Length };
            }
            case "esporta_csv":
            {
                Need(); var path = Str(a, "percorso");
                CsvWriter.Write(path, Ed.Project);
                return new { esportato = path, righe = Ed.Project.Elements.Count };
            }
            case "esporta_3d":
            {
                Need(); var path = Str(a, "percorso");
                var o = new Export3DOptions { Textures = OptBool(a, "texture") ?? true, DefaultBuildingHeight = OptNum(a, "altezza_edifici") ?? 10 };
                var r = Export3D.Write(path, Ed.Project, Ed.Data, o);
                return new { esportato = r.Path, triangoli = r.Triangles, elementi = r.Elements, edifici = r.Buildings, alberi = r.Trees, dimensione_byte = r.Bytes };
            }
            default: throw new ToolError("Strumento sconosciuto: " + name);
        }
    }

    // ───────────────────────── operazioni ─────────────────────────

    static object OpenProject(string path, string? gpkgOverride)
    {
        if (!File.Exists(path)) throw new ToolError("Progetto non trovato: " + path);
        var dto = ProjectFile.Read(path);
        var gp = gpkgOverride ?? dto.GpkgPath;
        if (gp == null || !File.Exists(gp))
        {
            var alt = gp != null ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(gp)) : null;
            if (alt != null && File.Exists(alt)) gp = alt;
            else throw new ToolError($"Il GeoPackage del progetto non è stato trovato ({dto.GpkgPath}). Indicalo con il parametro «geopackage».");
        }
        V2? origin = dto.Origin is { Length: 2 } o ? new V2(o[0], o[1]) : null;
        var d = GpkgReader.Load(gp, origin);
        foreach (var l in d.Layers)
        {
            if (dto.LayerRoles.TryGetValue(l.Name, out var r)) l.Role = r;
            l.Visible = !dto.HiddenLayers.Contains(l.Name);
        }
        var proj = ProjectFile.ToProject(dto, d.Origin);
        ProjectFile.LoadPhotos(dto, proj, path, d.Origin);
        Ed.SetData(d, proj);
        _projectPath = path;
        return new
        {
            progetto = path, geopackage = gp, sistema_di_riferimento = d.Crs,
            elementi = proj.Elements.Count, fotoinserimenti = proj.Photos.Count,
            estensione = Extent(),
            elenco = proj.Elements.Select(Summary).ToList()
        };
    }

    static object Draw(JsonObject a)
    {
        var t = ParseType(Str(a, "tipo"));
        string mode = Str(a, "modalita");
        if (a["punti"] is not JsonArray pts || pts.Count < 2) throw new ToolError("Servono almeno due punti.");
        bool poly = mode == "poligono", centered = mode == "interasse";
        if (!poly && !centered && mode != "bordo") throw new ToolError("modalita deve essere «bordo», «interasse» o «poligono».");
        if (poly && pts.Count < 3) throw new ToolError("Un poligono richiede almeno tre punti.");
        var origin = Ed.Data!.Origin;
        bool snap = OptBool(a, "aggancia") ?? false;
        bool oldSnap = Ed.SnapOn, oldFollow = Ed.FollowOn;
        Ed.SnapOn = snap; Ed.FollowOn = snap;
        try
        {
            Ed.Mode = Mode.Idle;
            Ed.StartDrawElement(t, centered, poly);
            foreach (var p in pts)
            {
                if (p is not JsonArray xy || xy.Count < 2) throw new ToolError("Ogni punto è [E, N].");
                Ed.TraceClick(new V2(xy[0]!.GetValue<double>() - origin.X, xy[1]!.GetValue<double>() - origin.Y), 0.5);
            }
            Ed.TraceFinish();
            // un poligono chiuso si salva subito
            if (poly && Ed.Mode == Mode.Idle && Ed.Selected is { Mode: ElementMode.Polygon } pe)
            {
                if (OptStr(a, "nome") is { } pn && pn.Trim().Length > 0) pe.Name = pn.Trim();
                return new { messaggio = Ed.Message, elemento = Detail(pe) };
            }
            if (Ed.Mode != Mode.SetWidth) { var msg = Ed.Message; Ed.TraceCancel(); throw new ToolError(msg); }
            if (OptNum(a, "larghezza") is { } w) Ed.SetDraftWidth(w);
            if (!poly && !centered && OptStr(a, "lato") is { } side)
            {
                int want = side == "sinistra" ? 1 : -1;
                if (Ed.DraftSide != want) Ed.FlipDraftSide();
            }
            Ed.BuildPreview();
            Ed.Commit();
            if (Ed.Selected is not { } e || Ed.Mode != Mode.Idle) { var msg = Ed.Message; Ed.CancelDraft(false); throw new ToolError(msg); }
            if (OptStr(a, "nome") is { } nm && nm.Trim().Length > 0) e.Name = nm.Trim();
            return new { messaggio = Ed.Message, elemento = Detail(e) };
        }
        finally { Ed.SnapOn = oldSnap; Ed.FollowOn = oldFollow; }
    }

    static object Modify(JsonObject a)
    {
        var e = El(a);
        Ed.Selected = e; Ed.Mode = Mode.Idle;
        if (OptStr(a, "nome") is { } nm && nm.Trim().Length > 0) e.Name = nm.Trim();
        if (OptStr(a, "tipo") is { } tp) Ed.SetSelectedType(ParseType(tp));
        if (OptNum(a, "larghezza") is { } w)
        {
            if (e.Mode != ElementMode.Offset) throw new ToolError("La larghezza si cambia solo sugli elementi tracciati.");
            Ed.SetSelectedWidth(w);
        }
        if (OptBool(a, "inverti_lato") == true)
        {
            if (e.Centered || e.Mode != ElementMode.Offset) throw new ToolError("Il lato si inverte solo sugli elementi disegnati dal bordo.");
            Ed.FlipSelected();
        }
        if (OptNum(a, "strisce") is { } n || OptStr(a, "stile_strisce") is not null)
        {
            if (!e.SupportsDividers) throw new ToolError("Questo tipo di elemento non ha strisce di mezzeria.");
            int count = OptNum(a, "strisce") is { } c ? (int)c : e.Dividers;
            var style = OptStr(a, "stile_strisce") is { } st ? Enum.Parse<DividerStyle>(st, true) : e.DividerStyle;
            Ed.SetSelectedDividers(count, style);
        }
        if (OptBool(a, "linee_stalli") is not null || OptNum(a, "lunghezza_stalli") is not null)
        {
            if (e.Type != ElementType.Parcheggio) throw new ToolError("Gli stalli valgono solo per i parcheggi.");
            Ed.SetSelectedStalls(OptBool(a, "linee_stalli") ?? e.StallLines, OptNum(a, "lunghezza_stalli") ?? e.StallLength);
        }
        if (OptNum(a, "altezza") is { } hgt)
        {
            if (!e.HasHeight) throw new ToolError("L'altezza vale solo per marciapiedi e aiuole.");
            Ed.SetSelectedHeight(hgt);
        }
        Ed.Project.Regenerate();
        return Detail(e);
    }

    // ───────────────────────── descrizioni ─────────────────────────

    static object Layers() => Ed.Data!.Layers.Select(l => new
    {
        nome = l.Name, geometria = l.GeomType, elementi = l.FeatureCount,
        ruolo = ((LayerRole)l.Role).ToString(), visibile = l.Visible
    }).ToList();

    static object Extent()
    {
        var b = Ed.Extent(); var o = Ed.Data!.Origin;
        return new { min_e = R(b.MinX + o.X), min_n = R(b.MinY + o.Y), max_e = R(b.MaxX + o.X), max_n = R(b.MaxY + o.Y) };
    }

    static object Summary(Element e) => new
    {
        id = e.Id, nome = e.Name, tipo = e.Type.ToString(), modalita = ModeName(e),
        larghezza_m = e.Mode == ElementMode.Offset ? R(e.Width) : (double?)null,
        estensione_m = R(e.Metrics.Length), superficie_m2 = R(e.Metrics.Area),
        larghezza_min_m = R(e.Metrics.WMin), larghezza_max_m = R(e.Metrics.WMax), larghezza_media_m = R(e.Metrics.WMean),
        variatori = e.Variators.Count, errore = e.Error
    };

    static object Detail(Element e)
    {
        var o = Ed.Data!.Origin;
        double[]? P(V2 v) => new[] { R(v.X + o.X), R(v.Y + o.Y) };
        return new
        {
            id = e.Id, nome = e.Name, tipo = e.Type.ToString(), etichetta = Element.TypeLabel(e.Type), modalita = ModeName(e),
            larghezza_m = e.Mode == ElementMode.Offset ? R(e.Width) : (double?)null,
            lato = e.Mode == ElementMode.Offset && !e.Centered ? (e.Side > 0 ? "sinistra" : "destra") : null,
            linea = e.Guide.Count >= 2 ? new { inizio = P(e.Guide[0]), fine = P(e.Guide[^1]), lunghezza_m = R(PL.Length(e.Guide)) } : null,
            misure = new
            {
                estensione_m = R(e.Metrics.Length), superficie_m2 = R(e.Metrics.Area),
                larghezza_min_m = R(e.Metrics.WMin), larghezza_max_m = R(e.Metrics.WMax), larghezza_media_m = R(e.Metrics.WMean)
            },
            variatori = e.Variators.Select((v, i) => new
            {
                numero = i + 1, inizio_m = R(v.T1), fine_m = R(v.T2), variazione_m = R(v.Delta),
                larghezza_prima_m = R(Ed.VariatorWidths(e, i).before), larghezza_dopo_m = R(Ed.VariatorWidths(e, i).after)
            }).ToList(),
            strisce = e.SupportsDividers ? new { numero = e.EffectiveDividers, automatico = e.Dividers < 0, stile = e.DividerStyle.ToString() } : null,
            stalli = e.Type == ElementType.Parcheggio ? new { linee = e.StallLines, lunghezza_m = R(e.StallLength) } : null,
            altezza_m = e.HasHeight ? R(e.Height) : (double?)null,
            errore = e.Error
        };
    }

    static string ModeName(Element e) => e.Mode switch
    {
        ElementMode.Polygon => "poligono",
        ElementMode.Fill => "riempimento",
        _ => e.Centered ? "interasse" : "bordo"
    };

    // ───────────────────────── argomenti ─────────────────────────

    static void Need() { if (Ed.Data == null) throw new ToolError("Nessun progetto aperto: usa prima apri_progetto o apri_geopackage."); }

    static Element El(JsonObject a)
    {
        int id = (int)Num(a, "id");
        return Ed.Project.Find(id) ?? throw new ToolError($"Nessun elemento con id {id}.");
    }

    static ElementType ParseType(string s) =>
        Enum.TryParse<ElementType>(s, true, out var t) ? t
        : Enum.GetValues<ElementType>().FirstOrDefault(x => string.Equals(Element.TypeLabel(x), s, StringComparison.OrdinalIgnoreCase)) is var f && string.Equals(Element.TypeLabel(f), s, StringComparison.OrdinalIgnoreCase) ? f
        : throw new ToolError($"Tipo sconosciuto «{s}». Tipi: {string.Join(", ", Enum.GetNames<ElementType>())}.");

    static string Str(JsonObject a, string k) => OptStr(a, k) ?? throw new ToolError($"Manca il parametro «{k}».");
    static string? OptStr(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double Num(JsonObject a, string k) => OptNum(a, k) ?? throw new ToolError($"Manca il parametro «{k}».");
    static double? OptNum(JsonObject a, string k)
    {
        if (a[k] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out d)) return d;
        return null;
    }
    static bool? OptBool(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    static double R(double v) => Math.Round(v, 3);
}
