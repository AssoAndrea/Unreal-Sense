# Indicizzatore C++ sperimentale (senza clangd) — stato del lavoro

Prototipo per capire se un indicizzatore scritto da zero (C#, niente clang/LLVM) può fare Find Usages
**preciso** su codice Unreal molto più velocemente di clangd. Tutto vive in `experimental/` (non toccare `src/`, `tools/`, `HANDOFF.md`).

## Regole (dall'utente, via coordinatore)

- **Nessuna logica specifica di Lyra** (niente nomi Lyra, niente casi speciali per simbolo): solo regole generali C++/Unreal.
  Le tabelle presenti sono API generiche dell'engine (Cast/NewObject/TArray/TObjectPtr...), non del progetto.
- Valutazione divisa: query Lyra (`data/oracle-queries.json`) = set di **TUNING**; set **HELD-OUT** generato con regola fissa
  (seed) da codice engine non-Lyra (+ qualche simbolo di Gym), da NON guardare mentre si correggono errori. Riportare i due separati.
- Niente download, niente esclusioni antivirus, usare tutti i core. Rapporto finale in italiano.

## Stato (aggiornato)

- [x] Progetti: `experimental/UnrealSense.Indexer` (libreria net8.0), `experimental/UnrealSense.IndexerCli` (exe `usindex`).
  La CLI usa una **copia** di `UnrealSense.Core.dll` in `experimental/lib` (solo per oracolo clangd e DeclarationScanner).
- [x] Oracolo clangd TUNING: `data/oracle-queries.json` (55 query) → `data/oracle-lyra.json` (risultati clangd, percorsi completi).
- [x] Indicizzatore completo + comandi `build`, `refs`, `score`, `dump`, `unresolved`, `oracle`, `heldout`.
- [x] Lyra solo progetto: **~1,4 s**, tuning **precision 100%, recall 99,94%** (1657/1658).
- [x] Modalità `--engine` (Engine/Source/{Runtime,Developer,Editor} + Engine/Plugins + progetto, ThirdParty e piattaforme non-Windows escluse):
  **99.960 file, 776 MB, 21,7 M righe: 26 s a freddo (19 s solo lettura file), 7,5 s a caldo**; 1,65 M simboli, 16,5 M riferimenti,
  indice 78 MB, picco memoria 8,2–8,4 GB (heap gestito ~6 GB). Tuning sull'indice engine: identico (100% / 99,94%).
  `refs` su indice engine: caricamento 0,8 s, query 30 ms.
- [ ] HELD-OUT engine: generazione in corso/da completare → `data/oracle-heldout-engine.json` (comando sotto; riprende NON automaticamente:
  se interrotto rilanciare, il file viene riscritto da capo). Poi Gym → `data/oracle-heldout-gym.json`.
- [ ] Problema trovato analizzando gli accessi non risolti nell'engine (NON dal held-out): `FString` in UE 5.8 è definita in
  `Containers/UnrealString.h.inl` (incluso più volte con `UE_STRING_CLASS` = FString/FUtf8String...), quindi `FString::Printf`,
  `.Len()`, `.IsEmpty()` ecc. risultano "member not found" (~450 k accessi di tipo 2 nell'engine). Altri: `TArray::Add/Find/...`,
  `FMath::Max/Min` (base via typedef `FPlatformMath`), builder Slate `.AutoWidth()/.Text()` (SNew/SAssignNew), delegate `CreateSP/BindLambda`.
  Da indagare: file `.inl` inclusi con macro → servirebbe "includere" il .inl nel contesto della classe con le macro definite.
- [ ] Report finale.

## Architettura (decisioni chiave)

1. **Phase 0**: lettura file (byte UTF-8; UTF-16 convertito), preprocessore solo direttive: `#if` valutati con define predefinite
   (come clangd/clang-cl: `__clang__`, `_MSC_VER`, `_WIN64`...), `/D` del compile db e i `Definitions.h` (/FI); raccoglie `#define` e
   `#include`. In modalità progetto calcola la **chiusura degli include** (cartelle `/I` del db + mappa nome-file → percorsi).
   Tabella macro globale = `#define` degli header (escluse le macro `#undef` nello stesso file, es. `UE_API`).
2. **Pass 1** (parallelo, ogni file UNA volta, header compresi): `DeclParser` sintattico → `Decl`. Macro decorative saltate
   (classificazione automatica dell'espansione), delegate `DECLARE_*DELEGATE*`, tag `UE_DECLARE/DEFINE_GAMEPLAY_TAG*`, log category,
   `DEFINE_FUNCTION` (corpi UHT). Macro che aprono una classe (`NOME(args) { ... }`) espanse nello stream (`MacroExpander.RewriteTypeMacros`).
3. **Merge**: tabella simboli globale (come gli USR di clangd); overload per firma; definizioni fuori classe agganciate per firma/arità;
   `Super`/`ThisClass`/`StaticClass()` sintetizzati; **override** calcolati (clangd: i riferimenti di un metodo includono quelli dei
   metodi base che sovrascrive, non dei fratelli).
4. **Pass 2** (parallelo): stesso parser + "sink" che risolve tipi e corpi con inferenza dei tipi (locali, parametri, membri/`this`,
   `auto`, cast, `Cast<T>`..., template con sostituzione anche nelle basi, typedef/alias template, operator->/*/[] + tabella puntatori
   e contenitori UE, range-for, lambda, overload per arità+tipi, chiamate dipendenti = tutti gli overload, nome di classe iniettato).
   Macro: argomenti risolti come codice; espansione "virtuale" (senza riferimenti) per tipizzare (`P_THIS->`, `P_GET_*`);
   `GENERATED_BODY()` → riferimenti ai tipi nominati nell'espansione del `.generated.h` (come clangd). Accessi non risolti = "usi incerti".
5. **Indice**: `%LOCALAPPDATA%\UnrealSense\OwnIndex\<progetto>\index-project.bin` / `index-engine.bin` (Brotli, struct-of-arrays).

## Dati (`experimental/data`)

- `oracle-queries.json` (tuning, scritto a mano), `oracle-lyra.json` (risposte clangd), `score-details*.txt` (mancanti/extra per query).
- `oracle-heldout-engine.json` (held-out engine, regola fissa: seed 20261005, DeclarationScanner di UnrealSense.Core su
  Engine/Source/Runtime/Engine, Plugins/Runtime/GameplayAbilities, Plugins/EnhancedInput, Plugins/Runtime/CommonUI, solo header indicizzati
  da clangd e nomi presenti nei sorgenti Lyra, quote 8 tipi/12 funzioni/5 campi/3 enumeratori, tenuti se clangd dà ≥2 riferimenti nello
  scope). Scope di confronto = file del progetto + header engine coperti dall'indice clangd (`--scope=indexed`).
- Indice clangd usato come oracolo: scratchpad `...\scratchpad\lyrascale-8-6b4757` (per Lyra, solo TU del progetto: i `.cpp`
  dell'engine NON sono indicizzati da clangd → il held-out può contare solo usi in Lyra + header engine). Gym: `%LOCALAPPDATA%\UnrealSense\Cache\Gym-8c1df914\clangd`.

## Comandi

```powershell
cd "C:\Test\VS Extension\experimental"
dotnet build -c Release UnrealSense.IndexerCli      # se usindex.exe è in uso: aggiungere -o <altra cartella>
$x = "UnrealSense.IndexerCli\bin\Release\net8.0\usindex.exe"
$s = "C:\Users\Andrea\AppData\Local\Temp\claude\C--Test-VS-Extension\1feb0c2a-1f65-4821-97d2-f8bc9b1446cc\scratchpad"
& $x build "C:\Unreal Project\LyraStarterGame"             # [--engine] [--threads=N]
& $x refs  "C:\Unreal Project\LyraStarterGame" "<file>" <line> <col> [--engine]
& $x score data\oracle-lyra.json --details=data\score-details.txt [--engine]
& $x score data\oracle-heldout-engine.json --scope=indexed [--engine]
& $x dump  "C:\Unreal Project\LyraStarterGame" "<file>" [fromLine] [toLine] [--engine]
& $x unresolved "C:\Unreal Project\LyraStarterGame" 2 30 --engine     # accessi a membri non risolti (1=ricevente ignoto, 2=membro non trovato, 3=nome)
# oracolo tuning
& $x oracle data\oracle-queries.json "$s\lyrascale-8-6b4757" data\oracle-lyra.json 8
# held-out engine (≈13 s a query clangd)
$e = "C:\Program Files\Epic Games\UE_5.8\Engine"
& $x heldout "C:\Unreal Project\LyraStarterGame" "$s\lyrascale-8-6b4757" data\oracle-heldout-engine.json 20261005 8 12 5 3 160 "$e\Source\Runtime\Engine" "$e\Plugins\Runtime\GameplayAbilities" "$e\Plugins\EnhancedInput" "$e\Plugins\Runtime\CommonUI"
# held-out Gym (da fare)
& $x heldout "C:\Unreal Project\Gym" "$env:LOCALAPPDATA\UnrealSense\Cache\Gym-8c1df914\clangd" data\oracle-heldout-gym.json 20261005 2 2 1 0 40 "C:\Unreal Project\Gym\Source"
```

## Prossimi passi

1. Finire held-out engine + Gym; `build` Gym (`usindex build "C:\Unreal Project\Gym"`), poi score held-out UNA volta e registrare i numeri.
2. Migliorare (solo regole generali) i casi "member not found" dell'engine: FString in `.inl` con `UE_STRING_CLASS`, basi via typedef,
   builder Slate, delegate. Ricontrollare il tuning dopo ogni modifica.
3. Ridurre memoria in modalità engine (8 GB): non tenere i byte dei file, compattare Decl/RefRec.
4. Report finale (italiano) con tabelle.
