---
title: Cruscotto
type: cruscotto
tags:
  - flusso
updated: 2026-10-20
---

Vista automatica per me, calcolata da Dataview. **Claude non deve aprire questa nota**: legge [[Intelligenza Artificiale/Template flusso/Stato attuale]].

Gli ID portano al tracker quando la [[Intelligenza Artificiale/Template flusso/Scheda progetto]] ha `tracker_link` e `tracker_id`, altrimenti alla nota che contiene il task; le PR sono cliccabili se c'è `pr_link`.

```dataviewjs
const cartella = dv.current().file.folder;
const oggi = dv.date("today");
const pagina = (nome) => dv.page(cartella ? `${cartella}/${nome}` : nome);
const task = (nome) => pagina(nome)?.file.tasks ?? dv.array([]);
const scheda = pagina("Scheda progetto") ?? {};
const idTracker = scheda.tracker_id ? new RegExp(scheda.tracker_id) : null;

// "ID · AAAA-MM-GG · Titolo · …" → pezzi, senza i campi [chiave:: valore]
const pezzi = (t) => {
  const [id, ...resto] = t.text.replace(/\s*\[[^\]]+::[^\]]*\]/g, "").trim().split(" · ");
  const data = /^\d{4}-\d{2}-\d{2}$/.test(resto[0] ?? "") ? resto.shift() : null;
  return { id, data, titolo: resto.join(" · "), resto };
};
const id = (t) => {
  const { id } = pezzi(t);
  if (idTracker && scheda.tracker_link && idTracker.test(id)) return `[${id}](${scheda.tracker_link.replace("{id}", id)})`;
  return dv.fileLink(t.path, false, id);
};
const pr = (testo) => {
  const n = testo.match(/\d+/)?.[0];
  return n && scheda.pr_link ? `[${testo}](${scheda.pr_link.replace("{n}", n)})` : testo;
};
// Nota che contiene il task, più le note collegate nel task o nelle sue sotto-righe che stanno in una sottocartella del progetto (es. Analisi/)
const nomeNota = (p) => p.split("/").pop().replace(/\.md$/, "");
const linkNota = (p) => String(dv.fileLink(p, false, nomeNota(p)));
const note = (t) => {
  const collegate = [t, ...(t.children ?? [])].flatMap((r) => r.outlinks ?? []).map((l) => l.path)
    .filter((p) => p.endsWith(".md") && p !== t.path && p.slice(0, p.lastIndexOf("/")) !== cartella);
  return [t.path, ...new Set(collegate)].map(linkNota).join(" · ");
};
const senzaFattoQuando = (t) => {
  const riga = (t.children ?? []).find((c) => c.text.trim().startsWith("Fatto quando:"));
  return !riga || /da definire/i.test(riga.text);
};
const ordine = { P1: 1, P2: 2, P3: 3 };
const numOre = (v) => v == null ? null : typeof v === "number" ? v : v.as ? v.as("hours") : parseFloat(String(v));
const ore = (v) => numOre(v) == null ? "—" : `${numOre(v)}h`;
const numero = (testo) => { const n = parseFloat(String(testo ?? "").replace(",", ".").match(/\d+(\.\d+)?/)?.[0]); return isNaN(n) ? null : n; };
const media = (valori) => { const v = valori.filter((x) => x != null); return v.length ? Math.round(v.reduce((a, b) => a + b, 0) / v.length * 10) / 10 : "—"; };
const sezione = (titolo, colonne, righe) => {
  dv.header(2, titolo);
  if (righe.length) dv.table(colonne, righe);
  else dv.paragraph("—");
};

const todo = task("To-Do").where((t) => !t.completed);
const attesa = todo.where((t) => t.text.includes("⏸"));
const daFare = todo.where((t) => !t.text.includes("⏸"));
const sbloccati = attesa.where((t) => t.fino && t.fino <= oggi);
const senzaCriterio = todo.where(senzaFattoQuando);
const valutare = task("Da valutare").where((t) => !t.completed);
const scadute = valutare.where((t) => t.rivedere && t.rivedere <= oggi);
const controllare = task("Da controllare").where((t) => !t.completed);
const fatti = task("To-Do Fatti");
const recenti = fatti.where((t) => {
  const { data } = pezzi(t);
  return data && dv.date(data) >= oggi.minus({ days: 14 });
});

dv.header(2, "Riepilogo");
dv.table(["Stato", "Quanti"], [
  ["📋 Da fare", daFare.length],
  ["⏸ In attesa", attesa.length],
  ["🔓 In attesa ma sbloccati", sbloccati.length],
  ["❓ Senza \"Fatto quando\"", senzaCriterio.length],
  ["🤔 Da valutare", valutare.length],
  ["⏰ Valutazioni scadute", scadute.length],
  ["🔍 Da controllare", controllare.length],
  ["✅ Fatti (ultimi 14 giorni)", `${recenti.length} (${fatti.length} in tutto)`],
  ["🚫 Rifiutati", task("To-Do Rifiutati").length],
]);

sezione("📋 Da fare", ["ID", "Titolo", "Priorità", "Stima", "Aggiunto", "Note"],
  daFare.sort((t) => `${ordine[t.priorita] ?? 9}${pezzi(t).id}`)
    .map((t) => [id(t), pezzi(t).titolo, t.priorita ?? "—", ore(t.stima), pezzi(t).data ?? "—", note(t)]).array());
dv.paragraph(`**Totale stimato da fare (esclusi ⏸):** ${daFare.array().reduce((s, t) => s + (numOre(t.stima) ?? 0), 0)}h`);

sezione("❓ Senza \"Fatto quando\"", ["ID", "Titolo", "Note"],
  senzaCriterio.map((t) => [id(t), pezzi(t).titolo.replace(/^⏸\s*/, ""), note(t)]).array());

sezione("🔓 In attesa ma sbloccati", ["ID", "Titolo", "Fino al", "Note"],
  sbloccati.map((t) => [id(t), pezzi(t).titolo.replace(/^⏸\s*/, ""), t.fino, note(t)]).array());

sezione("⏰ Valutazioni scadute", ["ID", "Idea", "Aggiunta", "Rivedere", "Note"],
  scadute.map((t) => [id(t), pezzi(t).titolo.split(" — Dubbio:")[0], pezzi(t).data ?? "—", t.rivedere, note(t)]).array());

sezione("🤔 Da valutare", ["ID", "Consiglio", "Idea", "Aggiunta", "Rivedere", "Note"],
  valutare.sort((t) => t.rivedere?.toFormat?.("yyyy-MM-dd") ?? "9999").map((t) => [id(t), t.consiglio ?? "—", pezzi(t).titolo.split(" — Dubbio:")[0], pezzi(t).data ?? "—", t.rivedere ?? "—", note(t)]).array());

sezione("🔍 Da controllare", ["ID", "Titolo", "Note"],
  controllare.map((t) => [id(t), pezzi(t).titolo, note(t)]).array());

sezione("⏸ In attesa", ["ID", "Titolo", "In attesa di", "Note"],
  attesa.map((t) => {
    const [titolo, motivo] = pezzi(t).titolo.replace(/^⏸\s*/, "").split(" — In attesa di: ");
    return [id(t), titolo, motivo ?? (t.fino ? `fino al ${t.fino.toFormat("yyyy-MM-dd")}` : "—"), note(t)];
  }).array());

sezione("✅ Fatti negli ultimi 14 giorni", ["ID", "Titolo", "Data", "PR", "Costo", "🔁", "Note"],
  recenti.sort((t) => pezzi(t).data, "desc").map((t) => {
    const { resto, data } = pezzi(t);
    const trova = (re) => resto.find((p) => re.test(p));
    const titolo = resto.filter((p) => !/^(PR\b|costo:|🔁)/.test(p)).join(" · ");
    return [id(t), titolo, data, pr(trova(/^PR\b/) ?? "—"), (trova(/^costo:/) ?? "—").replace(/^costo:\s*/, ""), (trova(/^🔁/) ?? "—").replace("🔁", ""), note(t)];
  }).array());

// Costo e rifacimenti medi per tipo di task: dove conviene una skill o una Mappa codice più ricca
const perTipo = {};
for (const t of fatti.array()) {
  const { resto } = pezzi(t);
  const tipo = t.tipo ?? "(senza tipo)";
  (perTipo[tipo] ??= []).push({
    costo: numero(resto.find((p) => /^costo:/.test(p))?.replace(/^costo:\s*/, "")),
    giri: numero(resto.find((p) => /^🔁/.test(p))),
  });
}
sezione("📈 Costo e 🔁 per tipo", ["Tipo", "Task", "Costo medio", "🔁 medio"],
  Object.entries(perTipo).sort((a, b) => b[1].length - a[1].length)
    .map(([tipo, v]) => [tipo, v.length, media(v.map((x) => x.costo)), media(v.map((x) => x.giri))]));
```
