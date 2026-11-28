# CicloDraft

Programma desktop (Avalonia / .NET 8, Windows e macOS) per disegnare piste ciclabili, marciapiedi e
carreggiate a partire da un GeoPackage, con esportazione DXF, 3D (glTF/GLB), CSV, PDF e fotoinserimento.

## Struttura del repository

| Cartella | Contenuto |
|---|---|
| `src/` | il programma: progetto `CicloDraft.csproj`, codice, temi, esempi (`src/samples`) e script per l'app Mac (`src/packaging`) |
| `docs/` | il sito web, pubblicato con GitHub Pages su <https://bugeo.github.io/ciclodraft/> |
| `CicloDraft.sln` | la soluzione per Visual Studio 2022 o Rider |

La guida d'uso è nel [wiki](https://github.com/bugeo/ciclodraft/wiki) e in [src/LEGGIMI.md](src/LEGGIMI.md).

## Compilare

Serve il .NET 8 SDK. Con Visual Studio 2022 (17.8 o successivo) apri `CicloDraft.sln` e premi F5; da riga di comando:

```
cd src
dotnet run -c Release
```

Eseguibile Windows in un solo file (dalla cartella `src`):

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

## Server MCP

`CicloDraft --mcp` avvia un server [MCP](https://modelcontextprotocol.io) su stdin/stdout: un assistente AI
può aprire, leggere, modificare ed esportare i progetti. Vedi [src/LEGGIMI.md](src/LEGGIMI.md#server-mcp-assistenti-ai).

## Licenza

CicloDraft è software libero, distribuito con la licenza **GNU General Public License v3.0 o successiva**
(GPL-3.0-or-later): vedi il file [LICENSE](LICENSE).

## Guida

Tutte le funzioni sono descritte nel [wiki](https://github.com/bugeo/ciclodraft/wiki) e in [src/LEGGIMI.md](src/LEGGIMI.md).
