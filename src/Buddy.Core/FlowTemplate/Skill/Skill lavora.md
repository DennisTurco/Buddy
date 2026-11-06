---
name: lavora
description: Implementa un task del flusso - lo prende da To-Do per ID, applica la modifica minima che soddisfa il suo "Fatto quando", lo fa rivedere e lo sposta in Da controllare con commit e PR pronti da copiare. Usare con /lavora ID (chiede l'ok sul piano se il lavoro è ampio) o /lavora ID autonomo (delega completa), anche per rifare un task tornato indietro dalla revisione.
---

# Lavoro su un task

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/lavora/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/lavora/SKILL.md` del repo (progetto personale). È generica: ID, tracker, comandi e regole del progetto stanno nella `Scheda progetto`.

## Due modalità

- **`/lavora ID`** (normale): se la modifica tocca più di qualche file o richiede scelte, mostra il piano e aspetta l'ok.
- **`/lavora ID autonomo`** (delega completa): non chiedere l'ok sul piano, decidi tu e vai fino alla consegna. Fermati **solo** se i requisiti sono ambigui, se serve un comando vietato o se un passo modificherebbe dati condivisi. Alla consegna riassumi le scelte fatte.

Per un task che l'utente vuole fare da sé c'è la skill `guidami`.

## 1. Prima di iniziare

1. Leggi la `Scheda progetto` (comandi vietati, tipi di task ricorrenti) e `Stato attuale`. Se c'è uno strumento che legge l'uso del piano (nell'app desktop: `get_usage`), annota la percentuale del limite di 5 ore e i token di contesto: servono a misurare il costo alla consegna.
2. Trova il task **per ID** in `To-Do`, senza leggere tutta la nota.
   - Se è ⏸ o è in `Da valutare`, fermati e chiedi conferma.
   - Se è in `Da controllare` e l'utente chiede di rifarlo: riportalo in `To-Do` con la nota correttiva, aumenta 🔁 di uno e prosegui. Se esiste già `Test manuali/Test manuali <ID>.md`, leggilo: va aggiornato (punto 2.5), non riscritto.
   - Se il "Fatto quando" manca o non è chiaro, chiedi prima di iniziare.
   - Se manca `[tag:: …]`, aggiungilo (legenda nella nota `To-Do`). Se durante il lavoro risulta sbagliato (es. un'`analisi` che è in realtà un `bug`), correggilo e dillo nel riepilogo.
   - Leggi le righe `Assegnato da:`, `Indicazioni da:`, `Chi chiedere:` e `Accordi:`, se ci sono. Se durante il lavoro l'utente racconta un accordo preso a voce, aggiungilo subito in `Accordi:` con data e persona.
3. Se il tipo di task ha una skill o una procedura nella `Scheda progetto`, usa quella.
4. Apri solo le note collegate al task, le `Decisioni` pertinenti e, nella `Mappa codice`, la sezione dell'area o del tipo del task (cercala per titolo, non leggere tutta la nota).
5. In `Stato attuale` scrivi "In lavorazione: <ID>".

## 2. Lavoro

1. Esplora il codice quanto basta per capire dove intervenire, **partendo dal campo `Dove:` del task e dalla `Mappa codice`**. Se serve cercare in molti file, delega la ricerca a un sotto-agente di esplorazione (es. Explore, con un modello economico): nel contesto torna solo l'elenco dei punti utili, non i file letti. Appena trovati, scrivi i punti nel campo `Dove:` del task, così una ripresa non riesplora.
   - **Refactoring o modifica di qualcosa di condiviso** (classe, metodo, query, componente usato in più punti): cerca nel codice **tutti** gli utilizzi, senza affidarti alla memoria, e scrivili in `Dove:`. Ognuno diventa un caso nei test manuali.
2. In modalità normale, se la modifica tocca più di qualche file o richiede scelte, mostra il piano e aspetta l'ok. In modalità autonoma prosegui e annota le scelte per il riepilogo finale.
3. Fai **la modifica più piccola che soddisfa completamente il "Fatto quando"**, seguendo le convenzioni del progetto o del team.
   - **Fuori perimetro**: se vedi altro da sistemare oltre il "Fatto quando", **non farlo**. Prepara la domanda di una riga da mandare a chi ha assegnato il task (es. "Già che ci sono sistemerei anche X, ok?"). Se invece è un miglioramento che non riguarda il task, mettilo in `Da valutare` con il tag `#proposta-team`.
4. Lancia build e test con i comandi della Scheda. Se falliscono per cause estranee alla modifica, riportalo senza correggerle.
5. **Test manuali** (sempre, per ogni task che modifica codice, configurazione o dati): il test lo fa l'utente e deve poterlo fare bene, quindi il documento va scritto con cura, non come formalità.
   - **Prima modifica**: crea `Test manuali/Test manuali <ID>.md` nella cartella del progetto, sul modello `Modelli/Nuovo test manuale` (cartella creata alla prima volta). Ricava i casi dal "Fatto quando" **e dal codice cambiato**: ogni ramo, condizione, parametro o feature flag toccato ha almeno un caso; in più funzionalità spenta, casi limite, errori e permessi, altri canali (API, app, servizi), testi e lingue, regressioni delle funzionalità vicine. Ogni caso è un'azione concreta con il risultato atteso verificabile (schermata, messaggio, riga nel database); metti in *Preparazione* configurazione, dati di prova e query utili, e in *Rollback* come tornare indietro.
   - **Ogni modifica successiva** (anche dopo la consegna, se il task torna indietro o cambiano i requisiti): aggiorna il documento **nello stesso passo** della modifica. Casi nuovi nella sezione giusta; per i casi già spuntati che la modifica tocca **non togliere la spunta**: aggiungi sotto `- [ ] **Da rifare dopo il AAAA-MM-GG**: …` con il nuovo risultato atteso; i casi non più validi si barrano (`~~…~~`) con il motivo. Aggiorna `updated`.
   - Le caselle le spunta l'utente: Claude non spunta mai un caso, salvo quelli coperti da uno script di `verifica-web` superato.
   - Se per un caso non è chiaro come testarlo (ambiente, dati, accessi), scrivilo nel caso e prepara la domanda per la persona in `Chi chiedere:` (o chiedi all'utente chi è), da mandare il giorno stesso.
6. Mai commit, push, merge, rebase, PR o comandi vietati dalla Scheda. Gli script per i database li **prepari**, non li esegui.
7. Una scelta importante (perché X e non Y) va in `Decisioni`, una riga.
8. Mai credenziali nel codice o nelle note (neanche nei test manuali: solo il riferimento della Scheda).

## 3. Revisione automatica

Se è disponibile una revisione del codice (es. il plugin code-review), usala sulle modifiche e correggi solo i problemi certi.

## 4. Consegna

1. **Dimensione**: conta file e righe cambiati (`git diff --stat`). Se superano la soglia della `Scheda progetto` (se non c'è: 15 file o 400 righe), prima di preparare commit e PR proponi di dividere il lavoro in passi rilasciabili uno alla volta, con l'ordine, e chiedi come procedere.
2. Sposta la voce da `To-Do` a `Da controllare`, nel formato della nota:
   - cosa è cambiato e file toccati;
   - come testare, in massimo 3 passi concreti (la prova rapida);
   - `Test manuali: [[…/Test manuali <ID>]]`, il link al documento completo del punto 2.5, con quanti casi ci sono e quanti sono da rifare;
   - messaggio di commit secondo la convenzione del progetto;
   - titolo e descrizione della PR (con il template del team, se c'è);
   - se il task ha una card sul tracker, il **commento per la card**, pronto da incollare e pensato per il PO: cosa è stato fatto e cosa è stato testato (e dove), in 3-5 righe, in linguaggio semplice, senza nomi di file, classi o tabelle e senza tecnicismi se si possono evitare. Se ci sono `Accordi:`, riportali nel commento, così non dipendono dalla memoria di nessuno;
   - `Da avvisare:` se `Indicazioni da:` è una persona diversa da `Assegnato da:`, nomina entrambe;
   - `Fuori perimetro:` la domanda pronta del punto 2.3, se c'è;
   - `Dimensione:` file e righe, con la proposta di divisione, se supera la soglia;
   - script da eseguire a mano, se ci sono;
   - i campi `[tag:: …]` e `[tipo:: …]` del task, se ci sono;
   - **costo**: rileggi l'uso del piano e scrivi la differenza (es. `Costo: 3% del limite 5h · contesto 85k token`); senza lo strumento scrivi `—`. È approssimato se nel frattempo lavorano altre sessioni.
   Tutti i testi che l'utente manda ad altri (commit, PR, commento per la card, domande) seguono le **Regole per i testi** qui sotto.
3. Aggiorna `Stato attuale`.
4. Se la modifica si vede nel browser, **chiedi se fare il test web** indicando il costo stimato (vedi la skill `verifica-web`).
5. Riepilogo breve.

## Regole per i testi

Valgono per ogni testo che l'utente manda a colleghi, PO o cliente: commento per la card, descrizione della PR, domande, messaggi in chat.

- Tono cordiale e neutro. Niente chiusure che possono suonare secche, come "grazie lo stesso" o "va bene, la metto".
- **Errore**: prima l'ammissione e cosa si farà per evitarlo, punto. Le cause di processo (bug di uno strumento, nomi ambigui…) non vanno nello stesso testo: vanno in `Da valutare` con il tag `#proposta-team`, da proporre in un secondo momento.
- **Disaccordo**: si scrive una volta, con un argomento. Poi si accetta o si propone un parere terzo, senza riaprire la discussione.
- Se la stessa domanda è già stata fatta a qualcun altro, dirlo ("ne ho parlato anche con X, dice che…").
- Se una bozza dell'utente viola una di queste regole, segnalalo e proponi una versione corretta.
