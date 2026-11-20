# CicloDraft

Programma desktop (Avalonia / .NET 8, Windows e macOS) per disegnare piste ciclabili, marciapiedi e
carreggiate a partire da un GeoPackage, con esportazione DXF, 3D (glTF/GLB), CSV e fotoinserimento.

## Compilare

Serve il .NET 8 SDK.

```
dotnet run -c Release
```

Eseguibile Windows in un solo file:

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o pub
```

App per Mac (Apple Silicon `osx-arm64`, Intel `osx-x64`):

```
dotnet publish -c Release -r osx-arm64 --self-contained -o pub
python3 packaging/make_mac_app.py pub CicloDraft-mac.zip
```

La cartella `modelli` (rimozione delle auto nel fotoinserimento) non è nel repository: va copiata accanto
all'eseguibile.

## Guida

Tutte le funzioni sono descritte in [LEGGIMI.md](LEGGIMI.md).
