"""Genera samples/esempio_curve.gpkg: via di circa 260 m con curve leggere, due restringimenti,
facciate non allineate (piccole rientranze) e vicoli laterali. Sistema di riferimento EPSG:6707
(RDN2008 / UTM 32N). Solo libreria standard di Python 3.

Uso: python make_sample_curve.py [file.gpkg] [--clicks punti.json]
"""
import json, math, os, sqlite3, struct, sys

args = [a for a in sys.argv[1:]]
clicks_path = None
if "--clicks" in args:
    i = args.index("--clicks"); clicks_path = args[i + 1]; del args[i:i + 2]
out = args[0] if args else os.path.join(os.path.dirname(os.path.abspath(__file__)), "esempio_curve.gpkg")
if os.path.exists(out):
    os.remove(out)

E0, N0, SRS = 512000.0, 5034000.0, 6707
WKT = ('PROJCS["RDN2008 / UTM zone 32N",GEOGCS["RDN2008",DATUM["Rete_Dinamica_Nazionale_2008",SPHEROID["GRS 1980",6378137,298.257222101]],'
       'PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["latitude_of_origin",0],'
       'PARAMETER["central_meridian",9],PARAMETER["scale_factor",0.9996],PARAMETER["false_easting",500000],PARAMETER["false_northing",0],'
       'UNIT["metre",1],AUTHORITY["EPSG","6707"]]')

# ── asse stradale: curve leggere ─────────────────────────────────────────────
def axis(s):
    x = s
    y = 9.0 * math.sin(s / 48.0) + 0.012 * (s - 130) ** 2 / 10
    return x, y

def frame(s):
    x0, y0 = axis(s - 0.5); x1, y1 = axis(s + 0.5)
    dx, dy = x1 - x0, y1 - y0; l = math.hypot(dx, dy)
    tx, ty = dx / l, dy / l
    return axis(s), (-ty, tx)  # punto, normale a sinistra

def bump(s, c, half):
    """1 al centro, 0 oltre c±half, raccordo dolce."""
    d = abs(s - c)
    return 0.0 if d >= half else 0.5 * (1 + math.cos(math.pi * d / half))

# semi-larghezze tra asse e facciate (m): via di ~16 m che si stringe a ~11 m
def half_left(s):  return 8.0 - 2.6 * bump(s, 128, 26)
def half_right(s): return 8.0 - 1.8 * bump(s, 128, 26)

# edifici: (inizio, fine, profondità, rientranze [(da, a, scostamento m)], avanzamento fisso m)
LEFT = [
    (0, 32, 13, [(10, 17, 0.6)], 0.0),
    (37, 70, 15, [], 0.0),
    (76, 106, 12, [(84, 96, 0.4)], 0.0),
    (111, 150, 14, [], 0.0),
    (156, 190, 13, [(170, 176, -0.5)], 0.0),   # piccola sporgenza (balcone/scala)
    (196, 230, 15, [], 0.0),
    (235, 262, 12, [], 0.0),
]
RIGHT = [
    (0, 40, 14, [], 0.0),
    (46, 80, 12, [(55, 66, 0.8)], 0.0),
    (85, 120, 15, [], 0.0),
    (124, 160, 13, [], 0.0),
    (166, 192, 12, [], 0.0),
    (196, 220, 14, [], 2.2),                   # edificio avanzato: restringimento puntuale
    (224, 262, 13, [(240, 248, 0.5)], 0.0),
]

def facade(span, side):
    a, b, depth, jogs, advance = span
    hw = half_left if side > 0 else half_right
    # progressive dei vertici: ogni 4 m + bordi delle rientranze
    marks = sorted(set([a, b] + [v for j in jogs for v in j[:2]] + list(range(int(a) + 4, int(b), 4))))
    pts = []
    for s in marks:
        offs = []
        for (j0, j1, d) in jogs:
            if abs(s - j0) < 1e-9: offs = [0.0, d]
            elif abs(s - j1) < 1e-9: offs = [d, 0.0]
            elif j0 < s < j1: offs = [d]
        if not offs: offs = [0.0]
        (px, py), (nx, ny) = frame(s)
        for o in offs:
            r = hw(s) - advance + o
            pts.append((px + side * nx * r, py + side * ny * r))
    front = pts
    back = []
    for s in reversed(marks[::2] + ([marks[-1]] if len(marks) % 2 == 0 else [])):
        (px, py), (nx, ny) = frame(s)
        r = hw(s) - advance + depth
        back.append((px + side * nx * r, py + side * ny * r))
    return front, front + back

def gp(wkb): return b"GP" + bytes([0, 1]) + struct.pack("<i", SRS) + wkb

def poly(ring):
    r = [(x + E0, y + N0) for x, y in ring]
    if r[0] != r[-1]: r.append(r[0])
    return struct.pack("<BIII", 1, 3, 1, len(r)) + b"".join(struct.pack("<dd", *p) for p in r)

def line(pts):
    pts = [(x + E0, y + N0) for x, y in pts]
    return struct.pack("<BII", 1, 2, len(pts)) + b"".join(struct.pack("<dd", *p) for p in pts)

buildings, clicks = [], {"left": [], "right": []}
for side, spans, key in ((1, LEFT, "left"), (-1, RIGHT, "right")):
    for sp in spans:
        front, ring = facade(sp, side)
        buildings.append(ring)
        clicks[key] += [[front[0][0] + E0, front[0][1] + N0], [front[-1][0] + E0, front[-1][1] + N0]]

# marciapiede esistente (rilievo) sul lato sinistro nei primi 70 m
sw_out, sw_in = [], []
for s in range(0, 71, 2):
    (px, py), (nx, ny) = frame(s)
    sw_in.append((px + nx * half_left(s), py + ny * half_left(s)))
    sw_out.append((px + nx * (half_left(s) - 1.4), py + ny * (half_left(s) - 1.4)))
sidewalks = [sw_in + sw_out[::-1]]

centre = [frame(s)[0] for s in range(-8, 271, 2)]
side_streets = []
for c, side in ((34.5, 1), (73, 1), (193, 1), (43, -1), (163, -1), (222, -1)):
    (px, py), (nx, ny) = frame(c)
    side_streets.append([(px, py), (px + side * nx * 35, py + side * ny * 35)])

con = sqlite3.connect(out); cur = con.cursor()
cur.execute("PRAGMA application_id=1196444487"); cur.execute("PRAGMA user_version=10300")
cur.execute("CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER PRIMARY KEY, organization TEXT NOT NULL, "
            "organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT)")
cur.executemany("INSERT INTO gpkg_spatial_ref_sys VALUES (?,?,?,?,?,?)", [
    ("Undefined cartesian SRS", -1, "NONE", -1, "undefined", None),
    ("Undefined geographic SRS", 0, "NONE", 0, "undefined", None),
    ("RDN2008 / UTM zone 32N", SRS, "EPSG", SRS, WKT, None)])
cur.execute("CREATE TABLE gpkg_contents (table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE, "
            "description TEXT DEFAULT '', last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), "
            "min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER)")
cur.execute("CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL, "
            "srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL, PRIMARY KEY (table_name, column_name))")
for name, gtype, geoms in (("edifici", "POLYGON", [poly(b) for b in buildings]),
                           ("marciapiedi_esistenti", "POLYGON", [poly(s) for s in sidewalks]),
                           ("assi_stradali", "LINESTRING", [line(centre)] + [line(s) for s in side_streets])):
    cur.execute(f'CREATE TABLE "{name}" (fid INTEGER PRIMARY KEY AUTOINCREMENT, geom {gtype}, nome TEXT)')
    cur.execute("INSERT INTO gpkg_contents (table_name,data_type,identifier,srs_id) VALUES (?,?,?,?)", (name, "features", name, SRS))
    cur.execute("INSERT INTO gpkg_geometry_columns VALUES (?,?,?,?,0,0)", (name, "geom", gtype, SRS))
    for i, g in enumerate(geoms):
        cur.execute(f'INSERT INTO "{name}" (geom,nome) VALUES (?,?)', (gp(g), f"{name} {i + 1}"))
con.commit(); con.close()
if clicks_path:
    json.dump(clicks, open(clicks_path, "w"))
print("creato", out)
