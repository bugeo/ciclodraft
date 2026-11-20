# CicloDraft

Programma desktop (Avalonia / .NET 8) per disegnare marciapiedi, piste ciclabili e carreggiate
partendo da un GeoPackage con i muri delle abitazioni, ed esportare il progetto in DXF (metri).

## Avvio

- **Windows**: apri `CicloDraft.exe` (non serve installare nulla).
- **Mac**: scarica `CicloDraft-mac-AppleSilicon.zip` (Mac con chip M1/M2/M3/M4) oppure
  `CicloDraft-mac-Intel.zip` (Mac con processore Intel; lo dice  → *Informazioni su questo Mac*),
  fai doppio clic sullo zip e sposta `CicloDraft.app` dove preferisci (es. *Applicazioni*).
  L'app non è firmata da uno sviluppatore registrato Apple, quindi **la prima volta** apri il
  Terminale e scrivi queste due righe (trascina `CicloDraft.app` nella finestra del Terminale al
  posto del percorso):
  ```
  xattr -cr /Applications/CicloDraft.app
  codesign --force --deep --sign - /Applications/CicloDraft.app
  ```
  Poi si apre con un normale doppio clic. Per la rimozione delle auto copia la cartella `modelli`
  accanto a `CicloDraft.app` (nella stessa cartella).
- **Da sorgente** (Windows, macOS, Linux): installa .NET 8 SDK, poi nella cartella `CicloDraft`
  esegui `dotnet run -c Release`.
- Per provare subito ci sono due GeoPackage in `samples`:
  - `esempio_via.gpkg`: via dritta di 200 m con edifici sui due lati (EPSG:3003);
  - `esempio_curve.gpkg`: via di circa 265 m con curve leggere, un restringimento graduale
    (da circa 16 a 11,6 m tra le facciate, intorno a 130 m), un edificio avanzato di 2,2 m sul lato
    destro (circa 200 m), piccole rientranze e sporgenze delle facciate e vicoli laterali (EPSG:6707).

## Flusso di lavoro

0. **Ritaglia dati** (per file grandi, es. il DBGT comunale) – menu *File → Ritaglia dati…*. Si apre
   da solo anche quando con *Apri GeoPackage…* scegli un file più grande di 25 MB.
   - *Aggiungi file*: uno o più GeoPackage (`.gpkg`) e shapefile (`.shp`, con i suoi `.dbf`/`.shx`
     accanto; se i dati sono in uno `.zip`, estrailo prima).
   - **Livelli**: *Tieni* = il livello va nel file ritagliato; *Mostra* = lo disegna sulla mappa.
     Il programma propone i livelli utili (per il DBGT: `EDIFC_CR_EDF_IS` edifici, `EDI_MIN_CR_EDF_IS`
     edifici minori, `AC_PED` marciapiedi, `AC_VEI` carreggiate, `AC_CIC` ciclabili, `EL_STR` assi,
     `AR_STR`, `SV_STR`, e per il 3D `UN_VOL` con le altezze degli edifici e `ALBERO`). *Cerca* filtra l'elenco per nome.
   - **Area**: *Rettangolo* (trascina), *Poligono* (clic sui vertici, doppio clic per chiudere),
     coordinate E/N scritte a mano, oppure *Usa l'estensione del livello selezionato* (es. lo
     shapefile della zona di progetto). *Margine* allarga l'area di qualche metro.
   - Accanto a ogni livello compare quanti elementi cadono nell'area.
   - **Salva**: in modo predefinito strade, marciapiedi e assi vengono tagliati sul bordo dell'area,
     mentre gli edifici restano interi (servono completi per ricalcare i muri). In alternativa si può
     tagliare tutto o tenere tutto intero.
   - *Salva e apri in CicloDraft* scrive il nuovo GeoPackage (con attributi, quote Z e sistema di
     riferimento) e lo apre per disegnare. Il file originale non viene mai modificato. I livelli
     vuoti nell'area non vengono salvati. Uno shapefile senza `.prj` viene salvato con il sistema di
     riferimento degli altri livelli (non viene riproiettato).
   - Esempio: il DBGT di Mantova (123 MB, 86 livelli) ritagliato sull'area dello shapefile AC_PED
     diventa un file di 2 MB con 8 livelli, in meno di un secondo.

In alto ci sono i menu: **File** (Apri progetto, Apri GeoPackage, Ritaglia dati, Salva),
**Esporta** (DXF, 3D, CSV) e **Visualizza** (*Etichette con i nomi degli elementi*
sulla mappa, attive o no; DXF con o senza contesto, con o senza etichette). Il riquadro con la **cornice verde** è quello su cui agire adesso (stessi colori e stessa
guida di SpeedVision). A sinistra stanno *Livelli*, *Elementi*, *Fotoinserimenti* e *Totali per tipo*. I riquadri per
aggiungere o modificare un elemento compaiono a destra, sopra la mappa, solo quando servono: la mappa
resta sempre a tutta larghezza. Sulla mappa: **Adatta vista** in
alto a sinistra e **+ Aggiungi elemento** in alto al centro.

1. **Inizia** – all'avvio, al centro della mappa: *Apri progetto…* (un `.ciclo` salvato) oppure
   *Apri GeoPackage…* (anche dal menu File, Ctrl+O apre un progetto, Ctrl+S salva). Aprendo un
   GeoPackage il primo passo sono i livelli; aprendo un progetto salvato i livelli sono già impostati
   e il loro riquadro resta chiuso (*Modifica* lo riapre).
2. **Livelli** – per ogni livello, sotto al nome, scegli il ruolo (*Muri abitazioni*, *Marciapiedi*,
   *Sfondo*, *Edifici 3D* o *Nascosto*; il programma lo propone dal nome) e la casella **Visibile**
   per vederlo o no sulla mappa (un livello non visibile conserva il suo ruolo: i muri continuano a
   servire per l'aggancio). Premi **Conferma i livelli**: il riquadro si chiude in una riga di
   riepilogo (con *Modifica* per riaprirlo) e in alto sulla mappa compare **+ Aggiungi elemento**.
   Ruoli e visibilità si salvano nel progetto.
3. **Aggiungi elemento** – il pulsante in alto sulla mappa apre il riquadro a destra, sopra la mappa (✕ lo chiude;
   dopo «Disegna» si chiude da solo). All'inizio mostra solo la scelta dell'elemento (nessuno è
   preselezionato); scelto l'elemento compaiono la **modalità** e il resto:
   - **Tracciato**: l'elemento si sviluppa lungo una linea. Scegli cosa disegni:
     - **Bordo**: l'elemento sta da un lato della linea (es. un marciapiede lungo i muri, una ciclabile
       lungo un marciapiede). Dopo il disegno il programma propone il lato libero; *Inverti lato* se serve.
     - **Interasse**: l'elemento sta metà per parte (per carreggiata, corsia doppia e striscia gialla è
       già proposto).
     - **Riempimento**: occupa lo spazio tra due elementi già disegnati: clicca il contorno del primo,
       scegli il tratto, poi il contorno del secondo.
   - **Poligono**: disegni il contorno chiuso dell'elemento (un'aiuola, un'isola, una piazzola):
     clic su ogni vertice, doppio clic o Invio per chiuderlo. Di un poligono si misurano superficie e
     perimetro.

   Premi **Disegna sulla mappa** (o *Disegna il poligono*) e disegna: clic per ogni punto, doppio clic
   o Invio per finire, Backspace per togliere l'ultimo, Esc per annullare. Per un tracciato imposta poi
   la larghezza; controlla l'anteprima e premi **Disegna**. In entrambe le modalità:
   - *Aggancia*: il punto si attacca a vertici e lati dei muri, dei marciapiedi esistenti e degli
     **elementi già disegnati** (quadrato = vertice, X = lato).
   - *Segui il contorno*: con due clic sullo stesso edificio o elemento, la linea ne segue il profilo
     (rientranze, sporgenze, spigoli). Per un elemento tutto attorno a un edificio: clic sui vertici e
     di nuovo sul primo.
   - **Lungo come …**: se il primo punto di un tracciato si aggancia al contorno di un elemento già
     disegnato, compare il pulsante «Lungo come «Marciapiede 1»»: la linea del nuovo elemento prende
     tutto quel contorno, da un capo all'altro (o tutto il giro, se l'elemento gira attorno a un
     edificio), e lo segue se cambia.
   - L'**elemento** si può cambiare dalla tendina anche dopo averlo scelto, mentre disegni o con
     l'anteprima già visibile: la linea resta, e la larghezza passa a quella tipica del nuovo elemento
     se non l'avevi cambiata a mano.
   - I punti cliccati sul contorno di un elemento restano **agganciati**: se poi quell'elemento si
     allarga, si restringe o si spezza, la linea (o il lato del poligono) lo segue. Esempio: due
     edifici con un marciapiede attorno a ciascuno, poi una ciclabile con due clic sul lato strada del
     primo marciapiede e due sul secondo: la ciclabile costeggia entrambi e li segue.

   Elementi disponibili:
   - *Marciapiede*, *Pista ciclabile* (in sede propria, in resina rossa; rialzabile nel 3D),
     *Carreggiata*, *Corsia*, *Parcheggio*, *Aiuola / verde*;
   - *Corsia ciclabile* (predefinita 1,50 m): ciclabile ricavata sulla carreggiata, a quota strada,
     in rosso con striscia bianca continua verso il traffico e simboli della bici;
   - *Striscia gialla* (predefinita 0,50 m): fascia gialla di separazione, per esempio tra corsia
     ciclabile e carreggiata o tra due corsie;
   - *Corsia doppia* (predefinita 6,00 m): due corsie di marcia in un solo elemento, con la
     striscia bianca tratteggiata (4,5 / 7,5 m) a metà larghezza.

   I progetti fatti con le versioni precedenti (con i bordi strada) si aprono e si modificano come
   prima: i loro bordi restano sulla mappa e si possono usare come contorni da cui riempire.

## Modificare un elemento

Clicca l'elemento sulla mappa o nell'elenco a sinistra. Nel riquadro *Elemento selezionato*, sotto al nome,
ci sono tre schede: **Dati** (tipo, larghezza, linea, strisce, elementi affiancati, elimina), **Variatori (n)**
(i variatori di sezione, con il loro numero) e **Info** (estensione, superficie e larghezze).

- **Tipo**: la tendina sotto al nome cambia il tipo dell'elemento (es. da marciapiede a pista
  ciclabile): forma e larghezza restano, cambiano colore, layer DXF, materiali 3D e segnaletica. Se il
  nome era quello automatico («Marciapiede 2») diventa quello del nuovo tipo.
- **Strisce di mezzeria** (carreggiata, corsia, corsia doppia, pista e corsia ciclabile): numero di
  strisce e tipo (*tratteggiate*, *continue*, *doppie continue*). Le strisce dividono l'elemento in
  parti uguali (es. 3 strisce su 12 m = 4 corsie da 3 m) e seguono le variazioni di larghezza.
  *Automatico* torna alla regola predefinita (una mezzeria sulla carreggiata da 5,5 m in su, sulla
  corsia doppia e sulla pista ciclabile bidirezionale da 2,5 m). Compaiono sulla mappa, nel 3D, nel
  fotoinserimento e nel DXF (layer `CD_SEGNALETICA`).
- **Parcheggio**: la casella *Linee bianche degli stalli di sosta* (attiva in modo predefinito) mostra o
  nasconde le linee degli stalli e la linea lungo il lato verso la strada; *Lunghezza degli stalli* (5,00 m
  in modo predefinito) cambia la distanza tra le linee. Valgono sulla mappa, nel 3D, nel fotoinserimento
  e nel DXF (layer `CD_SEGNALETICA`) e si salvano nel progetto.
- **Larghezza**: pulsanti −0,5 / −0,1 / +0,1 / +0,5, il campo numerico, oppure i tasti + e −.
- **Variatori di sezione** (elementi tracciati: carreggiata, ciclabile, corsia, marciapiede…): per far
  cambiare la larghezza in un tratto. *Aggiungi variatore di sezione*, poi clic sull'elemento dove inizia
  il cambio e dove finisce: tra i due punti la larghezza varia gradualmente e oltre la fine resta quella
  nuova. Nell'elenco ogni variatore mostra la larghezza prima e dopo (es. «V1 6,00 → 7,00 m»): **+** e **−**
  allargano o restringono il tratto in uscita di 10 cm alla volta (non sotto 10 cm), il cestino lo
  elimina. Per spostare l'inizio o la fine, trascina sulla mappa i pallini sul variatore (bianco = inizio,
  arancione = fine). Più variatori si sommano: per esempio un allargamento a metà via e poi un restringimento più
  avanti. Sulla mappa il tratto di passaggio è evidenziato in arancione. La fine può stare anche prima
  dell'inizio: in quel caso il cambio vale verso l'inizio della linea. *Larghezza costante* toglie i
  variatori; anche *Ridisegnala da capo* li toglie, perché la linea cambia.
- **Spezzare il bordo esterno**: doppio clic sul bordo esterno (linea blu) in un punto. Compare un
  quadratino: trascinalo per cambiare la larghezza in quel punto (la larghezza varia linearmente tra
  i punti). Ctrl+clic su un quadratino per toglierlo; *Larghezza costante* li toglie tutti.
- **Ignora rientranze e sporgenze fino a (m)**: i gradini delle facciate più piccoli di questo valore
  non vengono riportati sul bordo esterno, che resta regolare (predefinito 1,00 m sui bordi strada,
  0 sugli elementi). Il lato verso il muro segue sempre la facciata reale.
- **Modifica la linea**: mostra i punti chiave della linea (quelli cliccati durante il disegno;
  i vertici aggiunti da *Segui il contorno* lungo i muri non sono punti chiave e si ricalcolano):
  - **trascina** un punto per spostarlo (si aggancia a muri ed elementi);
  - **clic** su un punto e **Canc** (o *Elimina punto*) per toglierlo;
  - **doppio clic** sulla linea per inserire un nuovo punto in quel tratto;
  - **Prosegui dall'inizio** / **Prosegui dalla fine**, poi clic sulla mappa per aggiungere punti da
    quel lato (le etichette «inizio» e «fine» indicano i due capi); Esc smette di aggiungere;
  - **Salva** (o Invio) per chiudere la modifica. I punti verdi sono agganciati a un altro elemento.
  Si usa dall'elemento selezionato e anche in «Aggiungi elemento», dopo aver disegnato la linea e prima
  di premere «Disegna».
- **Aggiungi a sinistra / Aggiungi a destra** (in fondo al riquadro, per gli elementi tracciati):
  crea subito un nuovo elemento accanto a quello selezionato, lungo quanto lui, appoggiato al suo
  contorno da quel lato (dello stesso tipo, con la larghezza predefinita). Destra e sinistra sono
  riferite al verso della linea: con l'elemento selezionato la mappa mostra le frecce, «inizio» e
  «fine» e le scritte «sinistra» e «destra». Il nuovo elemento viene selezionato: cambiane tipo,
  larghezza, strisce o linea come per qualsiasi elemento. Resta agganciato: se l'elemento di partenza
  si allarga o cambia linea, lo segue. Se da quel lato c'era già un altro elemento, un avviso lo dice.
- **Ridisegnala da capo**: rifà il bordo, l'interasse o il contorno del poligono (Esc lo lascia com'era).
- **Tratto**: *Da/A* sulla linea di aggancio (solo per gli elementi riempiti tra due elementi e per
  i progetti precedenti).
- *Inverti lato* (solo per gli elementi disegnati dal bordo).
- *Elimina elemento* (o tasto Canc): con l'elemento se ne va anche la sua linea. Un elemento usato
  per riempire tra due elementi non si elimina finché non elimini prima quello; le linee che lo
  seguivano restano dov'erano.

Per ogni elemento il programma calcola **estensione** (lunghezza della linea disegnata),
**superficie**, **larghezza minima, massima e media** (misurate tra il bordo interno reale e il
bordo esterno, ogni 10 cm circa). In basso a destra ci sono i totali per tipo.

## Esportazione

- **Esporta DXF…**: DXF ASCII R12 (apribile da AutoCAD, BricsCAD, DraftSight, LibreCAD, QGIS),
  unità **metri** (`$INSUNITS = 6`), coordinate nel sistema di riferimento del GeoPackage.
  Layer: `CD_INTERASSI` (le linee d'asse degli elementi disegnati dall'interasse), `CD_SEGNALETICA` (strisce di mezzeria), `CD_BORDI_STRADA` (progetti precedenti), `CD_MARCIAPIEDE`, `CD_PISTA_CICLABILE`, `CD_CARREGGIATA`, `CD_CORSIA`,
  `CD_PARCHEGGIO`, `CD_AIUOLA`, `CD_CORSIA_CICLABILE`, `CD_SEPARATORE_GIALLO`, `CD_CORSIA_DOPPIA`, `CD_TESTI` (nome, lunghezza, superficie, larghezze) e, se richiesto,
  il contesto `CTX_MURI`, `CTX_MARCIAPIEDI`, `CTX_<livello>`. Ogni elemento è una polilinea chiusa.
- **Esporta 3D…**: vedi sotto. I fotoinserimenti si fanno dal riquadro *Fotoinserimenti* a sinistra.
- **Esporta CSV…**: misure di tutti gli elementi, apribile in Excel.
- **Salva progetto…** (`.ciclo`): bordi, elementi e ruoli dei livelli; si riapre con *Apri progetto…*
  (il GeoPackage viene cercato nel percorso originale o nella cartella del progetto).

## Esportazione 3D (glTF / GLB)

*Esporta 3D…* crea un file `.glb` (glTF 2.0, texture incluse nel file) che si apre in Blender,
Twinmotion, Unreal, Unity, SketchUp, 3ds Max e nei visualizzatori web.

**Aspetto** (materiali realistici, attivi in modo predefinito)
- Texture generate dal programma e incorporate: asfalto, marciapiede in lastre di pietra, cordolo
  in granito, ciclabile in resina rossa, vernice della segnaletica, erba, coppi.
- **Facciate**: piano terra con zoccolo in pietra, vetrine (alcune con tenda) e portoni; piani superiori
  con finestre, persiane e marcapiano; il numero di piani e di campate si adatta a ogni muro.
  Sei combinazioni di colore (ocra, rosa antico, rosso mattone, avorio, arancio, grigio caldo),
  assegnate a ogni edificio. Cornice di gronda in cima, copertura in coppi.
- **Alberi** dai punti del livello alberi (DBGT: `ALBERO`, tra i livelli consigliati del ritaglio),
  con dimensioni e forme variabili; quelli che cadono sul progetto vengono tolti.
- **Lampioni** ogni 25 m sui marciapiedi del progetto, verso la strada.
- **Simboli bici** dipinti ogni 25 m sulla pista (due, in versi opposti, se è bidirezionale).
- **Sole** (luce direzionale da sud-ovest) già impostato.
- Senza *Materiali realistici* la scena usa colori uniformi (file più leggero).

**Progetto**: corsia ciclabile in resina rossa con striscia bianca continua e simboli bici;
striscia gialla di separazione in vernice gialla; corsia doppia con tratteggio centrale;
marciapiedi e aiuole rialzati (predefinito 15 cm) con cordolo sul bordo esterno; pista
ciclabile a quota strada (o rialzata) con striscia continua verso la carreggiata e mezzeria
tratteggiata se larga almeno 2,5 m; carreggiata con mezzeria discontinua 4,5 / 7,5 m se larga almeno
5,5 m; corsie con striscia tratteggiata; parcheggi con linea esterna e stalli in linea (lunghezza scelta per ogni parcheggio, 5 m se non indicata; si possono nascondere).

**Edifici**: estrusi con l'altezza reale se nei dati c'è una colonna di altezza (DBGT: livello
`UN_VOL`, colonna `UN_VOL_AV`, ruolo **Edifici 3D**), altrimenti con l'altezza scelta nella finestra.
I volumi larghi meno di 80 cm (schegge dei dati) vengono saltati.

**Contesto**: marciapiedi esistenti (ruolo *Marciapiedi*) rialzati fuori dal progetto, piano stradale
sotto tutta l'area.

**Materiali con nome** (es. «Facciata 3 (piani)», «Cordolo (granito)», «Ciclabile (resina rossa)»):
nel programma di rendering si possono sostituire con materiali fotorealistici. Ogni elemento del
progetto è un oggetto con il suo nome.

**Viste**: *Vista ciclista* (1,6 m sulla pista) e *Vista dall'alto*.

**Coordinate**: metri; asse Y = quota, X = Est, −Z = Nord. L'origine della scena è l'origine locale
del progetto: le coordinate reali e il sistema di riferimento sono scritti nel file (asset → extras).
Le quote sono relative al piano stradale (terreno piano); le coperture sono piane.

Esempio pronto: `samples/mantova_progetto_prova.ciclo` (si apre con *Apri progetto…*) e
`samples/mantova_progetto_prova.glb` (circa 30 MB, 530.000 triangoli): una via di 155 m del centro di
Mantova con due marciapiedi, pista ciclabile bidirezionale, parcheggio in linea e carreggiata, tra
4.854 volumi di edifici con le altezze del DBGT e 1.752 alberi.

## Fotoinserimento

Il fotoinserimento disegna il progetto in prospettiva su una foto reale della via.

**Riquadro Fotoinserimenti** (a sinistra, sotto *Elementi*):
- **+ Nuovo**: scegli la foto; il fotoinserimento si apre al posto della mappa centrale (la colonna di sinistra
  resta) e la foto entra nell'elenco del progetto. **✕ Chiudi**, in alto a sinistra accanto al nome della foto, riporta alla mappa.
- Ogni riga mostra il nome della foto e lo stato (*da iniziare*, *N punti · fotocamera da calcolare*,
  *fotocamera calcolata*). Clic sulla riga per riaprirlo e continuare a modificarlo; il cestino lo toglie
  dall'elenco (la foto e i suoi file restano dove sono).
- Sulla mappa ogni fotoinserimento con la fotocamera calcolata ha un'**icona con l'occhio** nel punto di
  ripresa, con il cono verde nella direzione in cui guarda la fotocamera: clic sull'icona per aprirlo.
  *Visualizza → Icone dei fotoinserimenti* le mostra o le nasconde.
- L'elenco si salva nel progetto (`.ciclo`, con il percorso della foto anche relativo alla cartella del
  progetto). Punti, fotocamera, maschera e foto senza auto si salvano da soli accanto alla foto.

Nel fotoinserimento:

1. **Apri foto…** – una foto scattata ad altezza d'uomo (o uno screenshot di Street View solo per
   studio: per pubblicarlo valgono le condizioni d'uso di Google). Indica sotto se è stata scattata
   a piedi (1,7 m), da Street View (2,5 m) o da una finestra.
2. **Punti** – clic su un punto riconoscibile della foto, poi sullo stesso punto della mappa a destra
   (si aggancia ai vertici). Sopra la mappa: ⟲/⟳ ruotano la vista di 15° (anche Ctrl+rotella),
   *Nord in alto*, *Come la foto* (la direzione di ripresa verso l'alto) e la casella *Progetto*
   per nascondere le corsie disegnate e vedere solo la cartografia di base. Con **Maiusc+clic** sulla mappa si sceglie invece una **linea** (cordolo,
   striscia dei parcheggi, filo di facciata): sulla foto basta un suo punto qualsiasi. Servono almeno
   4 punti, meglio 6–8, distribuiti a destra e a sinistra, vicini e lontani; almeno uno deve essere
   un punto vero (non una linea), meglio due. Per uno spigolo alto (gronda) scrivi la quota, o premi
   *Quota = altezza edificio* se i dati hanno le altezze (DBGT: UN_VOL).
   Il programma calcola posizione, direzione, inclinazione e obiettivo della fotocamera e mostra
   l'errore di ogni punto (verde sotto 6 px). Un punto rosso è quasi sempre abbinato male.
3. **Verifica** – sulla foto compaiono gli edifici a terra (rosso), le gronde (giallo, se ci sono le
   altezze), i marciapiedi esistenti (azzurro) e il progetto (blu): devono combaciare.
4. **Fotoinserimento** – il progetto con texture, cordoli e segnaletica; gli edifici lo coprono dove
   serve e le ombre della foto vengono riportate sul progetto. Con **Maschera: dipingi** si colora
   ciò che deve restare davanti (auto, persone, pali, alberi); *Gomma* per correggere.
   Regola luminosità, ombre e opacità: con opacità 1 il progetto è pienamente coprente (dalla foto
   prende solo le ombre ampie, non la trama della pavimentazione).
   **▶ Anteprima** mostra subito il risultato nella vista grande (senza esportare); tieni premuto
   *◐ Originale* per confrontare prima e dopo.
5. **Rimuovi auto** – riconosce auto, furgoni, moto e persone nella foto, ne ritaglia la sagoma e,
   con la fotocamera calcolata, la appoggia a terra: le auto che toccano i tracciati disegnati
   (compresa la carrozzeria oltre le ruote visibili) vengono cancellate e la foto viene ricostruita
   al loro posto, ombra compresa. Le altre auto e le persone restano e vengono aggiunte da sole alla
   maschera, così passano davanti al progetto. Richiede circa un minuto; il risultato si salva
   accanto alla foto (`foto.senza_auto.png`). La casella *Senza auto* passa dalla foto ripulita a
   quella originale. Se il progetto cambia, premi di nuovo *Rimuovi auto*. I ritocchi fini si fanno
   con la maschera e la gomma. Serve la cartella `modelli` accanto a `CicloDraft.exe` (circa 350 MB:
   YOLOX-s per riconoscere gli oggetti, Segment Anything per le sagome, LaMa per la ricostruzione;
   tutti con licenza Apache 2.0, funzionano senza internet).
6. **Esporta immagine…** – salva l'immagine finita e, accanto, `…_livello.png` con il solo progetto
   su fondo trasparente, per il fotoritocco.

Punti, fotocamera, maschera e foto senza auto si salvano da soli accanto alla foto (`foto.fotoinserimento.json`,
`foto.maschera.png`, `foto.senza_auto.png`) e si ritrovano riaprendo la foto.

Esempio: `samples/pamedeo` – progetto di via Pier Amedeo a Mantova, foto, 8 punti di controllo
(errore medio 3,4 px), maschera sulle auto e risultato `via_pamedeo_1_progetto.png`.

Le basi cartografiche con tutte le classi in un solo livello (es. *base DBT per la pianificazione*,
colonna `codice_classe`) vengono divise in un livello per classe (`EDIFC · …`, `AC_PED · …`), così
gli edifici fanno da muri e da maschera.

## Comandi vista

Rotella = zoom · trascina con tasto destro o centrale = sposta · in assenza di selezione anche il
trascinamento col tasto sinistro sposta la vista · Esc = annulla · *Adatta vista* = tutto il disegno.

**Rotazione**: accanto ad *Adatta vista* i pulsanti **⟲ +5°** (antiorario) e **⟳ −5°** (orario) ruotano
il disegno sullo schermo, per esempio per avere la via in orizzontale. Quando il disegno è ruotato compare
un terzo pulsante con l'angolo (es. «+30° · N ↑»): premilo per tornare con il nord in alto. La rotazione
cambia solo la vista (coordinate, DXF, 3D ed esportazioni non cambiano) e si salva nel progetto.

Durante lo zoom e lo spostamento lo sfondo (edifici, marciapiedi esistenti, livelli di sfondo, alberi)
viene ingrandito come un'immagine già pronta, così la mappa resta fluida anche con molti edifici;
appena ti fermi (circa un decimo di secondo) lo sfondo viene ridisegnato nitido. Gli elementi
disegnati sono sempre nitidi.

## Note

- Usa un GeoPackage in coordinate proiettate in metri (es. EPSG:6707, 7791, 32632, 3003).
  Se il file è in gradi (EPSG:4326) viene proiettato in metri locali e il DXF non è georiferito.
- Ritaglio da riga di comando (prova): `CicloDraft --clip-test uscita.gpkg dbgt.gpkg zona.shp`
  (usa l'estensione dello shapefile come area).
- Prova automatica senza interfaccia:
  `CicloDraft --selftest samples/esempio_via.gpkg cartella_output` (crea un DXF, un CSV e un progetto).
- Versione per Mac da sorgente: `dotnet publish -c Release -r osx-arm64 --self-contained true -o pub`
  (oppure `osx-x64`), poi `python3 packaging/make_mac_app.py pub CicloDraft-mac.zip` crea l'app.
- `samples/make_sample_gpkg.py` e `samples/make_sample_curve.py` rigenerano i GeoPackage di esempio
  (Python 3, solo libreria standard). Puoi cambiare curve, larghezze ed edifici modificando i valori
  in testa allo script.

## Struttura del codice

| Cartella | Contenuto |
|---|---|
| `Geometry/Geo.cs` | polilinee: progressive, tratti, proiezione, offset a larghezza variabile, pulizia cappi |
| `Model/Project.cs` | bordi, elementi parametrici, rigenerazione, misure |
| `Model/Editor.cs` | logica di interazione (tracciamento, aggancio, scelta tratto, modifica) |
| `Controls/MapCanvas.cs` | vista planimetrica e mouse |
| `IO/` | lettura GeoPackage, scrittura DXF/CSV, file di progetto |
| `IO/SourceData.cs`, `IO/ShpReader.cs` | lettura di file grandi (indice spaziale del GeoPackage) e shapefile |
| `IO/GpkgClipper.cs` | ritaglio e scrittura del GeoPackage ridotto |
| `ClipWindow.axaml`, `Controls/ClipCanvas.cs` | finestra *Ritaglia dati* |
| `IO/Export3D.cs`, `IO/Textures.cs`, `Export3DWindow.axaml` | esportazione 3D in glTF/GLB, texture generate, finestra |
| `IO/PhotoMatch.cs`, `IO/PhotoRender.cs`, `PhotoWindow.axaml`, `Controls/PhotoCanvas.cs`, `Controls/PickMapCanvas.cs` | fotoinserimento: calcolo della fotocamera, disegno sulla foto, finestra |
| `IO/CarRemoval.cs` | rimozione delle auto: rilevamento (YOLOX), sagome (SAM), appoggio a terra e confronto con il progetto, ricostruzione (LaMa) con ONNX Runtime |
| `MainWindow.axaml` | interfaccia |
