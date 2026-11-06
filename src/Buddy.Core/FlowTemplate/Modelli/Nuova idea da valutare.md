<%*
// ID: chiave del tracker, oppure vuoto = prossimo_id di To-Do (che poi aumenta di uno)
const todo = tp.app.vault.getAbstractFileByPath(`${tp.file.folder(true)}/To-Do.md`);
const prossimo = (todo && tp.app.metadataCache.getFileCache(todo)?.frontmatter?.prossimo_id) || "T-001";
let id = ((await tp.system.prompt(`ID (vuoto = ${prossimo})`)) ?? "").trim();
if (!id && todo) {
  id = prossimo;
  // dopo l'inserimento: se la nota corrente è To-Do, scrivere prima la sovrascriverebbe
  tp.hooks.on_all_templates_executed(() => tp.app.fileManager.processFrontMatter(todo, (fm) => {
    fm.prossimo_id = `T-${String(parseInt(prossimo.slice(2), 10) + 1).padStart(3, "0")}`;
  }));
}
id = id || prossimo;
const titolo = (await tp.system.prompt("Titolo")) ?? "";
const dubbio = (await tp.system.prompt("Dubbio: perché non è deciso")) ?? "";
-%>
- [ ] <% id %> · <% tp.date.now("YYYY-MM-DD") %> · <% titolo %> — Dubbio: <% dubbio %> [rivedere:: <% tp.date.now("YYYY-MM-DD", 14) %>]
  - Idea: <% tp.file.cursor(1) %>
