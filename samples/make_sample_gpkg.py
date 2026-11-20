"""Genera samples/esempio_via.gpkg: una via di 200 m con edifici su due lati (EPSG:3003)."""
import sqlite3, struct, os, sys

out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "esempio_via.gpkg")
if os.path.exists(out):
    os.remove(out)
E0, N0, SRS = 1514000.0, 5034000.0, 3003
WKT = ('PROJCS["Monte Mario / Italy zone 1",GEOGCS["Monte Mario",DATUM["Monte_Mario",SPHEROID["International 1924",6378388,297]],'
       'PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["latitude_of_origin",0],'
       'PARAMETER["central_meridian",9],PARAMETER["scale_factor",0.9996],PARAMETER["false_easting",1500000],PARAMETER["false_northing",0],'
       'UNIT["metre",1],AUTHORITY["EPSG","3003"]]')


def gp(wkb):
    return b"GP" + bytes([0, 1]) + struct.pack("<i", SRS) + wkb


def poly(rings):
    b = struct.pack("<BII", 1, 3, len(rings))
    for r in rings:
        r = [(x + E0, y + N0) for x, y in r]
        if r[0] != r[-1]:
            r = r + [r[0]]
        b += struct.pack("<I", len(r)) + b"".join(struct.pack("<dd", *p) for p in r)
    return b


def line(pts):
    pts = [(x + E0, y + N0) for x, y in pts]
    return struct.pack("<BII", 1, 2, len(pts)) + b"".join(struct.pack("<dd", *p) for p in pts)


edifici = [
    [(0, 8), (15, 8), (15, 8.6), (22, 8.6), (22, 8), (40, 8), (40, 20), (0, 20)],
    [(46, 8), (70, 8), (70, 7.7), (78, 7.7), (78, 8), (100, 8), (100, 22), (46, 22)],
    [(106, 8), (200, 8), (200, 20), (106, 20)],
    [(0, -20), (70, -20), (70, -8), (36, -8), (36, -8.5), (30, -8.5), (30, -8), (0, -8)],
    [(76, -8), (200, -8), (200, -20), (76, -20)],
]
marciapiedi = [[(0, 8), (40, 8), (40, 6.8), (0, 6.8)], [(76, -8), (200, -8), (200, -6.5), (76, -6.5)]]
assi = [[(-10, 0), (210, 0)], [(43, 30), (43, 0)], [(73, 0), (73, -30)]]

con = sqlite3.connect(out)
c = con.cursor()
c.execute("PRAGMA application_id=1196444487")
c.execute("PRAGMA user_version=10300")
c.execute("CREATE TABLE gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER PRIMARY KEY, organization TEXT NOT NULL, "
          "organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL, description TEXT)")
c.executemany("INSERT INTO gpkg_spatial_ref_sys VALUES (?,?,?,?,?,?)", [
    ("Undefined cartesian SRS", -1, "NONE", -1, "undefined", None),
    ("Undefined geographic SRS", 0, "NONE", 0, "undefined", None),
    ("Monte Mario / Italy zone 1", SRS, "EPSG", SRS, WKT, None)])
c.execute("CREATE TABLE gpkg_contents (table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL, identifier TEXT UNIQUE, "
          "description TEXT DEFAULT '', last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')), "
          "min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER)")
c.execute("CREATE TABLE gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL, "
          "srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL, PRIMARY KEY (table_name, column_name))")
for name, gtype, geoms in [("edifici", "POLYGON", [poly([g]) for g in edifici]),
                           ("marciapiedi_esistenti", "POLYGON", [poly([g]) for g in marciapiedi]),
                           ("assi_stradali", "LINESTRING", [line(g) for g in assi])]:
    c.execute(f'CREATE TABLE "{name}" (fid INTEGER PRIMARY KEY AUTOINCREMENT, geom {gtype}, nome TEXT)')
    c.execute("INSERT INTO gpkg_contents (table_name,data_type,identifier,srs_id) VALUES (?,?,?,?)", (name, "features", name, SRS))
    c.execute("INSERT INTO gpkg_geometry_columns VALUES (?,?,?,?,0,0)", (name, "geom", gtype, SRS))
    for i, g in enumerate(geoms):
        c.execute(f'INSERT INTO "{name}" (geom,nome) VALUES (?,?)', (gp(g), f"{name} {i + 1}"))
con.commit()
con.close()
print("creato", out)
