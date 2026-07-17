const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
let state = { status: {}, youtube: {}, dashboard: {}, posts: [], channels: [] };
const labels = { dashboard:["VISÃO GERAL","Sua operação de conteúdo"], studio:["ESTÚDIO IA","Produção automática"], channels:["CANAIS","Portfólio de conteúdo"], queue:["FILA","Calendário operacional"], accounts:["CONTAS","Conexões sociais"] };

async function get(path){const response=await fetch(path);if(!response.ok)throw new Error(`${path}: ${response.status}`);return response.json()}
function esc(value=""){const node=document.createElement("span");node.textContent=value;return node.innerHTML}
function icon(){window.lucide?.createIcons()}
function toast(text){const node=$("#toast");node.textContent=text;node.classList.add("show");clearTimeout(toast.t);toast.t=setTimeout(()=>node.classList.remove("show"),2800)}

async function load(){
  try{
    const [status,youtube,dashboard,posts,channelData]=await Promise.all([get("/api/status"),get("/api/youtube/status").catch(()=>({})),get("/api/dashboard"),get("/api/posts"),get("/channels.json").catch(()=>({channels:[]}))]);
    state={status,youtube,dashboard,posts,channels:channelData.channels||[]};render();
  }catch(error){$("#systemState").textContent="Servidor desconectado";console.error(error)}
}

function render(){
  const {status,dashboard,posts,channels}=state;const accounts=status.accounts||[];const account=accounts[0];
  $("#metricAccounts").textContent=dashboard.accounts||0;$("#metricQueued").textContent=dashboard.scheduled||0;$("#metricAi").textContent=dashboard.analyzing||0;$("#metricPublished").textContent=dashboard.published||0;$("#navQueue").textContent=dashboard.scheduled||0;
  $("#systemState").textContent=status.aiConfigured?"IA + publicador online":"Publicador online · IA pendente";
  const next=posts.filter(p=>p.status==="scheduled").sort((a,b)=>new Date(a.publishAt)-new Date(b.publishAt))[0];$("#nextPost").textContent=next?`próximo ${new Date(next.publishAt).toLocaleString("pt-BR",{dateStyle:"short",timeStyle:"short"})}`:"nenhum agendamento";
  renderProfile(account);renderActivity(posts,accounts);renderChannels(channels);renderQueue(posts,accounts);renderAccounts(accounts);icon();
}

function renderProfile(account){
  const fallback="assets/profile-placeholder.svg";const avatar=account?.avatarUrl||fallback;const name=account?.name||"JORELWAST Studio";const handle=account?`@${account.username}`:"Conecte seu Instagram";
  [$("#sidebarAvatar"),$("#heroAvatar")].forEach(img=>{img.src=avatar;img.onerror=()=>img.src=fallback});
  $("#sidebarName").textContent=name;$("#sidebarHandle").textContent=handle;$("#heroName").textContent=name;$("#heroHandle").textContent=account?`${handle} · fundo sincronizado com a foto atual do perfil`:"Conecte seu Instagram para personalizar este espaço.";
  const photo=$("#heroPhoto");photo.style.backgroundImage=account?.avatarUrl?`url("${account.avatarUrl.replaceAll('"','%22')}")`:"linear-gradient(120deg,#5b2ad1,#19142a 55%,#ff7a3d)";
}

function renderActivity(posts,accounts){
  const list=$("#recentActivity");const recent=[...posts].sort((a,b)=>new Date(b.createdAt)-new Date(a.createdAt)).slice(0,6);
  if(!recent.length){list.innerHTML='<div class="empty">Nenhuma atividade ainda.</div>';return}
  list.innerHTML=recent.map(post=>{const account=accounts.find(a=>a.id===post.accountId);const status={scheduled:"Agendado",publishing:"Publicando",published:"Publicado",failed:"Falhou"}[post.status]||post.status;return `<div class="activity"><span class="activity-icon ${post.status}"><i data-lucide="${post.aiStatus==="analyzing"||post.aiStatus==="pending"?"brain-circuit":post.status==="published"?"check":"clock-3"}"></i></span><div><b>${esc(post.title||post.originalFileName||post.type)}</b><small>@${esc(account?.username||post.accountId)} · ${status}</small></div><time>${new Date(post.publishAt).toLocaleDateString("pt-BR")}</time></div>`}).join("");
}

function channelIcon(id){if(id.includes("filme"))return"clapperboard";if(id.includes("esporte"))return"trophy";if(id.includes("podcast"))return"mic-2";if(id.includes("live"))return"radio";if(id.includes("ciencia"))return"flask-conical";return"sparkles"}
function renderChannels(channels){
  const cards=channels.map((channel,index)=>`<article class="channel-card ${channel.enabled?"enabled":""}"><span class="channel-icon"><i data-lucide="${channelIcon(channel.id)}"></i></span><div><b>${esc(channel.name)}</b><small>${esc(channel.format)}</small></div><em>${channel.enabled?"Ativo":"Preparado"}</em></article>`).join("");
  $("#channelGrid").innerHTML=cards||'<div class="empty">Nenhum canal configurado.</div>';$("#channelPreview").innerHTML=channels.slice(0,4).map(c=>`<div><i data-lucide="${channelIcon(c.id)}"></i><span><b>${esc(c.name)}</b><small>${c.enabled?"Operação ativa":"Pronto para ativar"}</small></span></div>`).join("");
}

function renderQueue(posts,accounts){
  const body=$("#queueTable");const ordered=[...posts].sort((a,b)=>new Date(a.publishAt)-new Date(b.publishAt));
  body.innerHTML=ordered.length?ordered.map(post=>{const account=accounts.find(a=>a.id===post.accountId);return `<tr><td><b>${esc(post.title||post.originalFileName||post.type)}</b><small>${esc((post.caption||"").slice(0,70))}</small></td><td>@${esc(account?.username||post.accountId)}</td><td><span class="ai-state ${post.aiStatus}">${esc(post.aiStatus||"manual")}</span></td><td>${new Date(post.publishAt).toLocaleString("pt-BR",{dateStyle:"short",timeStyle:"short"})}</td><td><span class="post-state ${post.status}">${esc(post.status)}</span></td></tr>`}).join(""):'<tr><td colspan="5" class="empty">A fila está vazia.</td></tr>';
}

function renderAccounts(accounts){
  const youtube = state.youtube?.account;
  const instagramCards = accounts.map(a=>`<article class="account-card"><div class="account-cover" style="background-image:url('${(a.avatarUrl||"").replaceAll("'","%27")}')"></div><img src="${esc(a.avatarUrl||"assets/profile-placeholder.svg")}" alt=""><div><b>${esc(a.name||a.username)}</b><small>@${esc(a.username)}</small><span><i data-lucide="badge-check"></i> Instagram conectado</span></div></article>`);
  const youtubeCard = youtube ? [`<article class="account-card"><div class="account-cover" style="background-image:url('${(youtube.thumbnailUrl||"").replaceAll("'","%27")}')"></div><img src="${esc(youtube.thumbnailUrl||"assets/profile-placeholder.svg")}" alt=""><div><b>${esc(youtube.title||"YouTube")}</b><small>${esc(youtube.id||"canal")}</small><span><i data-lucide="badge-check"></i> YouTube conectado</span></div></article>`] : [];
  const cards = [...instagramCards, ...youtubeCard];
  $("#accountsGrid").innerHTML=cards.length?cards.join(""):'<div class="empty account-empty"><b>Nenhuma conta conectada</b><span>Conecte Instagram ou YouTube para começar.</span></div>';
}

function showView(name){$$('[data-view]').forEach(b=>b.classList.toggle("active",b.dataset.view===name));$$('.view').forEach(v=>v.classList.remove("active"));$(`#${name}View`).classList.add("active");$("#viewName").textContent=labels[name][0];$("#pageTitle").textContent=labels[name][1];location.hash=name;window.scrollTo({top:0,behavior:"smooth"})}
$$('[data-view]').forEach(button=>button.addEventListener("click",()=>showView(button.dataset.view)));$$('[data-go]').forEach(button=>button.addEventListener("click",()=>showView(button.dataset.go)));$("#refresh").addEventListener("click",()=>{load();toast("Painel atualizado")});
const initial=location.hash.slice(1);if(labels[initial])showView(initial);load();setInterval(load,30000);icon();
