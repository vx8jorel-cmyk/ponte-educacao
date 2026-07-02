const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
let accounts = [];
let posts = [];
let files = [];
let queueFilter = "all";

function showToast(message, isError = false) {
  const toast = $("#toast");
  toast.textContent = message;
  toast.classList.toggle("error", isError);
  toast.classList.add("show");
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => toast.classList.remove("show"), 3800);
}

async function api(path, options) {
  const response = await fetch(path, options);
  if (response.status === 204) return null;
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error || `Falha no servidor (${response.status}).`);
  return body;
}

function selectedAccountIds() {
  return $$("#accountList input:checked").map(input => input.value);
}

function renderAccounts() {
  const list = $("#accountList");
  if (!accounts.length) {
    list.innerHTML = '<span class="empty-inline">Nenhuma conta conectada.</span>';
    return;
  }
  list.replaceChildren(...accounts.map(account => {
    const label = document.createElement("label");
    label.className = "account-chip";
    label.innerHTML = `<input type="checkbox" value="${account.id}" checked><span><b>${escapeHtml(account.name || account.username)}</b><small>@${escapeHtml(account.username)}</small></span>`;
    label.querySelector("input").addEventListener("change", updateBatchPreview);
    return label;
  }));
}

function renderFiles() {
  $("#fileSummary").textContent = files.length ? `${files.length} arquivo(s) · ${formatBytes(files.reduce((sum, file) => sum + file.size, 0))}` : "Nenhum arquivo selecionado.";
  $("#fileList").replaceChildren(...files.map((file, index) => {
    const item = document.createElement("div");
    item.className = "file-item";
    item.innerHTML = `<i data-lucide="${file.type.startsWith("video/") ? "clapperboard" : "image"}"></i><span title="${escapeHtml(file.name)}">${escapeHtml(file.name)}</span><button type="button" aria-label="Remover"><i data-lucide="x"></i></button>`;
    item.querySelector("button").addEventListener("click", () => { files.splice(index, 1); renderFiles(); updateBatchPreview(); });
    return item;
  }));
  lucide.createIcons();
}

function updateBatchPreview() {
  const total = files.length * selectedAccountIds().length;
  $("#batchPreview").textContent = total ? `${total} publicação(ões) serão adicionadas à fila, respeitando o limite diário escolhido.` : "Selecione arquivos e contas para calcular o lote.";
}

function renderQueue() {
  const visible = posts.filter(post => queueFilter === "all" || post.status === queueFilter).sort((a, b) => new Date(a.publishAt) - new Date(b.publishAt));
  $("#queueCount").textContent = posts.length;
  const list = $("#queueList");
  if (!visible.length) {
    list.innerHTML = '<div class="empty"><strong>Nenhuma publicação aqui</strong><p>Os lotes agendados aparecerão nesta fila.</p></div>';
    return;
  }
  list.replaceChildren(...visible.map(post => {
    const account = accounts.find(item => item.id === post.accountId);
    const article = document.createElement("article");
    article.className = `queue-item status-${post.status}`;
    const status = { scheduled: "Agendado", publishing: "Publicando", published: "Publicado", failed: "Erro" }[post.status] || post.status;
    article.innerHTML = `<div><span class="status-pill">${status}</span><strong>${post.type === "REELS" ? "REEL" : "FOTO"} · @${escapeHtml(account?.username || post.accountId)}</strong><time>${new Date(post.publishAt).toLocaleString("pt-BR", { dateStyle: "short", timeStyle: "short" })}</time></div><button class="icon-btn" title="Excluir" aria-label="Excluir"><i data-lucide="trash-2"></i></button><p>${escapeHtml(post.error || post.caption || "Sem legenda")}</p>`;
    article.querySelector("button").addEventListener("click", async () => {
      if (!confirm("Excluir esta publicação da fila?")) return;
      try { await api(`/api/posts/${post.id}`, { method: "DELETE" }); await loadPosts(); }
      catch (error) { showToast(error.message, true); }
    });
    return article;
  }));
  lucide.createIcons();
}

async function loadStatus() {
  try {
    const status = await api("/api/status");
    accounts = status.accounts || (status.account ? [status.account] : []);
    $("#connectionBadge").innerHTML = status.configured ? `<i data-lucide="${accounts.length ? "badge-check" : "plug"}"></i> ${accounts.length ? `${accounts.length} conta(s) conectada(s)` : "Servidor pronto"}` : '<i data-lucide="circle-alert"></i> Configure a Meta';
    renderAccounts();
    updateBatchPreview();
    lucide.createIcons();
  } catch {
    $("#connectionBadge").innerHTML = '<i data-lucide="wifi-off"></i> Abra pelo servidor local';
    renderAccounts();
    lucide.createIcons();
  }
}

async function loadPosts() {
  try { posts = await api("/api/posts"); renderQueue(); }
  catch { posts = []; renderQueue(); }
}

$("#connectAccount").addEventListener("click", () => { window.location.href = "/api/auth/instagram/start"; });
$("#mediaInput").addEventListener("change", event => {
  files = [...event.target.files].slice(0, 100);
  renderFiles();
  updateBatchPreview();
});
$("#caption").addEventListener("input", () => { $("#captionCount").textContent = `${$("#caption").value.length} / 2.200`; });
$$('[data-status]').forEach(button => button.addEventListener("click", () => {
  queueFilter = button.dataset.status;
  $$("[data-status]").forEach(item => item.classList.toggle("active", item === button));
  renderQueue();
}));

$("#postForm").addEventListener("submit", async event => {
  event.preventDefault();
  const accountIds = selectedAccountIds();
  if (!accounts.length) return showToast("Conecte uma conta do Instagram primeiro.", true);
  if (!accountIds.length) return showToast("Marque ao menos uma conta.", true);
  if (!files.length) return showToast("Selecione pelo menos um arquivo.", true);
  if (!$("#publishDate").value || !$("#publishTime").value) return showToast("Escolha data e horário inicial.", true);

  const button = $("#scheduleBatch");
  button.disabled = true;
  const maximumChunk = 75 * 1024 * 1024;
  const oversized = files.find(file => file.size > maximumChunk);
  if (oversized) {
    button.disabled = false;
    return showToast(`${oversized.name} ultrapassa 75 MB. Comprima esse vídeo antes do envio pelo túnel atual.`, true);
  }
  const chunks = [];
  let current = [], currentSize = 0;
  files.forEach(file => {
    if (current.length && currentSize + file.size > maximumChunk) { chunks.push(current); current = []; currentSize = 0; }
    current.push(file); currentSize += file.size;
  });
  if (current.length) chunks.push(current);
  const start = new Date(`${$("#publishDate").value}T${$("#publishTime").value}`);
  const interval = Number($("#intervalMinutes").value);
  let processed = 0, scheduled = 0;
  try {
    for (let index = 0; index < chunks.length; index++) {
      button.textContent = `Enviando parte ${index + 1} de ${chunks.length}...`;
      const form = new FormData();
      chunks[index].forEach(file => form.append("media", file));
      accountIds.forEach(id => form.append("accountId", id));
      form.append("caption", $("#caption").value.trim());
      form.append("publishAt", new Date(start.getTime() + processed * interval * 60000).toISOString());
      form.append("intervalMinutes", String(interval));
      form.append("dailyLimit", $("#dailyLimit").value);
      form.append("useAi", $("#useAi").checked ? "true" : "false");
      const result = await api("/api/posts/bulk", { method: "POST", body: form });
      scheduled += result.count; processed += chunks[index].length;
    }
    showToast(`${scheduled} publicação(ões) adicionada(s) à fila.`);
    files = [];
    $("#mediaInput").value = "";
    renderFiles();
    updateBatchPreview();
    await loadPosts();
  } catch (error) { showToast(`${scheduled ? `${scheduled} foram agendadas. ` : ""}${error.message}`, true); }
  finally { button.disabled = false; button.innerHTML = '<i data-lucide="calendar-plus"></i> Agendar lote'; lucide.createIcons(); }
});

function escapeHtml(value = "") { const span = document.createElement("span"); span.textContent = value; return span.innerHTML; }
function formatBytes(value) { if (value < 1024 * 1024) return `${Math.ceil(value / 1024)} KB`; return `${(value / 1024 / 1024).toFixed(1)} MB`; }

const start = new Date(Date.now() + 30 * 60 * 1000);
$("#publishDate").value = start.toLocaleDateString("en-CA");
$("#publishTime").value = start.toTimeString().slice(0, 5);
Promise.all([loadStatus(), loadPosts()]);
setInterval(loadPosts, 30000);
lucide.createIcons();
