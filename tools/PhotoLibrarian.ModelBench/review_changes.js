"use strict";
(() => {
  const data = JSON.parse(document.getElementById("review-data").textContent);
  const byId = id => document.getElementById(id);
  const decisions = data.items.map(item => item.initialDecision);
  const storageKey = "PhotoLibrarian.ModelBench.Changes:" + data.fingerprint;
  let currentPhoto = null;
  let shownItems = [];
  let overrides = {};
  let storageUsable = true;

  function error(message) {
    byId("error").textContent = message;
    byId("error").hidden = false;
  }
  try {
    const saved = localStorage.getItem(storageKey);
    if (saved !== null) {
      const parsed = JSON.parse(saved);
      if (!parsed || parsed.fingerprint !== data.fingerprint || !parsed.decisions ||
          typeof parsed.decisions !== "object" || Array.isArray(parsed.decisions)) {
        throw new Error("Unrecognized saved review format.");
      }
      for (const [key, value] of Object.entries(parsed.decisions)) {
        if (!/^(0|[1-9]\d*)$/.test(key) || !data.items[Number(key)] ||
            ![null, "correct", "incorrect"].includes(value)) {
          throw new Error("Invalid saved tag decision.");
        }
      }
      overrides = parsed.decisions;
      for (const [key, value] of Object.entries(overrides)) decisions[Number(key)] = value;
    }
  } catch (problem) {
    storageUsable = false;
    error("Saved browser review could not be loaded. Previous CSV decisions remain available. " +
      "Browser saving is disabled to avoid overwriting saved work; export your changes. " + problem.message);
  }

  function save() {
    if (!storageUsable) return;
    try {
      localStorage.setItem(storageKey, JSON.stringify({fingerprint: data.fingerprint, decisions: overrides}));
    } catch (problem) {
      error("Your edits are retained in this page but could not be saved in the browser. Export before closing. " + problem.message);
    }
  }
  const pending = photo => photo.items.filter(id => decisions[id] === null);
  const showReviewed = () => byId("show-reviewed").checked;

  function updateProgress() {
    const remaining = data.items.filter(item => decisions[item.id] === null).length;
    const photosLeft = data.photos.filter(photo => pending(photo).length > 0).length;
    byId("progress").textContent =
      `${remaining} tags on ${photosLeft} photos left to review. ` +
      `${data.summary.reusedUniqueDecisions} prior decisions reused; ${data.summary.conflictingUniqueTags} prior conflicts flagged.`;
    byId("complete").hidden = remaining !== 0;
    const select = byId("photo-select");
    select.replaceChildren();
    const visible = data.photos.filter(photo => showReviewed() || pending(photo).length > 0 || photo.id === currentPhoto);
    for (const photo of visible) {
      const option = document.createElement("option");
      option.value = String(photo.id);
      option.textContent = `${photo.image} (${pending(photo).length} pending)`;
      select.append(option);
    }
    if (currentPhoto !== null) select.value = String(currentPhoto);
    select.disabled = visible.length === 0;
    byId("previous").disabled = byId("next").disabled = visible.length <= 1;
    if (currentPhoto !== null) {
      const photo = data.photos[currentPhoto];
      byId("photo-progress").textContent = `${pending(photo).length} pending; ${photo.items.length} distinct suggested tags on this photo.`;
      byId("reject-remaining").disabled = pending(photo).length === 0;
    }
  }

  function applyDecision(id, value) {
    decisions[id] = value;
    overrides[String(id)] = value;
    save();
  }

  function renderTags() {
    const container = byId("tags");
    container.replaceChildren();
    for (const id of shownItems) {
      const item = data.items[id];
      const card = document.createElement("article");
      card.className = "tag";
      card.dataset.item = String(id);
      const heading = document.createElement("h3");
      heading.textContent = item.label;
      card.append(heading);
      const status = document.createElement("p");
      status.className = "decision";
      status.textContent = decisions[id] === null
        ? (item.reason === "conflict" ? "Pending: prior reviews disagree" : "Pending: not previously reviewed")
        : decisions[id] === "correct" ? "Correct" : "Rejected";
      card.append(status);
      const occurrences = document.createElement("p");
      occurrences.className = "muted";
      occurrences.textContent = item.occurrences.map(p =>
        `${p.model} #${p.rank}: ${p.scoreKind === "binary" ? "detected" : `${Number(p.score).toPrecision(4)} ${p.scoreKind}`}`).join(" | ");
      card.append(occurrences);
      if (item.reason === "conflict") {
        const evidence = document.createElement("p");
        evidence.className = "muted";
        evidence.textContent = item.evidence.map(p =>
          `${p.model}: ${p.correct ? "correct" : "rejected"} (${p.source})`).join("; ");
        card.append(evidence);
      }
      const actions = document.createElement("div");
      actions.className = "actions";
      for (const [label, value] of [["Correct", "correct"], ["Reject", "incorrect"], ["Not sure", null]]) {
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = label;
        button.setAttribute("aria-label", `${label}: ${item.label}`);
        button.setAttribute("aria-pressed", String(decisions[id] === value));
        button.addEventListener("click", () => {
          applyDecision(id, value);
          renderTags();
          updateProgress();
          const replacement = byId("tags").querySelector(`[data-item="${id}"]`);
          replacement.querySelectorAll("button")[value === "correct" ? 0 : value === "incorrect" ? 1 : 2].focus();
        });
        actions.append(button);
      }
      card.append(actions);
      container.append(card);
    }
  }

  function renderPhoto(id) {
    currentPhoto = id;
    byId("workspace").hidden = id === null;
    if (id !== null) {
      const photo = data.photos[id];
      byId("photo-name").textContent = photo.image;
      byId("photo").src = photo.thumbnail;
      byId("photo").alt = photo.image;
      shownItems = photo.items.filter(item => showReviewed() || decisions[item] === null);
      renderTags();
    }
    updateProgress();
  }

  function navigate(direction) {
    const options = [...byId("photo-select").options].map(option => Number(option.value));
    if (options.length === 0) return;
    const index = options.indexOf(currentPhoto);
    renderPhoto(options[(index + direction + options.length) % options.length]);
  }

  byId("identity-warning").textContent = data.identityWarning;
  byId("previous").addEventListener("click", () => navigate(-1));
  byId("next").addEventListener("click", () => navigate(1));
  byId("photo-select").addEventListener("change", event => renderPhoto(Number(event.target.value)));
  byId("show-reviewed").addEventListener("change", () => {
    const current = currentPhoto === null ? null : data.photos[currentPhoto];
    const photo = current && (showReviewed() || pending(current).length > 0)
      ? current : data.photos.find(p => showReviewed() || pending(p).length > 0);
    renderPhoto(photo ? photo.id : null);
  });
  byId("reject-remaining").addEventListener("click", () => {
    if (currentPhoto === null) return;
    const ids = pending(data.photos[currentPhoto]);
    if (ids.length === 0 || !confirm(`Reject all ${ids.length} still-pending tags on this photo?`)) return;
    for (const id of ids) applyDecision(id, "incorrect");
    renderTags();
    updateProgress();
  });
  byId("export").addEventListener("click", () => {
    save();
    const rows = [data.exportFields];
    for (const row of data.rows) {
      const reviewed = row.predictions.filter(p => decisions[p.item] !== null).map(p => p.rank);
      const correct = row.predictions.filter(p => decisions[p.item] === "correct").map(p => p.rank);
      rows.push([row.image, row.model, "", correct.join(";"), "", reviewed.join(";")]);
    }
    const csv = value => '"' + String(value ?? "").replaceAll('"', '""') + '"';
    const content = "\uFEFF" + rows.map(row => row.map(csv).join(",")).join("\r\n");
    const url = URL.createObjectURL(new Blob([content], {type: "text/csv;charset=utf-8"}));
    const link = document.createElement("a");
    link.href = url;
    link.download = "human-scores-delta.csv";
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  const first = data.photos.find(photo => pending(photo).length > 0);
  renderPhoto(first ? first.id : null);
})();
