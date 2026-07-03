# Continuidade dos projetos — JORELWAST e STARK

Atualizado em 03/07/2026. Este arquivo é o ponto de entrada para retomar os dois projetos em outro computador. Ele não contém senhas, tokens ou chaves de API.

## 1. JORELWAST

### Identificação

- Pasta atual: `C:\Users\EU SOU JOREL\Documents\O_Preco_do_Rio`
- Repositório: `https://github.com/vx8jorel-cmyk/ponte-educacao.git`
- Branch: `jorelwast-app`
- Último commit registrado: `dad9c18` — configuração das URLs permanentes do Render
- Produção: `https://jorelwast.onrender.com`
- Stack: ASP.NET Core/.NET 8, HTML/CSS/JavaScript, Docker, FFmpeg e Gemini

### Objetivo

Plataforma de produção e publicação em massa. Conecta contas profissionais do Instagram por OAuth oficial, recebe fotos e Reels, gera título e legenda com IA, distribui horários, limita publicações por dia, mantém uma fila e publica pela API oficial da Meta. Há estruturas iniciais para TikTok e ferramentas locais para YouTube.

### Funcionalidades implementadas

- Dashboard JORELWAST e publicador em massa.
- OAuth oficial `Instagram API with Instagram Login`.
- Upload de fotos, MP4 e MOV; lotes de até 100 arquivos.
- Agendamento com intervalo e limite de até 10 posts por dia por conta.
- Fila de publicação e estados `scheduled`, `publishing`, `published` e `failed`.
- IA para analisar mídia e gerar título/legenda.
- Corte e preparação de vídeo com FFmpeg.
- Sincronização periódica do nome, usuário e foto do Instagram.
- Marca dinâmica aplicada pouco antes da publicação usando a identidade atual do perfil.
- Substituições de legenda: `{usuario}`, `{nome}` e `{arquivo}`.
- Docker e Blueprint do Render (`Dockerfile` e `render.yaml`).

### Estado exato em produção

O serviço do Render está online e `/api/status` responde, mas a integração da Meta ainda não está operacional porque `Meta__AppSecret` não foi cadastrada no ambiente do Render. Por isso, clicar em conectar Instagram redireciona para `/?error=meta_not_configured`.

No Render, em **jorelwast > Environment**, precisam existir:

- `Meta__AppId` — já definido pelo Blueprint.
- `Meta__AppSecret` — segredo; copiar do cofre original, nunca salvar no Git.
- `Meta__RedirectUri=https://jorelwast.onrender.com/api/auth/instagram/callback`
- `Meta__PublicBaseUrl=https://jorelwast.onrender.com`
- `GEMINI_API_KEY` — segredo da IA; nunca salvar no Git.
- `JORELWAST_DATA_DIR=/var/data`

Depois de salvar as variáveis, é necessário redeploy. No painel da Meta, adicionar exatamente esta URI OAuth válida:

`https://jorelwast.onrender.com/api/auth/instagram/callback`

Depois, reconectar a conta pelo site permanente.

### Atenção sobre persistência

O plano gratuito do Render usa armazenamento efêmero e pode dormir. Tokens, fila e uploads locais podem ser perdidos em reinícios/deploys. Para operação contínua real, usar um plano com disco persistente montado em `/var/data` ou migrar tokens/fila para banco e mídia para armazenamento de objetos. Não contratar plano pago sem decisão explícita do proprietário.

### Como instalar em outro computador

```powershell
git clone --branch jorelwast-app https://github.com/vx8jorel-cmyk/ponte-educacao.git JORELWAST
cd JORELWAST
dotnet restore .\Ponte.Server\Ponte.Server.csproj
dotnet build .\Ponte.Server\Ponte.Server.csproj
dotnet run --project .\Ponte.Server\Ponte.Server.csproj --urls http://127.0.0.1:5055
```

Requisitos locais: Git, .NET 8 SDK, FFmpeg e opcionalmente Cloudflared. Segredos locais devem ser recriados com `dotnet user-secrets`; nunca copiar para arquivos versionados.

### Verificação rápida

```powershell
Invoke-RestMethod http://127.0.0.1:5055/api/status
Invoke-RestMethod https://jorelwast.onrender.com/api/status
```

### Arquivos centrais

- `Ponte.Server/Program.cs` — endpoints, configuração e limites de upload.
- `Ponte.Server/InstagramService.cs` — OAuth, perfil, publicação e insights.
- `Ponte.Server/PublishingWorker.cs` — fila de publicação.
- `Ponte.Server/ContentAiService.cs` — análise e geração de texto.
- `Ponte.Server/VideoBrandingService.cs` — marca dinâmica com FFmpeg.
- `Ponte.Server/ProfileSyncWorker.cs` — atualização periódica do perfil.
- `publicador.html` e `publicador.js` — agendamento em massa.
- `render.yaml` e `Dockerfile` — produção no Render.

### Alterações locais fora do Git

Há arquivos não rastreados relacionados ao editor RioCut, scripts de YouTube, TikTok e diagnóstico. Eles não estão no repositório remoto e precisam ser revisados antes de decidir o que versionar. Não presumir que o clone do GitHub contém esses arquivos.

## 2. STARK

O documento detalhado está em:

`C:\Users\EU SOU JOREL\Documents\STARK\CONTINUAR_EM_OUTRO_PC.md`

Importante: no momento da geração deste documento, o STARK não tinha remoto Git configurado e possuía muitas mudanças locais não commitadas. Copie a pasta com segurança ou crie um repositório privado antes de trocar de computador.

## Checklist antes de mudar de computador

1. Confirmar que os commits do JORELWAST chegaram ao GitHub.
2. Fazer backup separado dos arquivos não rastreados do JORELWAST, se forem necessários.
3. Versionar o STARK em repositório privado ou copiar a pasta inteira para mídia segura.
4. Exportar as chaves apenas por um gerenciador de senhas/cofre seguro.
5. Não colocar `Meta__AppSecret`, `GEMINI_API_KEY`, tokens OAuth ou bancos com tokens no Git.
6. No novo computador, instalar dependências e executar os testes antes de continuar.

