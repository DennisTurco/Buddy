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
const tags = ["ticket", "feature", "bug", "analisi", "miglioria", "refactoring", "documentazione", "supporto"];
const tag = (await tp.system.suggester(tags, tags, false, "Tag (natura del lavoro)")) ?? "feature";
-%>
- [ ] <% id %> · <% tp.date.now("YYYY-MM-DD") %> · <% titolo %> [priorita:: P2] [stima:: 1h] [tag:: <% tag %>]
  - Contesto: <% tp.file.cursor(1) %>
  - Dove: <% tp.file.cursor(3) %>
  - Fatto quando: <% tp.file.cursor(2) %>
  - Assegnato da: <% tp.file.cursor(4) %>
