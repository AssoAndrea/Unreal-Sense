# UnrealSense — passaggio di consegne: ottimizzare l'indicizzazione C++

Documento per l'agente (e lo sviluppatore) che continua il lavoro **sul PC aziendale**, dove si può misurare
sull'engine vero. Scritto il 05/10/2026, versione dell'estensione **0.2.17**.

## 1. Cos'è UnrealSense e qual è il problema

UnrealSense è un'estensione per Visual Studio (2022 17.14+ / 2026) per Unreal Engine, stile Rider/ReSharper:
completamento degli specifier UHT, inspection, uso nei Blueprint (parser `.uasset`), Go to Symbol/File
veloce, e **Find Usages semantico** basato su **clangd** (un `pippo->Get()` deve trovare solo il `Get` della
classe di `pippo`, non tutti i `Get`, come invece fa Visual Assist).

**Problema aperto:** l'indicizzazione completa di clangd (progetto + engine + plugin) sull'engine aziendale
è troppo lenta. L'utente vuole che sia **rapida in locale**.

## 2. Ambiente aziendale (dai log dell'utente)

| Cosa | Valore |
|---|---|
| Engine | `S:\GameB_main\Engine` — UE 5.8.3 **compilato da sorgente, modificato** |
| Progetto | `S:\GameB_main\projects\gameb\gameb.uproject` (layout "nativo": `.slnx` nella radice, `.uprojectdirs`) |
| Target UBT | `gamebEditor Win64 Development` |
| VS | Visual Studio Professional 2026 18.7 (usato) + Professional 2022 17.14 |
| MSVC | 14.44 in VS 2022; UBT con `-Compiler=VisualStudio2022` fallisce (vuole 14.50) → si usa `-Compiler=VisualStudio2026` |
| CPU / RAM | 24 core fisici, 32 logici, 63,7 GB |
| Build farm | Horde `http://horde-server1:13340/`, pool `Win-UE5`, agenti UBA (UBT ne usa fino a 64) |
| Cache UnrealSense | `%LOCALAPPDATA%\UnrealSense\Cache\gameb-83a040d9\clangd` |
| Log UnrealSense | `%LOCALAPPDATA%\UnrealSense\Logs\UnrealSense.log` (anche Output › UnrealSense in VS) |

Dimensioni: **30.201 file sorgente** nel database di UBT (≈ 29.600 dell'engine/plugin, ≈ 600 del progetto).

## 3. Vincoli dati dall'utente

- **No** alla soluzione "un PC indicizza e i colleghi copiano la cache" (l'opzione *Index folder (shareable)*
  esiste ma l'utente non la vuole come soluzione).
- Deve indicizzare **anche engine e plugin** (c'è codice cross-project nell'engine modificato e nei plugin).
- Find Usages deve restare **semantico** (niente approcci solo testuali).
- ReSharper non è installabile sul PC aziendale e **non va decompilato** (licenza JetBrains). Si può partire
  dalla documentazione pubblica.
- Ogni **nuovo download** (es. `clangd_indexing_tools`) va approvato dall'utente. clangd 23.1.0 è già in
  `%LOCALAPPDATA%\UnrealSense\clangd`.
- Usare il pool Horde per l'indicizzazione va concordato con chi lo gestisce.
- Si comunica con l'utente **in italiano**.

## 4. Come funziona oggi l'indicizzazione (pipeline)

1. **UBT** `-mode=GenerateClangDatabase` → `…\clangd\ubt\compile_commands.json` (comandi MSVC con `.rsp` annidati,
   **senza PCH**). `CompileDatabase.Generate` in `src/UnrealSense.Core/Clang/CompileDatabase.cs`.
2. **Sanitize** (stesso file): espande il `.rsp` per-file, tiene il `.rsp` condiviso del modulo come
   `@rsp/<Modulo>.Shared-<hash>.rsp` ripulito, toglie `/Yu /Fp /Fo /d2 …`, aggiunge `/w -Wno-everything
   -ferror-limit=0`, normalizza il compilatore a `cl.exe`. Output: `compile_commands.files.json` (un comando per file).
3. **BuildUnity** (stesso file): raggruppa i `.cpp` in **unità unity** (`unity/<nome>.N.cpp` = lista di
   `#include`), fino a **128 file / 1 MB**. Da 0.2.12 raggruppa **anche tra moduli** con le stesse opzioni
   (unione di `/I`, `/D` e `/FI Definitions.h` nel `.rsp` dell'unità); da 0.2.14 **ignora** nel confronto le
   opzioni irrilevanti per il parsing (warning, ottimizzazione, debug: regex `IrrelevantForIndexing`).
   Moduli `bUseUnity = false` → unità da 16 file, mai mischiati con altri moduli.
   Produce `compile_commands.project.json` (solo progetto) e `compile_commands.full.json` (tutto).
4. **clangd** (`src/UnrealSense.Core/Clang/ClangdClient.cs`, servizio `src/UnrealSense.Vsix/Services/ClangdService.cs`):
   `--background-index -j=N`, priorità bassa/normale da opzione. Due fasi: prima il database *project*, poi
   (dopo 60 s senza nuovi cicli di indicizzazione) riavvio con il *full*. L'indice di clangd è in `.cache/clangd/index`.
5. **Comandi per-file**: clangd vede solo le unità unity; quando si apre un file, `CompileCommandIndex`
   (`src/UnrealSense.Core/Clang/CompileCommandIndex.cs`) gli manda il comando esatto via
   `workspace/didChangeConfiguration` → `compilationDatabaseChanges`.
6. **Monitor**: ogni minuto il log scrive `index (fase): X% n/tot; clangd GB; free RAM` e avvisa se fermo 10 min.

Altre parti utili: `UnrealWorkspace` (carica indici in parallelo, log `Timing:`), `GoToIndex` (Go to Symbol,
indipendente da clangd), `FindUsagesService` (query clangd + Blueprint).

## 5. Misure

### 5.1 Sul PC aziendale (log dell'utente)

| Versione | Unità (progetto + engine) | Ritmo fase engine | Note |
|---|---|---|---|
| 0.2.3 (file singoli) | 29.708 | — (ore) | database da ~1,2 GB per `.rsp` inline |
| 0.2.6/0.2.7 (unity 48 file/384 KB, per modulo) | 38 + 2.536 | 5/min → 18/min | 22 thread; all'inizio priorità bassa + Unreal Editor aperto |
| 0.2.11 (128 file/1 MB, bUseUnity=false raggruppati) | 21 + 1.573 | ~12/min nei primi minuti | priorità normale |
| 0.2.13 (anche tra moduli) | 9 + 1.571 | — | i moduli dell'engine **non** si sono uniti: opzioni diverse |
| 0.2.14 (opzioni irrilevanti ignorate) | 8 + 1.571 | 16–21/min | **1.238 option sets**: le differenze erano i `@../Intermediate/.../<Modulo>.Shared.rsp` relativi non risolti (bug) |
| 0.2.15 (fix `.rsp` relativi a `Engine/Source`) | 8 + 521 | ~10/min nei primi minuti | **2 option sets** (solo `/GR`); clangd 22 GB, **CPU 16,8%** (~5 core), antivirus attivo |
| 0.2.17 (unità 384 file/8 MB, 8 thread, priorità normale) | **da misurare** (atteso ~80) | **da misurare** | |

Memoria osservata: con 22 thread clangd usa 8–19 GB (≈ 0,7–0,9 GB per thread con gruppi da 384 KB).

### 5.2 Su Gym (PC di sviluppo; progetto template, 1 modulo, 24 `.cpp` + 2 generati; indice da zero)

| Prova (1 thread) | Tempo |
|---|---|
| 26 TU singole | 93,5 s |
| 1 unità unity (tutto) | 19,0–19,7 s |
| 1 unità, senza `.gen.cpp` (codice UHT 6× più grande di quello scritto a mano) | 19,3 s |
| 1 unità, senza `.gen.cpp` + `-fdelayed-template-parsing` | 19,2 s |
| gli stessi 24 file in **4 unità** | 34,1 s |
| 6 file singoli (di 24) | 28,3 s |

**Conclusione su Gym:** il costo è quasi tutto **per unità** (parsing degli header UE, ≈ 4–5 s per unità in più
dopo la prima, la prima ≈ 14 s perché indicizza anche i simboli di tutti gli header). I corpi dei `.cpp`
(anche il codice generato) pesano poco. **Da verificare sull'engine**: i moduli dell'engine includono header più
pesanti (UnrealEd...) e hanno più codice per unità.

### 5.3 Su Lyra (PC di sviluppo: i9-14900KF 24 core/32 thread come quello aziendale; 388 sorgenti, 19 moduli; priorità normale)

**Thread** (388 file singoli, indice da zero):

| Thread | Tempo | Core occupati da clangd | Antivirus (MsMpEng) |
|---|---|---|---|
| 8 | **200 s** | 7,4 | 0,26 core |
| 12 | 311 s | 7,6 | 0,76 core |
| 16 | 230 s | 8,9 | 0,52 core |
| 24 | 275 s | 8,0 | **1,00 core (saturo)** |

Oltre ~8 thread non si guadagna nulla: il motore dell'antivirus (single-thread) satura, perché ogni apertura e ogni file
d'indice scritto passa da lui. Anche 3 processi clangd × 8 thread restano a ~8 core totali (il limite è di sistema, non
di clangd). L'utente **non accetta soluzioni basate su esclusioni dell'antivirus** (Visual Assist e ReSharper non le
richiedono) → default automatico **8 thread** (`ClangdClient.MaxAutomaticJobs`).

**Dimensione delle unità** (1 thread, tutti i 388 file):

| Unità | Tempo | RAM di picco |
|---|---|---|
| 1 (tutti i file) | **72 s** | 3,2 GB |
| 4 | 95 s | 1,9 GB |
| 13 | 266 s | 1,8 GB |
| 388 file singoli (8 thread) | 200 s | 3,0 GB |

Il costo è quasi tutto **per unità** (parsing degli header); i corpi costano ~0,15 s per file. Da 0,2.17 le unità arrivano a
**384 file / 8 MB** (con i default Lyra = 2 unità, 71 s). **Correttezza verificata**: Find Usages identico tra indice a
file singoli e indice a unità grandi (`ALyraCharacter` 130/130 riferimenti, `ULyraHealthComponent::GetHealth` 4/4).

**Riscaldamento** (indicizzare prima un'unità da sola): effetto trascurabile, non adottato.

**ReSharper** (fonti pubbliche JetBrains): indicizza prima il progetto e poi l'engine in background; usa il modello di
progetto di UBT; lo speed-up maggiore sull'engine viene dalla **cache degli header precompilati** (fino a 2×, ReSharper
C++ 2022.2); Find Usages scarta i file con il grafo degli include. Le nostre unità unity grandi sono l'equivalente della
loro cache degli header.

## 6. Problemi già risolti (da non reintrodurre)

- clangd emette **più cicli** `$/progress begin/end` (caricamento, file aperti, indicizzazione): la prima `end`
  non vuol dire "finito" → il passaggio di fase aspetta 60 s senza nuovi cicli.
- Se a **tutti** i file aperti si manda un comando per-file, clangd **non carica mai** il database e non indicizza:
  i comandi "prestati" a un header si cercano solo entro il modulo (cartella con `.Build.cs`), e per avviare
  l'indicizzazione si apre sempre un file **unity** (che non riceve mai override).
- UBT scrive i sorgenti dell'engine **relativi a `Engine/Source`**: i percorsi si confrontano risolti, non come stringhe.
- Anche i `@<Modulo>.Shared.rsp` annidati nei `.rsp` per-file sono **relativi alla cartella di lavoro** della voce
  (`Engine/Source`), non al `.rsp` che li cita: prima della 0.2.15 non venivano letti, restavano opzioni opache diverse
  per modulo e impedivano di unire i moduli (`ResolveResponseFile` in `CompileDatabase.cs`).
- La build VSSDK **non rigenerava** `obj\…\extension.vsixmanifest` quando cambiava solo la versione: 0.2.11/0.2.12 sono
  uscite con versione interna 0.2.10. `build-release.ps1` ora fa Rebuild e **verifica** la versione nel `.vsix`.
- `ConvertFrom-Json` in Windows PowerShell 5.1 restituisce l'array come un solo oggetto (usare `Out-String` + `@()`).
- Thread clangd limitati dalla RAM (`ClangdClient.AutomaticJobs`: 3/4 dei core, max 1 ogni 2,5 GB oltre 8 GB).

- Fino alla 0.2.17 il database *full* raggruppava di nuovo **tutti** i file: i file del progetto finivano in unità diverse
  da quelle della fase *project* (e i nomi dei file unity collidevano). clangd carica l'indice salvato solo per le unità
  presenti nel database corrente → dopo il passaggio alla fase engine Find Usages vedeva solo la dichiarazione (bug
  segnalato su `HUDWidgetTag`, indice al 18%). Da 0.2.18 `CompileDatabase.WriteVariants`: *full* = unità del progetto
  identiche + unità dell'engine, file `unity/p.*` e `unity/e.*` (test `FullDatabaseKeepsTheProjectUnitsUnchanged`).
- clangd gira con `--log=info` solo per intercettare "Failed to compile …, index may be incomplete" (unità indicizzate
  con errori): finiscono nel log di UnrealSense e nel conteggio del monitor.

- 0.2.18 sul PC aziendale: **tutte e 4 le unità unity del progetto** (818 file di più moduli) risultavano "Failed to
  compile" → probabile causa degli usi mancanti nei `.cpp` del progetto. Da 0.2.19 il progetto è indicizzato **un file
  per unità** (comandi esatti di UBT, `WriteVariants(groupProject: false)`); le unità grandi restano solo per
  engine e plugin. Il primo file aperto per avviare l'indicizzazione va sincronizzato **senza** comando per-file
  (`SyncDocumentAsync(pushCommand: false)`), altrimenti clangd non carica il database.
- Comando **Diagnose C++ Index Errors** (`IndexDiagnostics.cs`): per le unità segnalate con errori rilancia
  `clangd --check` su una copia che inizia con una dichiarazione (così non è un preambolo e i corpi vengono compilati)
  e scrive nel log gli errori raggruppati, con il sorgente in cui si trovano.

- Errori nelle unità che uniscono più moduli: le macro `*_API` (dllexport nel proprio `Definitions.h`, dllimport in quello
  dei dipendenti) vincevano con l'ultima definizione → errori "dllimport" e `constexpr ClassInfo` nel codice UHT
  (Lyra, unità da 388 file: **242 errori**). Da 0.2.20 ogni unità include per ultimo `unity/<unità>.api.h`, che ridefinisce
  vuote tutte le `*_API` trovate nei `Definitions.h` (`WriteApiNeutralizer`): **3 errori** residui (nomi locali duplicati
  tra `.cpp`, es. `UE_DEFINE_GAMEPLAY_TAG_STATIC` con lo stesso nome, e un `#if` in un `.gen.cpp`).

- Con il progetto file per file (0.2.20) restavano errori come `no matching constructor for initialization of
  'VectorRegister4Float' (aka 'union __m128')`: UBT passa `INCLUDE` di MSVC e il Windows SDK come `/external:I` / `/I`,
  che clang cerca **prima** dei suoi intrinseci → `xmmintrin.h` di MSVC (`__m128` union) mentre UE, vedendo `__clang__`,
  si aspetta i tipi vettoriali di clang. Da 0.2.21 `Clean()` le riscrive come `/imsvc` (cartelle di sistema "come da
  %INCLUDE%", dopo gli header interni di clang); test `MovesToolchainHeadersBehindClangIntrinsics`.

- File per file il progetto era corretto ma lento (818 TU). Da 0.2.22 il progetto torna in unità unity **per modulo**
  (niente moduli misti, max 64 file, `ProjectUnityFiles`). Lyra: **65 s** invece di 200 s, **0 unità con errori**, Find
  Usages identico al file per file su 4 simboli (328/328 riferimenti).
- Bug della 0.2.20/0.2.21: il neutralizzatore delle `*_API` svuotava anche `UE_VALIDATE_INTERNAL_API` /
  `UE_VALIDATE_EXPERIMENTAL_API` (interruttori usati in `#if`) → "expected value in expression" nel codice UHT di ogni
  unità. Ora neutralizza solo le macro definite `DLLEXPORT`/`DLLIMPORT`.

- **Errori per include mancanti** (`GEngine`, `DrawDebugSphere`, `TSubclassOf` incompleto): UBT genera il database con
  `-NoPCH` (vedi `Modes/GenerateClangDatabase.cs`), ma molto codice si appoggia al PCH per alcuni include. Da 0.2.23
  `PchResolver` replica la scelta di UBT e lo aggiunge con `/FI` dopo `Definitions.h`: `NoPCHs` → nessuno;
  `PrivatePCHHeaderFile` → quello; altrimenti il PCH condiviso più grande tra le dipendenze (dirette + pubbliche
  transitive) in ordine UnrealEd > Engine > Slate > CoreUObject > Core; i `.gen.cpp` prendono il modulo dal percorso
  `Intermediate`. Il PCH fa parte della chiave di raggruppamento. Lyra: 14 unità UnrealEd + 8 Engine, 0 errori,
  328/328 riferimenti identici; costo ~+45% sul progetto (95 s invece di 65 s).

- **0.2.24 — header map** (`HeaderMaps.cs`): per ogni `#include` clang prova ogni cartella `/I` (~500 per unità) finché
  il file esiste → centinaia di migliaia di aperture a vuoto per unità, ognuna attraverso i filtri del file system
  (18 µs l'una qui; sul PC aziendale una TU singola costava ~60 s contro ~4 s su Lyra con la stessa CPU). Ogni cartella
  `/I` è sostituita, al suo posto, da una header map clang (`hmap/*.hmap`, formato `HeaderMapTypes.h`, chiavi
  case-insensitive), le cartelle originali restano in coda come `/imsvc` (fallback per header nuovi);
  `HeaderMaps.Refresh` le aggiorna a ogni avvio. Cartelle con > 8192 file (es. `Engine/Source`) restano normali
  (l'hash di clang è una somma di caratteri, non distribuisce tabelle grandi). Lyra: 99 s → 79 s, CPU 597 → 499 s, 0 errori.
- **0.2.24 — indice vecchio congelato**: clangd (`Background.cpp`, `update` + `FileFilter`) non riscrive i simboli di un
  file invariato, salvo che il passaggio precedente avesse errori e il nuovo no. I riferimenti persi per errori nelle
  versioni precedenti restavano quindi per sempre finché l'unità nuova aveva un qualunque errore (caso `HUDWidgetTag`).
  `CompileDatabase.IndexFormat` + `DiscardOutdatedIndex` cancellano `.cache/clangd/index` una volta.
- **Richiesta dell'utente (per ora): ogni nuova versione reindicizza tutto da zero una volta**, per confrontare i tempi
  tra versioni. Il timbro dell'indice contiene `UnrealSensePackage.Version` (che `build-release.ps1` verifica uguale al
  manifest). Non togliere senza chiederlo all'utente.
- **0.2.26 — file aperti reindicizzati**: passare a clangd il comando di un file aperto (`compilationDatabaseChanges`) fa
  partire un ciclo dell'indice in background; se il file è già indicizzato costa ~2 s, ma clangd cerca l'indice per
  stringa esatta del percorso (cartella reale + nome): un percorso con maiuscole/minuscole diverse (es. da VS) non lo
  trova e rianalizza tutto il file (Lyra: 15 s, 35 s CPU; sul PC aziendale minuti e 25 GB con 22 file aperti), e i cicli
  continui impedivano il passaggio all'engine. `ClangdClient.CanonicalPath` (GetFinalPathNameByHandle + nome su disco)
  per ogni URI. La verifica dei file sospetti di Find Usages scatta solo per le unità con errori, non durante l'indice.
- **0.2.26 — errori automatici**: ogni unità segnalata con errori viene controllata subito in background (`clangd
  --check`, 2 alla volta) e gli errori, con le righe di continuazione che dicono dove sono negli header, vanno in
  `%LOCALAPPDATA%\UnrealSense\Logs\index-errors.log` man mano che escono. Il comando di menu ricontrolla tutto.
- **0.2.27 — unità sostituita (subst)**: sul PC aziendale `S:\GameB_main` è `D:\Work\GameB_main`
  (subst). clangd salva l'indice con i percorsi reali (`D:\`); i file aperti come `S:\` non venivano riconosciuti
  (rianalisi completa a ogni apertura: era questa la causa dei cicli da 25 GB). La 0.2.26 mandava a clangd i percorsi
  reali ma li restituiva come `D:\…`, e l'estensione caricava `D:\…\gameb.uproject` come un secondo progetto
  → ciclo infinito di ricaricamenti/riavvii. Ora: verso clangd percorsi reali (`CanonicalPath`), verso l'utente i
  percorsi originali (`ToViewPath`, prefissi imparati con `RememberView`); `WorkspaceService.SameFile/IsUnder`
  confrontano i percorsi reali. Test con una junction.
- **Errori 0.2.26 sul PC aziendale** (`index-errors.log`): 12 unità su 26 con `constexpr variable 'Funcs' must be
  initialized by a constant expression` in ogni `Module.*.gen.cpp` (codice UHT di registrazione delle UFUNCTION; Lyra
  non ce l'ha; causa da capire), 2 con `'dte80a.tlh' file not found` (plugin VisualStudioTools, file creato dalla build
  con `#import`), 1 `missing 'template' keyword` (RoadEditorWidget.cpp, MSVC permissivo). La 0.2.27 segue ogni tipo di
  errore lungo gli `#include` fino alla riga esatta (`exact location:` nel file) e Find Usages ricontrolla solo i file
  che contengono davvero gli errori (`IndexDiagnostics.IsUntrusted`).
- **0.2.24 — Find Usages**: i documenti C++ vengono aperti in clangd quando compaiono nell'editor (`ClangdDocumentSync`),
  così la prima ricerca non paga l'analisi degli header (~10 s segnalati); le 4 richieste partono in parallelo; i file
  che contengono il nome ma non hanno riferimenti e stanno in un'unità con errori (o indice in corso) vengono
  riaperti singolarmente e ogni occorrenza è risolta (conta solo se porta alla stessa dichiarazione). Il log riporta
  `Find Usages '<nome>': N from the index in Xs …`.

## 7. Prossimi passi consigliati (in ordine)

1. **Leggere il log della 0.2.17** (`compile_commands:`, `unity grouping: … distinct option sets`, righe `option set:`).
   Se restano molte combinazioni di opzioni, capire quali switch le separano e decidere se sono irrilevanti per il
   parsing (→ aggiungerli a `IrrelevantForIndexing`) o se vanno tenuti (`/std`, `/GR`, `/EH`, `/Zc`, `/Zp`, `/MD`, `/arch`).
2. **Misurare il costo per unità sull'engine vero** con il CLI (vedi §8):
   `sampledb` da `compile_commands.full.json` (es. 40 unità a caso) → `indexbench` con 1 thread e con N thread.
   Variare la dimensione dei gruppi (`BuildUnity(maxFiles, maxBytes)`) per trovare il punto migliore tempo/RAM.
3. **Capire cosa domina sull'engine**: parsing header vs. corpi vs. **I/O**. Da verificare in particolare:
   - **antivirus aziendale** che scansiona ogni header letto da clangd (provare con esclusioni su `S:\` e sulla cache, se consentito);
   - che tipo di disco è `S:` (locale NVMe? di rete?);
   - `clangd --log=verbose` per i tempi per unità.
4. **Distribuire con UBA/Horde** (idea dell'utente, valutata fattibile, non ancora prototipata):
   `UbaCli.exe` (in `Engine\Binaries\Win64\UnrealBuildAccelerator\x64`) esegue programmi qualsiasi in remoto con file
   system virtualizzato, legge una lista di comandi da `.yaml`, e ottiene agenti da un coordinator
   (`-coordinator=<nome> -uri=… -pool=…`, carica `UbaCoordinator<nome>.dll`). Piano:
   dividere `compile_commands.full.json` in blocchi → per ogni blocco un worker (meglio .NET Framework 4.8 o nativo,
   così non serve il runtime sugli agenti) che avvia clangd su quel blocco, aspetta la fine dell'indicizzazione e
   termina → i file `.cache/clangd/index/*.idx` tornano sull'host (stessi percorsi assoluti grazie a UBA) → si
   copiano nella cache principale → clangd locale li trova già aggiornati.
   Da verificare: clangd sotto i detour di UBA (si può provare in locale con `UbaCli … agent …`), presenza di
   `UbaCoordinatorHorde.dll` (o equivalente) e autenticazione, traffico di rete, permesso d'uso del pool.
5. Solo se l'utente lo accetta: escludere cartelle che non servono (opzione *Engine folders excluded from indexing*).

## 8. Come compilare, testare, rilasciare

```powershell
# Test (50, tutti devono passare; quelli con clangd vero sono opt-in: $env:UNREALSENSE_CLANGD_TESTS=1, usano C:\Unreal Project\Gym)
dotnet test tests/UnrealSense.Core.Tests -c Release

# CLI (net8.0)
dotnet run -c Release --project tools/UnrealSense.Cli -- resanitize "<cartella o .uproject>" [esclusioni]
dotnet run -c Release --project tools/UnrealSense.Cli -- sampledb "<cache>\clangd\compile_commands.full.json" "<outDir>" 40 [seed]
dotnet run -c Release --project tools/UnrealSense.Cli -- indexbench "<outDir>" [thread]
dotnet run -c Release --project tools/UnrealSense.Cli -- refs "<progetto>" "<file>" <riga> <colonna> [db-dir] [thread]

# Release: compila, verifica la versione nel .vsix, crea dist\UnrealSense-<ver>.vsix, Install-UnrealSense.ps1 e dist\gallery (feed atom per aggiornamenti automatici)
powershell -ExecutionPolicy Bypass -File build-release.ps1 [-GalleryFolder <cartella condivisa>]

# Installazione/aggiornamento (usa il VSIX Installer del VS più recente, sceglie il .vsix con versione più alta)
powershell -ExecutionPolicy Bypass -File dist\Install-UnrealSense.ps1
```

Regole:
- Versione da alzare in **tre** posti: `src/UnrealSense.Vsix/UnrealSense.Vsix.csproj` (`<Version>`),
  `source.extension.vsixmanifest` (`Identity Version`), `UnrealSensePackage.cs` (`InstalledProductRegistration`).
- Se cambia il formato dei database generati, alzare `CompileDatabase.FormatVersion`: al primo avvio i database
  vengono riscritti da `ubt\compile_commands.json` senza rilanciare UBT.
- La build del `.vsix` richiede il restore NuGet (`Microsoft.VSSDK.BuildTools`, `Community.VisualStudio.Toolkit.17`).
- Opzioni utente: Strumenti › Opzioni › UnrealSense › General, sezione *Find Usages (clangd)*.

