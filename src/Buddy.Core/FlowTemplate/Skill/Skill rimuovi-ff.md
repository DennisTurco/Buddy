---
name: rimuovi-ff
description: Rimuove un feature flag ormai definitivo, tenendo solo il ramo che resta attivo e ripulendo codice, configurazione, script e test. Usare quando un task chiede di eliminare un feature flag (es. "Rimozione FF_XXX") o con /rimuovi-ff ID nome flag.
---

# Rimozione di un feature flag

Questa nota **è** il file `SKILL.md`: per installarla, copiarla in `~/.claude/skills/rimuovi-ff/SKILL.md` (o `.claude/skills/rimuovi-ff/SKILL.md` in un progetto personale). È generica: i dettagli del progetto (nome dei flag, dove sono definiti, come si legge il valore, script DB) stanno nella sezione **Feature flag** della `Scheda progetto`.

## 1. Prima di toccare il codice

1. Leggi la sezione **Feature flag** della `Scheda progetto`. Se manca, chiedimi le informazioni e proponi di aggiungerle.
2. Stabilisci quale valore resta. Di solito è "acceso", ma **chiedimelo se non è scritto nel task**.
3. Se il flag può avere valori diversi tra ambienti o clienti, **fermati e chiedimi** come procedere.
4. Cerca **tutte** le occorrenze del nome del flag: backend, frontend, configurazioni, script SQL, test, documentazione. Mostrami l'elenco prima di modificare.

## 2. Rimozione

1. Tieni solo il ramo del valore definitivo ed elimina l'altro.
2. Elimina il codice rimasto inutilizzato a causa della rimozione: metodi, classi, query, traduzioni usate solo dal ramo eliminato. **Solo quello**: niente altri refactoring.
3. Elimina la definizione del flag (costante, enum, voce di configurazione).
4. Se il flag è anche nel database, **prepara** lo script di rimozione seguendo la `Scheda progetto`. Non eseguirlo.
5. Aggiorna o elimina i test legati al ramo eliminato.

## 3. Controlli

- Build e test passano.
- Una ricerca finale del nome del flag non trova più niente, a parte lo script DB da eseguire.
- Il comportamento finale è identico a quello con il flag nel valore definitivo.

## 4. Consegna

Sposta il task in `Da controllare` con:
- file toccati e rami eliminati;
- script DB da eseguire, se c'è;
- come testare, in massimo 3 passi;
- messaggio di commit e descrizione della PR pronti da copiare.
