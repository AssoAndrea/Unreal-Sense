# UnrealSense — guida rapida

UnrealSense aggiunge a Visual Studio (2022 e 2026) un aiuto pensato per i progetti Unreal: ti dice **dove le tue
classi C++ sono usate nei Blueprint**, ti suggerisce gli specifier di `UPROPERTY`/`UFUNCTION` e ti avvisa degli errori
tipici di Unreal mentre scrivi. Funziona anche con l'editor di Unreal chiuso.

## Installare e aggiornare

1. Chiudi Visual Studio.
2. Doppio clic sul file `UnrealSense-<versione>.vsix`.
3. Nella finestra che si apre spunta le versioni di Visual Studio in cui la vuoi (2022, 2026) e premi **Install**.

Per aggiornare si fa la stessa cosa con il file nuovo: non serve disinstallare.

Se nell'elenco non compare la tua versione di Visual Studio, metti `Install-UnrealSense.ps1` nella stessa cartella del
`.vsix` e fai clic destro sullo script › **Esegui con PowerShell**: usa l'installer della versione più recente.

## Primo avvio

Apri la soluzione del tuo progetto come fai di solito. UnrealSense riconosce il progetto da solo e in pochi secondi
legge le classi C++ e i Blueprint (la prima volta può volerci un po' di più). Da lì in poi tutto funziona in automatico.

## Cosa trovi nel codice

### Quanti Blueprint usano una classe

Accanto a classi, funzioni e proprietà esposte ai Blueprint compare una scritta grigia, per esempio
`12 derived Blueprints` o `3 Blueprint usages`. **Cliccala** per vedere l'elenco dei Blueprint.

Il conteggio include anche i Blueprint che derivano da altri Blueprint o da classi C++ figlie (indicati con "via …").

### Cercare gli usi nei Blueprint

Metti il cursore sul nome di una classe, funzione o proprietà e premi **Alt+Shift+B** (oppure clic destro ›
**Find Blueprint Usages**). Si apre la finestra **Blueprint Usages** con l'elenco: doppio clic su una riga per
mostrare il file dell'asset in Esplora file.

### Informazioni al passaggio del mouse

Passa il mouse su uno specifier (es. `BlueprintReadWrite`) per leggerne la spiegazione, o sul nome di una classe/
funzione/proprietà per vedere dove è usata nei Blueprint.

### Suggerimenti mentre scrivi

Dentro `UPROPERTY(...)`, `UFUNCTION(...)`, `UCLASS(...)` ecc. Visual Studio ti propone gli specifier validi per la tua
versione di Unreal, con la spiegazione. Funziona anche per i valori, per esempio le `Category` già usate nel progetto
o la funzione da usare in `ReplicatedUsing`.

### Avvisi e correzioni rapide

Gli errori tipici di Unreal vengono sottolineati mentre scrivi, prima di compilare: include `.generated.h` mancante o
sbagliato, `GENERATED_BODY()` mancante, prefissi A/U/F/E/I sbagliati, `_Implementation` mancante, e altri. Quando
compare la **lampadina** puoi applicare la correzione con un clic (per esempio crea il corpo di `_Implementation`
nel `.cpp`). Gli stessi avvisi sono anche nell'Elenco errori.

### Passare da dichiarazione a implementazione

Su una `UFUNCTION` premi **Alt+Shift+I** (o clic destro › **Go to Declaration / _Implementation**) per saltare
alla sua implementazione, a `_Implementation` o `_Validate`, e ritorno.

### Unreal Explorer

Menu **Extensions › UnrealSense › Unreal Explorer** (o **View › Other Windows**). Mostra i moduli del progetto, i
plugin, i Blueprint raggruppati per classe C++ di origine e i file di configurazione, con una casella di ricerca.

## Impostazioni

**Tools › Options › UnrealSense**. Le più utili:

- **Show Blueprint usage hints**: accende/spegne le scritte grigie nel codice.
- **Show hints for unused Blueprint API**: mostra anche "no Blueprint usages" sulle funzioni e proprietà esposte ai
  Blueprint ma mai usate (utile per fare pulizia).
- **Unreal project**: da impostare solo se UnrealSense apre il progetto sbagliato (capita con soluzioni che ne
  contengono più di uno).

## Se qualcosa non va

- **Non vedo i contatori**: aspetta qualche secondo dopo l'apertura della soluzione. Se hai appena salvato dei
  Blueprint in Unreal, usa **Extensions › UnrealSense › Rebuild UnrealSense Index**.
- **Un contatore sembra sbagliato**: annota la classe e un Blueprint che la usa, e manda il file di log (sotto).
- **"Trova tutti i riferimenti" di Visual Studio non funziona più**: se nel menu Extensions › UnrealSense compare
  **Restore Visual Studio Indexing**, eseguilo e riavvia Visual Studio.
- **Log da allegare a una segnalazione**: `%LOCALAPPDATA%\UnrealSense\Logs\UnrealSense.log` (incolla il percorso
  nella barra di Esplora file). È anche visibile in Visual Studio in **Output › UnrealSense**.
