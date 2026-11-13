---
title: <% tp.file.title %>
type: test
task: 
tags:
  - test
updated: <% tp.date.now("YYYY-MM-DD") %>
---

<!--
Modello dei test manuali di un task. Lo usano /test-manuali e, solo su richiesta, /lavora e /guidami (lo creano quando viene chiesto e da lì lo aggiornano a ogni modifica); a mano: nuova nota vuota nella cartella "Test manuali/" del progetto, chiamata "Test manuali <ID>", poi "Templater: Insert template".
- Un caso = una riga con la casella: azione concreta → risultato atteso verificabile (cosa si vede, cosa c'è nel database, quale messaggio). Mai "verificare che funzioni".
- Le caselle le spunta chi esegue il test. Claude spunta solo i casi coperti da uno script di verifica-web passato, e lo scrive nell'introduzione.
- Dopo una modifica al codice: i casi già spuntati che la modifica tocca NON si tolgono; si aggiunge sotto una riga "[ ] **Da rifare dopo il AAAA-MM-GG**: …" con il nuovo risultato atteso. I casi nuovi si aggiungono nella sezione giusta.
- Una sezione che non serve si cancella (i test non hanno sezioni obbligatorie oltre a Preparazione e Regressioni).
- Cancellare questo commento quando la nota è compilata.
-->

Checklist per provare a mano <cosa fa la modifica, in una riga>. Task: [[Progetti/<Progetto>/Da controllare]] (voce <ID>). Coperti da script di test web: nessuno. Ambiente: <ambiente, database, azienda/cliente di prova>.

## 0. Preparazione

- [ ] Codice aggiornato, database pubblicato / migrazioni applicate, applicazione avviata
- [ ] Configurazione necessaria (parametri, feature flag, template…) con i valori da usare
- [ ] Dati di prova: utenti, record, casi particolari che servono ai test

Query utili (solo database locale o di test):

```sql
-- stato dei dati toccati dalla modifica
```

## 1. Funzionalità spenta (feature flag o parametro a off)

- [ ] Il comportamento è identico a prima della modifica

## 2. Caso normale

- [ ] Azione → risultato atteso

## 3. Casi limite

- [ ] Valori al limite, dati mancanti, liste vuote, più elementi

## 4. Errori e permessi

- [ ] Input non valido → messaggio atteso, nessun dato sporco
- [ ] Utente senza permesso → bloccato anche chiamando direttamente il servizio

## 5. Altri canali (API, app, servizi schedulati)

- [ ] Stesso comportamento dagli altri punti di ingresso toccati

## 6. Testi e lingue

- [ ] Testi nuovi tradotti, nessun segnaposto rimasto

## 7. Regressioni da controllare

- [ ] Funzionalità vicine che la modifica potrebbe rompere → invariate

## Rollback

Come tornare indietro se il test va male (script, parametro da spegnere).

## Domande

<!-- Dubbi emersi durante i test, da girare a chi ha chiesto la modifica -->
