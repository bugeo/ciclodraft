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
  Con **Visual Studio 2022** (17.8 o successivo, carico di lavoro *Sviluppo di applicazioni desktop .NET*) apri
  `CicloDraft.sln` e premi F5; con Rider o VS Code basta aprire la stessa soluzione o la cartella.
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

In alto ci sono i menu: **File** (Apri progetto, Apri GeoPackage, Ritaglia dati, Salva, Salva con nome),
**Modifica** (Annulla, Ripeti),
**Esporta** (DXF, 3D, CSV, PDF) e **Visualizza** (*Etichette con i nomi degli elementi*
sulla mappa, attive o no; DXF con o senza contesto, con o senza etichette). Il riquadro con la **cornice verde** è quello su cui agire adesso (stessi colori e stessa
guida di SpeedVision). A sinistra stanno *Livelli*, poi le schede **Gruppi (n) · Foto ins. (n) · Sezioni (n) · Elementi (n)** (tra
parentesi quante voci ci sono; si vede un riquadro alla volta) e in fondo *Totali per tipo*. I riquadri per
aggiungere o modificare un elemento compaiono a destra, sopra la mappa, solo quando servono: la mappa
resta sempre a tutta larghezza. Sulla mappa: **+ Aggiungi elemento** in alto al centro; in basso a destra **3D**, **Adatta vista**, la rotazione
e gli strumenti penna e testo.

1. **Avvio** – al centro della mappa: *Apri progetto…* (un `.ciclo` salvato) oppure
   *Apri GeoPackage…* (anche dal menu File, Ctrl+O apre un progetto, Ctrl+S salva). Aprendo un
   GeoPackage il primo passo sono i livelli; aprendo un progetto salvato i livelli sono già impostati
   e il loro riquadro resta chiuso (*Modifica* lo riapre).
   Sotto ai pulsanti c'è l'elenco **Aperti di recente** con gli ultimi 5 progetti e GeoPackage aperti:
   clic su una riga per riaprirlo, clic sulla **puntina** per fissarlo in alto all'elenco (resta anche se
   ne apri altri; un secondo clic lo toglie). Un file spostato o cancellato viene tolto dall'elenco.
2. **Livelli** – per ogni livello, sotto al nome, scegli il ruolo (*Muri abitazioni*, *Marciapiedi*,
   *Sfondo*, *Edifici 3D* o *Nascosto*; il programma lo propone dal nome) e la casella **Visibile**
   per vederlo o no sulla mappa (un livello non visibile conserva il suo ruolo: i muri continuano a
   servire per l'aggancio). Premi **Conferma i livelli**: il riquadro si chiude in una riga di
   riepilogo (con *Modifica* per riaprirlo) e in alto sulla mappa compare **+ Aggiungi elemento**.
   Ruoli e visibilità si salvano nel progetto.
3. **Aggiungi elemento** – il pulsante in alto sulla mappa (o **+ Nuovo** nel riquadro *Elementi*, verde
   con il riquadro incorniciato finché non c'è ancora nessun elemento) apre il riquadro a destra, sopra la
   mappa (✕ lo chiude; dopo «Salva» si chiude da solo). Per prima cosa chiede il **tipo di elemento**
   (marciapiede, pista ciclabile, carreggiata…, ognuno con il suo colore); poi come disegnarlo, **Tracciato** o
   **Poligono**:
   - **Poligono**: il contorno chiuso dell'elemento (un'aiuola, un'isola, una piazzola): clic su ogni vertice,
     doppio clic o Invio per chiuderlo. **Appena chiuso il poligono si salva** e resta selezionato. Di un poligono
     si misurano superficie e perimetro.
   - **Tracciato**: l'elemento si sviluppa lungo una linea. Compaiono tre pulsanti; un clic su uno di loro
     vale subito e il disegno parte:
     - **Bordo**: l'elemento sta da un lato della linea (es. un marciapiede lungo i muri, una ciclabile lungo
       un marciapiede). Dopo il disegno il programma propone il lato libero; *Inverti lato* se serve.
     - **Interasse**: l'elemento sta metà per parte (es. carreggiata).
     - **Riempimento**: occupa lo spazio tra due elementi già disegnati: clicca il contorno del primo,
       scegli il tratto, poi il contorno del secondo.

   Il poligono si comincia a disegnare subito; per il tracciato il disegno parte con il clic su Bordo,
   Interasse o Riempimento. Il titolo del riquadro mostra il percorso delle scelte, per esempio
   «Aggiungi › Pista ciclabile › Tracciato › Bordo»: clic su una voce per tornare a quella scelta. Se annulli il
   disegno (Esc) torni alla scelta della modalità, con lo stesso tipo. Mentre disegni compaiono
   *Aggancia* e *Segui il contorno*. Durante il disegno: clic per ogni punto,
   doppio clic o Invio per finire, Backspace per togliere l'ultimo, Esc per annullare.
   Finito il disegno premi **Salva** (o doppio clic, o Invio): l'elemento si salva subito, con la larghezza
   predefinita del tipo e dal lato proposto, e si apre *Elemento selezionato*, dove cambi larghezza, lato
   (*Inverti lato*), linea e tutto il resto. Per il riempimento l'elemento si salva appena scegli il secondo
   contorno. In entrambe le modalità:
   - *Aggancia*: il punto si attacca a vertici e lati dei muri, dei marciapiedi esistenti e degli
     **elementi già disegnati** (quadrato = vertice, X = lato).
   - *Segui il contorno*: con due clic sullo stesso edificio o elemento, la linea ne segue il profilo
     (rientranze, sporgenze, spigoli). Per un elemento tutto attorno a un edificio: clic sui vertici e
     di nuovo sul primo.
   - I punti cliccati sul contorno di un elemento restano **agganciati**: se poi quell'elemento si
     allarga, si restringe o si spezza, la linea (o il lato del poligono) lo segue.

   Elementi disponibili (nell'elenco sono in ordine alfabetico):
   - *Aiuola / verde*, *Carreggiata*, *Corsia*, *Marciapiede*, *Parcheggio*, *Pista ciclabile* (in sede propria,
     in resina rossa; rialzabile nel 3D);
   - *Corsia ciclabile* (predefinita 1,50 m): ciclabile ricavata sulla carreggiata, a quota strada,
     in rosso con striscia bianca continua verso il traffico e simboli della bici;
   - *Striscia gialla* (predefinita 0,50 m): fascia gialla di separazione, per esempio tra corsia
     ciclabile e carreggiata o tra due corsie;
   - *Ciclopedonale* (predefinita 3,00 m): percorso per bici e pedoni, **misto** oppure **separato** (striscia
     continua a metà: da una parte le bici, dall'altra i pedoni), da scegliere nella scheda *Opzioni*.

   La *Corsia doppia* non c'è più: per due corsie usa una *Carreggiata* con una striscia di mezzeria. I progetti che
   la usavano si aprono con una carreggiata al suo posto (stessa forma, mezzeria tratteggiata).

   I progetti fatti con le versioni precedenti (con i bordi strada) si aprono e si modificano come
   prima: i loro bordi restano sulla mappa e si possono usare come contorni da cui riempire.

## Modificare un elemento

Clicca l'elemento sulla mappa o nell'elenco a sinistra. Si apre il riquadro dell'elemento: il titolo dice come è
disegnato (*Poligono*, oppure *Tracciato › Bordo*, *› Interasse*, *› Riempimento*); sotto al nome c'è la tendina del
**tipo** (con il colore di ogni tipo), poi quattro schede: **Disegna** (larghezza, linea, elementi affiancati, elimina), **Opzioni** (le
caratteristiche proprie del tipo: strisce di mezzeria per carreggiate e corsie, linee degli stalli per i
parcheggi, altezza per marciapiedi e aiuole), **Variatori (n)** (i variatori di sezione, con il loro
numero) e **Info** (estensione, superficie e larghezze).

- **Tipo**: la tendina subito sotto al nome cambia il tipo dell'elemento (es. da marciapiede a pista
  ciclabile): forma e larghezza restano, cambiano colore, layer DXF, materiali 3D e segnaletica. Se il
  nome era quello automatico («Marciapiede 2») diventa quello del nuovo tipo.
- **Opzioni → Strisce di mezzeria** (carreggiata, corsia, pista e corsia ciclabile): numero di
  strisce e tipo (*tratteggiate*, *continue*, *doppie continue*). Le strisce dividono l'elemento in
  parti uguali (es. 3 strisce su 12 m = 4 corsie da 3 m) e seguono le variazioni di larghezza.
  *Automatico* torna alla regola predefinita (una mezzeria sulla carreggiata da 5,5 m in su, sulla
  pista ciclabile bidirezionale da 2,5 m). Compaiono sulla mappa, nel 3D, nel
  fotoinserimento e nel DXF (layer `CD_SEGNALETICA`).
- **Opzioni → Parcheggio**: la casella *Linee bianche degli stalli di sosta* (attiva in modo predefinito) mostra o
  nasconde le linee degli stalli e la linea lungo il lato verso la strada; *Lunghezza degli stalli* (5,00 m
  in modo predefinito) cambia la distanza tra le linee. Valgono sulla mappa, nel 3D, nel fotoinserimento
  e nel DXF (layer `CD_SEGNALETICA`) e si salvano nel progetto.
- **Opzioni → Mista / Separata** (ciclopedonale): percorso misto, oppure separato da una striscia bianca
  continua a metà (sulla mappa, nel 3D, nel fotoinserimento e nel DXF).
- **Opzioni → Altezza** (marciapiede, aiuola e ciclopedonale): quota del piano sopra la strada, 0,15 m se non indicata.
  Vale nel modello 3D, nel fotoinserimento e nel disegno delle sezioni.
- **Larghezza**: pulsanti −0,5 / −0,1 / +0,1 / +0,5, il campo numerico, oppure i tasti + e −.
- **Variatori di sezione** (elementi tracciati: carreggiata, ciclabile, corsia, marciapiede…): per far
  cambiare la larghezza in un tratto. *Aggiungi variatore di sezione*, poi clic sull'elemento dove inizia
  il cambio e dove finisce: tra i due punti la larghezza varia gradualmente e oltre la fine resta quella
  nuova. Nell'elenco ogni variatore mostra la larghezza prima e dopo (es. «V1 6,00 → 7,00 m»): **+** e **−**
  allargano o restringono il tratto in uscita di 10 cm alla volta (non sotto 10 cm), il cestino lo
  elimina. Per spostare l'inizio o la fine, trascina sulla mappa i pallini sul variatore (bianco = inizio,
  arancione = fine). Più variatori si sommano: per esempio un allargamento a metà via e poi un restringimento più
  avanti. Sulla mappa il tratto di passaggio è evidenziato in arancione. La fine può stare anche prima
  dell'inizio: in quel caso il cambio vale verso l'inizio della linea.
- **Spezzare il bordo esterno**: doppio clic sul bordo esterno (linea blu) in un punto. Compare un
  quadratino: trascinalo per cambiare la larghezza in quel punto (la larghezza varia linearmente tra
  i punti). Ctrl+clic su un quadratino per toglierlo.
- **Ignora irregolarità (m)**: i gradini delle facciate più piccoli di questo valore
  non vengono riportati sul bordo esterno, che resta regolare (predefinito 1,00 m sui bordi strada,
  0 sugli elementi). Il lato verso il muro segue sempre la facciata reale.
- **Modifica la linea**: mostra i punti chiave della linea (quelli cliccati durante il disegno;
  i vertici aggiunti da *Segui il contorno* lungo i muri non sono punti chiave e si ricalcolano):
  - **trascina** un punto per spostarlo (si aggancia a muri ed elementi);
  - **clic** su un punto e **Canc** per toglierlo;
  - **doppio clic** sulla linea per inserire un nuovo punto in quel tratto;
  - **Prosegui dall'inizio** / **Prosegui dalla fine**, poi clic sulla mappa per aggiungere punti da
    quel lato (le etichette «inizio» e «fine» indicano i due capi); Esc smette di aggiungere;
  - **Salva** (o Invio) per chiudere la modifica. I punti verdi sono agganciati a un altro elemento.
  Si usa dall'elemento selezionato e anche in «Aggiungi elemento», dopo aver disegnato la linea e prima
  di premere «Prosegui».
- **Aggiungi a sinistra / Aggiungi a destra** (in fondo al riquadro, per gli elementi tracciati):
  crea subito un nuovo elemento accanto a quello selezionato, lungo quanto lui, appoggiato al suo
  contorno da quel lato (dello stesso tipo, con la larghezza predefinita). Destra e sinistra sono
  riferite al verso della linea: con l'elemento selezionato la mappa mostra le frecce, «inizio» e
  «fine» e le scritte «sinistra» e «destra». Il nuovo elemento viene selezionato: cambiane tipo,
  larghezza, strisce o linea come per qualsiasi elemento. Resta agganciato: se l'elemento di partenza
  si allarga o cambia linea, lo segue. Se da quel lato c'era già un altro elemento, un avviso lo dice.
- **Tratto**: *Da/A* sulla linea di aggancio (solo per gli elementi riempiti tra due elementi e per
  i progetti precedenti).
- *Inverti lato* (solo per gli elementi disegnati dal bordo).
- **Converti in poligono** (elementi tracciati): l'elemento diventa un poligono con il contorno che ha in
  quel momento. Prima di convertire compare un avviso: **non si può tornare indietro**. Larghezza,
  variatori di sezione, strisce di mezzeria e lato non saranno più modificabili; restano tipo, nome e
  superficie, e con *Modifica la linea* si spostano i vertici del contorno. Gli elementi agganciati al suo
  contorno restano dove sono ma non lo seguono più; se un elemento è riempito tra questo e un altro, la
  conversione non è possibile finché non lo elimini.
- **Doppio clic su un elemento** già disegnato: lo seleziona ed entra subito in *Modifica la linea* (per un
  tracciato) o *Modifica poligono* (per un poligono).
- **Poligono**: il contorno è chiuso, quindi non ha inizio né fine (niente etichette «inizio»/«fine» né
  pulsanti *Prosegui*).
- **Punti curvi** (tracciati e poligoni): in modifica, **doppio clic su un punto** lo rende **curvo** (curva di Bézier): compaiono due
  maniglie da trascinare per cambiare la curva (sono simmetriche); i punti curvi sono tondi, gli spigoli
  quadrati. I lati del punto curvo non seguono più muri o elementi; gli altri lati restano come sono.
  Un altro doppio clic sul punto lo riporta spigolo. Le curve restano quando sposti gli altri punti, si salvano nel progetto e valgono
  per superficie, DXF, 3D e sezioni.
- **Crea elementi da aree ed elementi**: **doppio clic** su una parte vuota della mappa per sceglierla: un'area
  dove non c'è nessun elemento (il più piccolo poligono dei livelli visibili, esclusi gli edifici; altrimenti
  lo spazio libero tra edifici ed elementi disegnati). Poi **Ctrl+clic** o Maiusc+clic su altre **aree della mappa**
  (⌘+clic sul Mac); Maiusc/Ctrl+clic su un elemento disegnato invece lo sceglie per i *gruppi* per aggiungere altre parti, **anche lontane**; un altro
  Ctrl+clic la toglie. Le aree scelte sono in verde, gli elementi scelti in blu, i contorni uniti in verde
  scuro. In alto compare il pulsante verde **Crea elemento**: si sceglie solo il **tipo** dall'elenco. Le
  parti che si toccano (fessure fino a 5 cm comprese) si uniscono e **ogni gruppo diventa un elemento
  poligono** di quel tipo; gli elementi disegnati che facevano parte della scelta vengono sostituiti dai nuovi.
  Un gruppo che racchiude un vuoto non si può creare (il pulsante resta spento e la barra in basso dice
  perché). Un clic semplice o Esc annullano la scelta; Ctrl+Z annulla la creazione.
- *Elimina elemento* (o tasto Canc): con l'elemento se ne va anche la sua linea. Un elemento usato
  per riempire tra due elementi non si elimina finché non elimini prima quello; le linee che lo
  seguivano restano dov'erano.

Per ogni elemento il programma calcola **estensione** (lunghezza della linea disegnata),
**superficie**, **larghezza minima, massima e media** (misurate tra il bordo interno reale e il
bordo esterno, ogni 10 cm circa). In basso a destra ci sono i totali per tipo.

## Gruppi

Il riquadro **Gruppi** (a sinistra, sopra *Elementi*) serve a raggruppare elementi, per esempio un tratto di
strada, e a tenerne **più versioni** nello stesso progetto, da accendere e spegnere.

- **Maiusc+clic** (o Ctrl+clic) sugli **elementi disegnati** per sceglierne uno o più (un altro Maiusc+clic lo
  toglie dalla scelta). In alto, al posto di *Aggiungi elemento* e *Crea elemento*, compaiono:
  - **Crea gruppo**: un nuovo gruppo con gli elementi scelti (il nome si può scrivere subito);
  - **Aggiungi a gruppo ▾**: l'elenco dei gruppi, ciascuno con il suo colore; gli elementi scelti vanno in quello.
  Un clic semplice o Esc annullano la scelta.
- Ogni scheda della colonna di sinistra (Gruppi, Foto ins., Sezioni, Elementi) ha un riquadro **Cerca…**
  a sinistra di *+ Nuovo*: filtra l'elenco per nome (per gli elementi anche per tipo).
- **+ Nuovo** crea un gruppo vuoto con un nome predefinito (*Gruppo 1*, *Gruppo 2*…); l'elemento selezionato, se
  c'è, ci entra subito. Il nome si cambia scrivendo direttamente nella riga (Invio per confermare).
- **Gruppi** (in alto sulla mappa, accanto ad *Aggiungi elemento*, con l'occhio aperto o chiuso): mostra o nasconde i
  bordi colorati degli elementi dei gruppi; con l'occhio chiuso la mappa appare come se i gruppi non ci fossero.
- Ogni gruppo riceve in automatico un **colore** diverso dagli altri (il pallino nella riga). Gli elementi del
  gruppo hanno sulla mappa il **bordo di quel colore**.
- **(+)** nella riga attiva la **modifica del gruppo**: clicca sulla mappa gli elementi da mettere nel gruppo (un
  altro clic su un elemento lo toglie); i bordi del gruppo diventano più spessi. In alto, al posto di *Aggiungi
  elemento*, c'è solo **Salva gruppo «nome»**: premilo (o Esc) per finire. Un elemento sta in un solo gruppo.
- I pulsanti in alto sulla mappa cambiano con la modalità: normale → *Aggiungi elemento*; modifica di un gruppo →
  *Salva gruppo*; elementi scelti con Maiusc/Ctrl+clic → *Crea gruppo* e *Aggiungi a gruppo*; aree della mappa
  scelte → *Crea elemento*.
- **Clic sulla riga** = gruppo corrente (evidenziato): i nuovi elementi disegnati entrano in quel gruppo. Un
  secondo clic lo toglie.
- **Occhio**: mostra o nasconde il gruppo. Gli elementi di un gruppo nascosto non si vedono sulla mappa, non
  compaiono nell'elenco degli elementi, non si selezionano, non contano nei totali e nelle sezioni e non
  vanno nelle esportazioni (DXF, CSV, 3D, fotoinserimento). Restano però nel progetto e si salvano: gli
  elementi agganciati a loro continuano a funzionare.
- **Nuova versione** (icona delle due pagine): copia tutti gli elementi del gruppo, con le loro linee, in un
  nuovo gruppo (*Nome – versione 2*, con un altro colore), che diventa visibile e corrente; l'originale viene
  nascosto. Nella copia gli agganci tra elementi dello stesso gruppo passano alle copie (es. la striscia gialla
  segue la nuova carreggiata); quelli verso elementi fuori dal gruppo restano. Per confrontare le versioni
  accendi l'occhio dell'una e spegni quello dell'altra.
- **Cestino**: elimina il gruppo; i suoi elementi restano, senza gruppo.
- Nel riquadro dell'elemento (scheda *Disegna*) la tendina **Gruppo** sposta l'elemento in un altro gruppo o lo
  toglie da tutti (*Nessuno*).
- Gruppi, nomi, colori e visibilità si salvano nel progetto e si possono annullare con Ctrl+Z.

## Sezioni stradali

Il riquadro **Sezioni** (a sinistra) raccoglie le sezioni del progetto.

- **+ Nuova**: clic sulla mappa sul punto **A**, da un lato della strada, poi sul punto **B**, dall'altro
  lato (i punti si agganciano a muri ed elementi). Il programma disegna la strada in sezione lungo la linea A–B.
- **Sulla mappa** la sezione è una linea nera; ai due capi una freccia indica il verso in cui la si guarda.
  Clic sulla linea per aprirla; con la sezione aperta i capi A e B si trascinano per spostarla.
- **Disegno in sezione** (in basso, sopra la mappa): gli elementi tagliati dalla linea come fasce colorate
  (marciapiedi e aiuole rialzati di 15 cm, quote in altezza esagerate per leggerle), gli edifici tagliati ai
  lati, il numero di ogni elemento e le quote: la larghezza di ciascun elemento, il **totale degli elementi** e,
  se la linea arriva agli edifici, la distanza **tra gli edifici**. A destra l'elenco con numero, nome, tipo
  e larghezza di ogni elemento e la larghezza complessiva.
- **Elementi uno dentro l'altro** (es. parcheggi disegnati dentro la carreggiata): nella sezione l'elemento
  interno sta sopra e divide quello che lo contiene (carreggiata – parcheggio – carreggiata), nell'elenco è
  indicato «dentro Carreggiata 1» e la larghezza complessiva non conta due volte la stessa striscia. Per le
  sovrapposizioni parziali vale l'elemento disegnato dopo. Anche sulla mappa gli elementi più piccoli stanno
  sopra a quelli più grandi e un clic seleziona quello più piccolo.
- **Inverti verso**: guarda la sezione dall'altro lato della linea (le frecce sulla mappa si girano e nel
  disegno destra e sinistra si scambiano).
- **Figure**: sopra ogni elemento il disegno mostra chi lo usa, visto di fronte e in scala: un **pedone** sul
  marciapiede, un **ciclista** sulla pista e sulla corsia ciclabile, un'**auto** sulla carreggiata, sulle corsie e nei parcheggi, un **pedone e un ciclista** affiancati sul ciclopedonale. Sulle **aiuole** il verde dipende dalla larghezza:
  fino a 40 cm una tappezzante, fino a 1,40 m un arbusto, oltre un albero.
  Se l'elemento è diviso in corsie dalle strisce di mezzeria, c'è una figura per corsia (es. due ciclisti su una
  ciclabile bidirezionale).
- **Salva PNG**: salva la sezione come immagine su fondo bianco, con il nome, il disegno (figure e quote) e la
  legenda con numero, nome, tipo e larghezza di ogni elemento, il totale e la distanza tra gli edifici.
- Il riquadro della sezione si apre sopra i pulsanti in basso a destra (vista, penna, testo).
- Il nome si cambia nella casella in alto; il cestino elimina la sezione; ✕ (o Esc) la chiude.
- Le sezioni si salvano nel progetto.

## Esportazione

- **Esporta DXF…**: DXF ASCII R12 (apribile da AutoCAD, BricsCAD, DraftSight, LibreCAD, QGIS),
  unità **metri** (`$INSUNITS = 6`), coordinate nel sistema di riferimento del GeoPackage.
  Layer: `CD_INTERASSI` (le linee d'asse degli elementi disegnati dall'interasse), `CD_SEGNALETICA` (strisce di mezzeria), `CD_BORDI_STRADA` (progetti precedenti), `CD_MARCIAPIEDE`, `CD_PISTA_CICLABILE`, `CD_CARREGGIATA`, `CD_CORSIA`,
  `CD_PARCHEGGIO`, `CD_AIUOLA`, `CD_CORSIA_CICLABILE`, `CD_SEPARATORE_GIALLO`, `CD_TESTI` (nome, lunghezza, superficie, larghezze) e, se richiesto,
  il contesto `CTX_MURI`, `CTX_MARCIAPIEDI`, `CTX_<livello>`. Ogni elemento è una polilinea chiusa.
- **Esporta 3D…**: vedi sotto. I fotoinserimenti si fanno dal riquadro *Fotoinserimenti* a sinistra.
- **Esporta PDF…**: si apre una finestra con l'elenco dei **gruppi** (tutti, nessuno o solo alcuni, con il numero
  di elementi; gli elementi che non stanno in nessun gruppo sono la voce *Senza gruppo*; senza gruppi c'è *Tutto il progetto*), le pagine da aggiungere e il formato (**A3** o **A4**, sempre
  orizzontale). Planimetrie e sezioni sono disegnate a vettori: si ingrandiscono senza perdere definizione.
  Per ogni gruppo scelto:
  - una pagina con la **planimetria del solo gruppo** (sopra la cartografia di sfondo, con la rotazione della vista),
    in una scala d'uso (1:200, 1:250, 1:500…), con scala grafica, nord e legenda dei tipi con le superfici;
  - una pagina per ogni **sezione** che attraversa il gruppo, disegnata con i soli elementi del gruppo;
  - una pagina per ogni **fotoinserimento** in cui il gruppo si vede davvero (non troppo lontano né nascosto dagli edifici);
    sulla foto ci sono gli elementi del gruppo insieme a quelli senza gruppo, la base comune del progetto (foto senza auto
    e deformazione comprese, se ci sono).
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
striscia gialla di separazione in vernice gialla;
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

1. **La foto** – si sceglie con *+ Nuovo* nel riquadro Fotoinserimenti e si apre alla massima larghezza:
   una foto scattata ad altezza d'uomo (o uno screenshot di Street View solo per
   studio: per pubblicarlo valgono le condizioni d'uso di Google). Indica a destra se è stata scattata
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
   **Reset** (in alto, prima dei passi) elimina, dopo conferma, tutti i punti, la fotocamera calcolata e la
   deformazione e riparte dal passo 1; la maschera e la foto senza auto restano.
3. **Verifica** – sulla foto compaiono gli edifici a terra (rosso), le gronde (giallo, se ci sono le
   altezze), i marciapiedi esistenti (azzurro) e il progetto (blu): devono combaciare.
4. **Fotoinserimento** – il progetto con texture, cordoli e segnaletica; gli edifici lo coprono dove
   serve e le ombre della foto vengono riportate sul progetto. Regola luminosità, ombre e opacità: con
   opacità 1 il progetto è pienamente coprente (dalla foto prende solo le ombre ampie, non la trama della
   pavimentazione).
   **Deforma**: se il progetto non combacia perfettamente con la foto, premi *Deforma*: il pulsante
   resta verde finché la modalità è attiva. Clicca quattro vertici sulla foto (per esempio i quattro angoli
   di un tratto di marciapiede o di corsia disegnato): si crea un poligono. Trascinando i suoi vertici il
   progetto si deforma in prospettiva, in modo che quei punti finiscano dove li porti; trascinando dentro il
   poligono lo sposti tutto; cliccando fuori dal poligono ne inizi uno nuovo (la deformazione fatta resta).
   La deformazione vale per tutto il progetto: scegli un poligono ampio, attorno alla parte che ti
   interessa. Premi di nuovo *Deforma* (o Esc) per uscire: il poligono sparisce e la deformazione resta
   applicata. Le auto e le persone davanti restano sopra al progetto. La deformazione si salva con il
   fotoinserimento e vale anche per l'immagine esportata; **Annulla deformazione** riporta il progetto
   com'era.
5. **Rimuovi auto** (casella) – la prima volta che la spunti il programma riconosce auto, furgoni, moto
   e persone nella foto, ne ritaglia la sagoma e, con la fotocamera calcolata, la appoggia a terra: le auto
   che toccano i tracciati disegnati (compresa la carrozzeria oltre le ruote visibili) vengono cancellate e
   la foto viene ricostruita al loro posto, ombra compresa. Le altre auto e le persone restano e passano
   davanti al progetto. Il calcolo richiede circa un minuto e si salva accanto alla foto
   (`foto.senza_auto.png`); dopo, la casella passa subito dalla foto ripulita a quella originale. Serve la
   cartella `modelli` accanto a `CicloDraft.exe` (circa 350 MB: YOLOX-s per riconoscere gli oggetti,
   Segment Anything per le sagome, LaMa per la ricostruzione; tutti con licenza Apache 2.0, funzionano
   senza internet).
6. **Esporta immagine…** – salva l'immagine finita e, accanto, `…_livello.png` con il solo progetto
   su fondo trasparente, per il fotoritocco.

Punti, fotocamera, maschera e foto senza auto si salvano da soli accanto alla foto (`foto.fotoinserimento.json`,
`foto.maschera.png`, `foto.senza_auto.png`) e si ritrovano riaprendo la foto.

Esempio: `samples/pamedeo` – progetto di via Pier Amedeo a Mantova, foto, 8 punti di controllo
(errore medio 3,4 px), maschera sulle auto e risultato `via_pamedeo_1_progetto.png`.

Le basi cartografiche con tutte le classi in un solo livello (es. *base DBT per la pianificazione*,
colonna `codice_classe`) vengono divise in un livello per classe (`EDIFC · …`, `AC_PED · …`), così
gli edifici fanno da muri e da maschera.

## Disegni e note sulla mappa

In basso a destra sulla mappa ci sono due strumenti:

- **Penna** (matita): trascina sulla mappa per disegnare a mano libera, per esempio la linea lungo cui vuoi
  una strada o una ciclabile.
- **Testo** (T): clic sulla mappa per scrivere una nota (Invio per confermare, Maiusc+Invio per andare a capo,
  Esc per annullare). Clic su una nota esistente per modificarla; svuotata, la nota sparisce.

Con uno dei due attivo compare sopra un piccolo menu: **colore** (otto colori), **dimensione** (spessore del
tratto o grandezza del testo) e la **gomma**: clic su un disegno o su un testo per cancellarlo. Esc o un nuovo
clic sullo strumento lo spegne. Tratti e testi restano della stessa grandezza sullo schermo quando ingrandisci,
si salvano nel progetto, si annullano con Ctrl+Z e si nascondono da *Visualizza → Disegni e note*. Non vanno
nelle esportazioni.

I disegni servono anche a parlare con un assistente AI: vedi *Server MCP* (strumenti `elenco_note` e
`disegna_lungo_nota`).

## Comandi vista

Rotella = zoom · trascina con tasto destro o centrale = sposta · in assenza di selezione anche il
trascinamento col tasto sinistro sposta la vista · Esc = annulla · *Adatta vista* = tutta la piantina (livelli visibili, elementi visibili e sezioni) al massimo
ingrandimento possibile, anche con il disegno ruotato, senza finire sotto i pulsanti e i riquadri aperti sopra la mappa.

**Rotazione**: in basso a destra sulla mappa, accanto ad *Adatta vista*, i pulsanti **⟲ +5°** (antiorario) e **⟳ −5°** (orario) ruotano
il disegno sullo schermo, per esempio per avere la via in orizzontale. Quando il disegno è ruotato compare
un terzo pulsante con l'angolo (es. «+30° · N ↑»): premilo per tornare con il nord in alto. La rotazione
cambia solo la vista (coordinate, DXF, 3D ed esportazioni non cambiano) e si salva nel progetto.

**Vista 3D**: il pulsante **3D** in basso a destra, accanto ad *Adatta vista*, mostra la mappa in tre dimensioni: edifici
con le altezze dei dati, elementi del progetto rialzati con i cordoli, segnaletica, alberi e lampioni (gli stessi dell'esportazione 3D,
senza texture). Si guarda e basta: per disegnare e modificare si torna al 2D (pulsante **2D**, oppure *Aggiungi elemento*).
- **trascina** con il tasto sinistro per ruotare attorno al centro e inclinare la vista;
- **trascina** con il tasto destro o centrale per spostarti; **rotella** per avvicinarti o allontanarti;
- *Adatta vista* inquadra il progetto, **⟲ +5°** e **⟳ −5°** ruotano la vista.

Mentre ti sposti e ruoti, **gli edifici che coprono gli elementi disegnati si nascondono da soli**: al loro posto resta
l'impronta grigia a terra, e ricompaiono appena non coprono più il progetto. La scena si aggiorna da sola quando il progetto cambia.

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

## Server MCP (assistenti AI)

CicloDraft include un server [MCP](https://modelcontextprotocol.io) (Model Context Protocol): un assistente
AI come Claude può aprire un progetto, leggere elementi e misure, modificarli, disegnarne di nuovi ed
esportare DXF, CSV e 3D, usando lo stesso motore del programma. Si avvia con lo stesso eseguibile:

```
CicloDraft.exe --mcp
```

(su Mac: `CicloDraft.app/Contents/MacOS/CicloDraft --mcp`). Comunica su stdin/stdout e non apre finestre.

**Configurazione in Claude Desktop** (`claude_desktop_config.json`, menu *Impostazioni → Sviluppatore →
Modifica configurazione*):

```json
{
  "mcpServers": {
    "ciclodraft": {
      "command": "E:\\claude cowork\\ciclodraft\\CicloDraft.exe",
      "args": ["--mcp"]
    }
  }
}
```

**Strumenti**

| Strumento | Cosa fa |
|---|---|
| `apri_progetto` | apre un `.ciclo` (con il suo GeoPackage) e ne restituisce il riepilogo |
| `apri_geopackage` | inizia un progetto vuoto su un GeoPackage |
| `elenco_livelli` | livelli con geometria, numero di elementi, ruolo e visibilità |
| `elenco_elementi` | elementi con tipo, modalità, larghezza, estensione, superficie e larghezze |
| `dettaglio_elemento` | tutto di un elemento: linea, misure, variatori, strisce, stalli |
| `totali` | superfici e lunghezze per tipo |
| `disegna_elemento` | nuovo elemento da punti [E, N]: dal bordo, dall'interasse o come poligono |
| `modifica_elemento` | nome, tipo, larghezza, lato, strisce di mezzeria, stalli del parcheggio |
| `aggiungi_variatore` / `elimina_variatore` | variatori di sezione (progressive in metri lungo la linea) |
| `affianca_elemento` | nuovo elemento a destra o a sinistra, lungo come quello indicato |
| `elimina_elemento` | elimina un elemento |
| `salva_progetto` | salva il `.ciclo` (stesso file o un altro) |
| `progetti_recenti` | i progetti aperti di recente nel programma (per trovare quello su cui lavori) |
| `elenco_note` | i tratti disegnati con la penna e le note di testo: colore, punti, lunghezza, testo, elementi vicini |
| `disegna_lungo_nota` | nuovo elemento usando un tratto come linea (bordo, interasse o poligono) |
| `aggiungi_nota` / `elimina_nota` | scrive o toglie una nota sulla mappa |
| `esporta_dxf` / `esporta_csv` / `esporta_3d` | esportazioni, come dal menu Esporta |

Le coordinate sono in metri nel sistema di riferimento del GeoPackage. Le modifiche restano in memoria
finché non si chiama `salva_progetto`; il server lavora sui file. Se il progetto è aperto nel programma,
dopo il salvataggio il programma lo ricarica da solo.

**Disegnare e chiedere all'assistente.** Il modo più comodo di lavorare insieme a un assistente:

1. nel programma disegna con la **penna** la linea lungo cui vuoi l'intervento (es. in rosso lungo la via) e,
   se vuoi, scrivi una nota con il **testo**; poi **salva** il progetto (Ctrl+S);
2. nella chat scrivi cosa vuoi, per esempio: «nel mio progetto CicloDraft, lungo la linea rossa voglio una
   ciclabile bidirezionale da 2,50 m con un'aiuola da 1 m verso la carreggiata»;
3. l'assistente trova il progetto (`progetti_recenti`), legge i disegni (`elenco_note`), crea gli elementi
   (`disegna_lungo_nota`, `affianca_elemento`, `modifica_elemento`…) e salva;
4. il programma si accorge che il file è cambiato e **lo ricarica da solo**, mantenendo la vista (se avevi
   modifiche non salvate chiede prima). Gli elementi nuovi compaiono sulla mappa e si modificano come gli altri.

## Annulla e ripeti

**Annulla** (Ctrl+Z, ⌘Z sul Mac) torna indietro di un'operazione: un elemento disegnato, eliminato o
modificato (larghezza, tipo, linea, variatori, strisce, stalli…), una sezione, un fotoinserimento aggiunto o
tolto, la rotazione della vista. Si possono annullare fino a 100 operazioni. **Ripeti** (Ctrl+Y o
Ctrl+Maiusc+Z) rifà quello che è stato annullato. Mentre si sta disegnando, Annulla interrompe il disegno in
corso. Si trovano anche nel menu *Modifica*. Aprendo un altro progetto la cronologia riparte; i punti del
fotoinserimento hanno il loro *Reset*.

## Salvataggio e chiusura

*Salva* (Ctrl+S) scrive nel file `.ciclo` aperto o salvato per ultimo; la prima volta chiede dove salvarlo.
*Salva con nome…* lo salva in un altro file. Se chiudi la finestra con modifiche non salvate, il programma
lo segnala e propone **Salva**, **Non salvare** o **Annulla** (resta nel programma).

## Licenza

CicloDraft è software libero: puoi ridistribuirlo e modificarlo secondo i termini della GNU General
Public License, versione 3 o (a tua scelta) successiva. Il testo completo è nel file `LICENSE`.

## Struttura del codice

| Cartella | Contenuto |
|---|---|
| `Geometry/Geo.cs` | polilinee: progressive, tratti, proiezione, offset a larghezza variabile, pulizia cappi |
| `Model/Project.cs` | bordi, elementi parametrici, rigenerazione, misure |
| `Model/Editor.cs` | logica di interazione (tracciamento, aggancio, scelta tratto, modifica) |
| `Controls/MapCanvas.cs` | vista planimetrica e mouse |
| `Controls/View3D.cs` | vista 3D della mappa (edifici che si nascondono davanti al progetto) |
| `IO/` | lettura GeoPackage, scrittura DXF/CSV, file di progetto |
| `IO/SourceData.cs`, `IO/ShpReader.cs` | lettura di file grandi (indice spaziale del GeoPackage) e shapefile |
| `IO/GpkgClipper.cs` | ritaglio e scrittura del GeoPackage ridotto |
| `ClipWindow.axaml`, `Controls/ClipCanvas.cs` | finestra *Ritaglia dati* |
| `IO/Export3D.cs`, `IO/Textures.cs`, `Export3DWindow.axaml` | esportazione 3D in glTF/GLB, texture generate, finestra |
| `IO/PhotoMatch.cs`, `IO/PhotoRender.cs`, `PhotoWindow.axaml`, `Controls/PhotoCanvas.cs`, `Controls/PickMapCanvas.cs` | fotoinserimento: calcolo della fotocamera, disegno sulla foto, finestra |
| `IO/CarRemoval.cs` | rimozione delle auto: rilevamento (YOLOX), sagome (SAM), appoggio a terra e confronto con il progetto, ricostruzione (LaMa) con ONNX Runtime |
| `MainWindow.axaml` | interfaccia |
| `MainWindowPdf.cs`, `ExportPdfWindow.axaml` | esportazione PDF (planimetrie dei gruppi, sezioni, fotoinserimenti) |
