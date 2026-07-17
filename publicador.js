const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
let accounts = [];
let youtube = { configured: false, connected: false, account: null };
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

function selectedPlatforms() {
  const platforms = [];
  if ($("#platformInstagram")?.checked) platforms.push("instagram");
  if ($("#platformYouTube")?.checked) platforms.push("youtube");
  return platforms;
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
  const platforms = selectedPlatforms();
  const instagramCount = platforms.includes("instagram") ? selectedAccountIds().length : 0;
  const youtubeCount = platforms.includes("youtube") && youtube.connected ? 1 : 0;
  const total = files.length * (instagramCount + youtubeCount);
  const interval = readInteger("#intervalSeconds", 0, 0, 604800);
  const daily = readInteger("#dailyLimit", 5000, 1, 5000);
  $("#batchPreview").textContent = total ? `${total} publicação(ões) serão adicionadas à fila · intervalo de ${formatInterval(interval)} · até ${daily} por dia/conta.` : "Selecione arquivos e contas para calcular o lote.";
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
    const aiLabel = { pending: "Na fila da IA", analyzing: "IA analisando...", ready: "Legenda pronta", fallback: "Legenda automática simples", disabled: "Manual" }[post.aiStatus] || post.aiStatus || "Manual";
    const title = post.title || post.originalFileName || (post.type === "REELS" ? "REEL" : "FOTO");
    const caption = post.error || post.caption || (post.aiStatus === "pending" ? "Aguardando a IA gerar título e legenda." : post.aiStatus === "analyzing" ? "A IA está analisando este arquivo. A publicação só sai depois que a legenda ficar pronta." : "Sem legenda");
    article.innerHTML = `<div><span class="status-pill">${status}</span><strong>${post.type === "REELS" ? "REEL" : "FOTO"} · @${escapeHtml(account?.username || post.accountId)}</strong><time>${new Date(post.publishAt).toLocaleString("pt-BR", { dateStyle: "short", timeStyle: "short" })}</time></div><button class="icon-btn" title="Excluir" aria-label="Excluir"><i data-lucide="trash-2"></i></button><p><b>${escapeHtml(title)}</b><br><small>${escapeHtml(aiLabel)}</small><br>${escapeHtml(caption)}</p>`;
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
    const [status, youtubeStatus] = await Promise.all([api("/api/status"), api("/api/youtube/status").catch(() => ({ configured: false, connected: false, account: null }))]);
    youtube = youtubeStatus;
    accounts = status.accounts || (status.account ? [status.account] : []);
    $("#connectionBadge").innerHTML = status.configured ? `<i data-lucide="${accounts.length ? "badge-check" : "plug"}"></i> ${accounts.length ? `${accounts.length} conta(s) conectada(s)` : "Servidor pronto"}` : '<i data-lucide="circle-alert"></i> Configure a Meta';
    $("#youtubeState").textContent = youtube.connected ? `Conectado: ${youtube.account?.title || "canal"}` : youtube.configured ? "Clique em Contas para conectar" : "Configure YouTube no Render";
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
  files = [...event.target.files].slice(0, 1000);
  renderFiles();
  updateBatchPreview();
});
$("#caption").addEventListener("input", () => { $("#captionCount").textContent = `${$("#caption").value.length} / 2.200`; });
$("#intervalSeconds").addEventListener("input", updateBatchPreview);
$("#dailyLimit").addEventListener("input", updateBatchPreview);
$("#platformInstagram").addEventListener("change", updateBatchPreview);
$("#platformYouTube").addEventListener("change", updateBatchPreview);
$$('[data-status]').forEach(button => button.addEventListener("click", () => {
  queueFilter = button.dataset.status;
  $$("[data-status]").forEach(item => item.classList.toggle("active", item === button));
  renderQueue();
}));

$("#postForm").addEventListener("submit", async event => {
  event.preventDefault();
  const accountIds = selectedAccountIds();
  const platforms = selectedPlatforms();
  if (!platforms.length) return showToast("Escolha Instagram, YouTube ou ambos.", true);
  if (platforms.includes("instagram") && !accounts.length) return showToast("Conecte uma conta do Instagram primeiro.", true);
  if (platforms.includes("instagram") && !accountIds.length) return showToast("Marque ao menos uma conta do Instagram.", true);
  if (platforms.includes("youtube") && !youtube.connected) return showToast("Conecte o YouTube primeiro na aba Contas.", true);
  if (platforms.includes("youtube") && files.some(file => !file.type.startsWith("video/"))) return showToast("YouTube aceita apenas vídeos neste lote. Remova imagens ou desmarque YouTube.", true);
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
  const interval = readInteger("#intervalSeconds", 0, 0, 604800);
  const dailyLimit = readInteger("#dailyLimit", 5000, 1, 5000);
  let processed = 0, scheduled = 0;
  try {
    for (let index = 0; index < chunks.length; index++) {
      button.textContent = `Enviando parte ${index + 1} de ${chunks.length}...`;
      const form = new FormData();
      chunks[index].forEach(file => form.append("media", file));
      accountIds.forEach(id => form.append("accountId", id));
      platforms.forEach(platform => form.append("platform", platform));
      form.append("caption", $("#caption").value.trim());
      form.append("publishAt", new Date(start.getTime() + processed * interval * 1000).toISOString());
      form.append("intervalSeconds", String(interval));
      form.append("dailyLimit", String(dailyLimit));
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
function readInteger(selector, fallback, min, max) {
  const value = Number.parseInt($(selector).value, 10);
  if (!Number.isFinite(value)) return fallback;
  return Math.min(max, Math.max(min, value));
}
function formatInterval(seconds) {
  if (seconds === 0) return "imediato/mesmo horário";
  if (seconds < 60) return `${seconds}s`;
  if (seconds % 3600 === 0) return `${seconds / 3600}h`;
  if (seconds % 60 === 0) return `${seconds / 60}min`;
  return `${Math.floor(seconds / 60)}min ${seconds % 60}s`;
}

const start = new Date(Date.now() + 5 * 60 * 1000);
$("#publishDate").value = start.toLocaleDateString("en-CA");
$("#publishTime").value = start.toTimeString().slice(0, 5);
Promise.all([loadStatus(), loadPosts()]);
setInterval(() => Promise.all([loadStatus(), loadPosts()]), 30000);
lucide.createIcons();
