---
name: test-manuali
description: Scrive o aggiorna il documento dei test manuali di un task (Test manuali/Test manuali ID.md) sul modello Nuovo test manuale - casi concreti ricavati dal "Fatto quando" e dal codice cambiato, da spuntare a mano. Usare con /test-manuali ID, per un task già fatto senza documento o quando il documento va rifatto o aggiornato fuori da /lavora.
---

# Documento dei test manuali

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/test-manuali/SKILL.md` (o `.claude/skills/test-manuali/SKILL.md` in un progetto personale). È generica: comandi, ambienti, database e credenziali del progetto stanno nella `Scheda progetto`.

Fa da sola quello che `/lavora` e `/guidami` fanno al passo *Test manuali*: serve per i task fatti prima di questa regola, per quelli fatti a mano e quando il documento va rifatto o aggiornato. Il test lo fa l'utente e deve poterlo fare bene: il documento va scritto con cura, non come formalità.

## 1. Prima di iniziare

1. Leggi la `Scheda progetto` (ambienti, database di test, riferimento per le credenziali) e cerca il task **per ID** in `Da controllare`, `To-Do` e `To-Do Fatti`, senza leggere le note per intero.
2. Se esiste già `Test manuali/Test manuali <ID>.md`, leggilo: va **aggiornato** (punto 3), non riscritto.
3. Ricava cosa è cambiato:
   - dalla voce del task: "Fatto quando", "Cambiato", "File", "Dove:", "Come testare", `Accordi:`;
   - dal codice: le modifiche del ramo del task rispetto al ramo principale (`git diff` dal punto in cui il ramo si è staccato) o, se il task è chiuso, la PR o i commit indicati nella voce. Se non trovi le modifiche, chiedi dove sono invece di indovinare.
4. Se la modifica tocca qualcosa di condiviso (classe, metodo, query, componente usato in più punti), cerca nel codice **tutti** gli utilizzi: ognuno diventa un caso.

## 2. Documento nuovo

Crea `Test manuali/Test manuali <ID>.md` nella cartella del progetto (crea la cartella la prima volta) sul modello `Modelli/Nuovo test manuale`:

- `task:` con l'ID, `updated:` con la data di oggi; togli il commento del modello.
- Introduzione di una riga: cosa fa la modifica, link alla voce del task, script di `verifica-web` che coprono dei casi (o "nessuno"), ambiente di prova.
- Casi ricavati dal "Fatto quando" **e dal codice cambiato**: ogni ramo, condizione, parametro o feature flag toccato ha almeno un caso; in più funzionalità spenta, casi limite, errori e permessi, altri canali (API, app, servizi schedulati), testi e lingue, regressioni delle funzionalità vicine.
- Ogni caso è un'**azione concreta → risultato atteso verificabile** (cosa si vede, quale messaggio, quale riga nel database). Mai "verificare che funzioni".
- In *Preparazione*: configurazione, dati di prova e query utili (solo database locale o di test). In *Rollback*: come tornare indietro.
- Cancella le sezioni che non servono, tranne *Preparazione* e *Regressioni*.
- Se per un caso non è chiaro come testarlo (ambiente, dati, accessi), scrivilo nel caso e prepara la domanda per la persona in `Chi chiedere:` (o chiedi all'utente chi è).

## 3. Documento esistente

- Casi nuovi nella sezione giusta.
- I casi già spuntati che la modifica tocca **non perdono la spunta**: aggiungi sotto `- [ ] **Da rifare dopo il AAAA-MM-GG**: …` con il nuovo risultato atteso.
- I casi non più validi si barrano (`~~…~~`) con il motivo.
- Aggiorna `updated`.

## 4. Regole

- Le caselle le spunta l'utente: non spuntare mai un caso, salvo quelli coperti da uno script di `verifica-web` superato (e scrivilo nell'introduzione).
- Mai credenziali nel documento: solo il riferimento indicato nella `Scheda progetto`.
- Non modificare il codice e non eseguire script sui database: li **prepari** nel documento.

## 5. Fine

1. Nella voce del task in `Da controllare` aggiungi o aggiorna la riga `Test manuali: [[…/Test manuali <ID>]] — N casi, M da rifare`. Se il task è già in `To-Do Fatti`, aggiungi il link 🧪 alla sua riga.
2. Riepiloga in chat in 2-3 righe: quanti casi per sezione, cosa resta da chiarire e con chi.
