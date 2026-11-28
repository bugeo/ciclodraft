"""Crea CicloDraft.app (macOS) da una cartella di `dotnet publish -r osx-arm64|osx-x64` e la comprime in uno .zip.
Uso: python3 make_mac_app.py cartella_publish uscita.zip

L'app non è firmata: al primo avvio sul Mac serve il passaggio descritto in LEGGIMI.md
(xattr + codesign ad-hoc, comandi già presenti in macOS)."""
import os, sys, shutil, stat, zipfile, tempfile
from PIL import Image, ImageDraw, ImageFont

src, out = sys.argv[1], sys.argv[2]
work = tempfile.mkdtemp()
app = os.path.join(work, "CicloDraft.app")
macos = os.path.join(app, "Contents", "MacOS"); res = os.path.join(app, "Contents", "Resources")
shutil.copytree(src, macos, ignore=shutil.ignore_patterns("*.pdb", "createdump"))
os.makedirs(res)

# icona: cerchio arancio con la C, come nell'intestazione del programma
S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0)); d = ImageDraw.Draw(img)
d.rounded_rectangle((60, 60, S - 60, S - 60), radius=220, fill=(30, 40, 51, 255))
d.ellipse((190, 190, S - 190, S - 190), fill=(214, 96, 63, 255))
try: font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 400)
except OSError: font = ImageFont.load_default()
d.text((S / 2, S / 2 + 10), "C", fill="white", font=font, anchor="mm")
img.save(os.path.join(res, "CicloDraft.icns"), sizes=[(16, 16), (32, 32), (64, 64), (128, 128), (256, 256), (512, 512), (1024, 1024)])

version = "1.1.0"
with open(os.path.join(app, "Contents", "Info.plist"), "w") as f:
    f.write(f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>CicloDraft</string>
  <key>CFBundleDisplayName</key><string>CicloDraft</string>
  <key>CFBundleIdentifier</key><string>it.ciclodraft.app</string>
  <key>CFBundleExecutable</key><string>CicloDraft</string>
  <key>CFBundleIconFile</key><string>CicloDraft.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>{version}</string>
  <key>CFBundleShortVersionString</key><string>{version}</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.graphics-design</string>
</dict>
</plist>
""")
for name in os.listdir(macos):
    p = os.path.join(macos, name)
    if name == "CicloDraft" or name.endswith(".dylib"): os.chmod(p, 0o755)

# zip con i permessi Unix (l'eseguibile deve restare eseguibile)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(app):
        dirs.sort()
        rel = os.path.relpath(root, work)
        zi = zipfile.ZipInfo(rel + "/"); zi.create_system = 3; zi.external_attr = (stat.S_IFDIR | 0o755) << 16 | 0x10
        z.writestr(zi, b"")
        for name in sorted(files):
            p = os.path.join(root, name)
            zi = zipfile.ZipInfo.from_file(p, os.path.relpath(p, work))
            zi.create_system = 3; zi.compress_type = zipfile.ZIP_DEFLATED
            zi.external_attr = (stat.S_IFREG | (os.stat(p).st_mode & 0o777)) << 16
            with open(p, "rb") as fh: z.writestr(zi, fh.read())
shutil.rmtree(work)
print("creato", out)
