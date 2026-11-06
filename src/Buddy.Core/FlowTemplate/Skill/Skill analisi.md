---
name: analisi
description: Scrive o aggiorna l'analisi di implementazione di una funzionalità nella cartella Analisi del progetto nel vault, seguendo il modello comune (contesto, requisiti, situazione attuale, soluzione, alternative, impatti, rischi, punti aperti, piano di lavoro, verifica). Legge il codice ma non lo modifica. Usare con /analisi argomento, /analisi ID per un task, o /analisi aggiorna nome-analisi.
---

# Analisi di implementazione

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/analisi/SKILL.md` (progetto in team, flusso privato) o in `.claude/skills/analisi/SKILL.md` del repo (progetto personale). È generica: i percorsi del vault e le regole del progetto stanno nella `Scheda progetto`.

Serve **prima** di implementare qualcosa di non banale: capire come funziona oggi, proporre una soluzione e lasciare un documento da rileggere o discutere con il team. **Sul codice è di sola lettura**: niente modifiche, niente commit, niente comandi vietati dalla Scheda.

## Tre modi d'uso

- `/analisi argomento` — nuova analisi su una funzionalità descritta a parole.
- `/analisi ID` — nuova analisi per un task: il contesto e il "Fatto quando" del task diventano obiettivo e requisiti.
- `/analisi aggiorna nome` — riprende un'analisi esistente (punti aperti risolti, nuove informazioni, cambio di stato).

## 1. Prima di iniziare

1. Leggi la `Scheda progetto` e `Stato attuale`.
2. Elenca la cartella `Analisi/` del progetto: se esiste già un'analisi sullo stesso argomento, proponi di aggiornarla invece di crearne una nuova.
3. Con un ID: cerca il task in `To-Do` (e in `Da valutare`) **con una ricerca**, senza leggere la nota intera.
4. Cerca l'argomento in `Decisioni`, `Glossario`, `To-Do Rifiutati` e nei titoli di sezione della `Mappa codice`: la proposta non deve contraddire una decisione né riproporre un'idea scartata senza dirlo.
5. Se obiettivo o perimetro non sono chiari, fai **al massimo 3 domande** prima di esplorare. Un termine che non è nel `Glossario` e non si capisce dal codice: chiedi, non indovinare.

## 2. Esplorazione

- Parti dalla `Mappa codice` e dal campo `Dove:` del task, se c'è.
- Per ricerche ampie (molti file o convenzioni di nome) usa un sotto-agente di esplorazione e tieni solo le conclusioni.
- Leggi le istruzioni del repo che valgono per le aree toccate (convenzioni, feature flag, testi multilingua, sicurezza): la soluzione proposta deve rispettarle e la sezione *Impatti* deve dirlo.
- Annota per ogni fatto **file e riga**. Quello che non hai verificato va scritto come ipotesi, con "Da verificare".

## 3. Stesura

1. Crea la nota in `<cartella del progetto>/Analisi/Analisi <argomento>.md` partendo dal modello `Modelli/Nuova analisi.md` del template base (percorso nella Scheda): sostituisci i segnaposto Templater (`<% … %>`) con titolo e data di oggi e togli il commento iniziale.
2. Compila **tutte** le sezioni, nell'ordine del modello. Una sezione che non serve resta, con "Non applicabile: motivo".
3. Regole di scrittura:
   - italiano chiaro, frasi brevi; nomi di file, classi e tabelle tra backtick;
   - *In breve* leggibile da solo: chi legge solo quello deve capire problema, proposta e rischio;
   - requisiti verificabili (ognuno si può spuntare con un test della sezione 10);
   - almeno un'alternativa vera nella sezione 5, con il motivo per cui è stata scartata;
   - piano di lavoro con passi da massimo un giorno ciascuno e totale delle ore;
   - niente credenziali, stringhe di connessione o chiavi: solo il riferimento indicato nella Scheda.
4. Proprietà: `stato: bozza`, `task:` con l'ID se c'è, `tags` con l'argomento, `created` e `updated` alla data di oggi.

Con `/analisi aggiorna`: modifica solo le sezioni toccate, spunta i punti aperti risolti scrivendo la risposta, aggiorna `stato` e `updated` e aggiungi una riga in *Storico*. Se la soluzione cambia del tutto, segna la vecchia come `superata` e crea una nuova analisi che la cita.

## 4. Collegamenti

- Aggiungi una riga in `00 Indice`, sezione *Analisi*: `[[percorso/Analisi …]] — cosa analizza (stato)`. Con un aggiornamento, correggi lo stato nella riga esistente.
- Con un ID: aggiungi al task `- Analisi: [[percorso/Analisi …]]` sotto `Contesto` e completa `Dove:` con i file principali trovati.
- Le scelte già prese con l'utente durante l'analisi vanno anche in `Decisioni`; i termini nuovi, dopo la conferma, nel `Glossario`.
- **Non creare task da solo**: alla fine chiedi se portare i passi del piano in `To-do Appunti` (poi li smista `/smista`).

## 5. Riepilogo in chat

Massimo 6 righe: link alla nota, la proposta in una frase, stima e rischio, i punti aperti che servono dall'utente. Il dettaglio sta nella nota, non nella chat.
