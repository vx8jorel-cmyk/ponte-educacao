# JORELWAST — produção e publicação inteligente em massa

Aplicação gratuita para escolas programarem fotos e Reels e acompanharem o engajamento pela API oficial da Meta.

Também inclui uma área TikTok com OAuth oficial, upload e agendamento de vídeos pela Content Posting API.

## O que já está implementado

- OAuth oficial **Instagram API with Instagram Login**;
- várias contas do Instagram conectadas por OAuth, selecionáveis por lote;
- token de longa duração protegido no servidor com ASP.NET Data Protection;
- upload em massa de até 100 fotos (`IMAGE`) e Reels (`REELS`) por lote;
- distribuição automática de horários, com limite configurável de até 10 posts por dia por conta;
- fila que verifica publicações a cada 30 segundos;
- espera do processamento de vídeo antes de publicar o Reel;
- calendário, estado de publicação e erros;
- alcance, visualizações, curtidas, comentários, compartilhamentos, salvos e interações;
- nenhuma senha do Instagram passa pela Ponte.

## Requisitos

1. .NET 8 SDK (o computador atualmente possui apenas o runtime; instale o SDK para compilar).
2. Domínio público com HTTPS. A Meta precisa baixar as fotos e vídeos por uma URL pública.
3. Conta do Instagram **Business ou Creator**. Este fluxo não exige uma Página do Facebook vinculada à conta publicada.

## HTTPS temporário

O desenvolvimento pode ser exposto com `cloudflared tunnel --url http://127.0.0.1:5055`. URLs `trycloudflare.com` são temporárias e mudam quando o túnel reinicia; para produção, use domínio e túnel nomeado estáveis.
4. Aplicativo criado em [Meta for Developers](https://developers.facebook.com/apps/).

## Cadastro na Meta

1. Crie um app na Meta e escolha o caso de uso para acessar a API do Instagram.
2. Adicione **Instagram API with Instagram Login**.
3. Em configurações da API, cadastre exatamente:
   - Redirect OAuth URI: `https://SEU-DOMINIO/api/auth/instagram/callback`
   - Deauthorize callback: `https://SEU-DOMINIO/api/meta/deauthorize` (adicione o endpoint antes da análise pública)
   - Data deletion URL: `https://SEU-DOMINIO/data-deletion`
4. Solicite as permissões `instagram_business_basic`, `instagram_business_content_publish` e `instagram_business_manage_insights`.
5. Durante desenvolvimento, adicione a conta profissional como Instagram Tester. Para outras escolas, conclua Business Verification e App Review.

## Segredos e execução

Nunca grave o App Secret no Git. Configure com User Secrets:

```powershell
cd Ponte.Server
dotnet user-secrets set "Meta:AppId" "SEU_APP_ID"
dotnet user-secrets set "Meta:AppSecret" "SEU_APP_SECRET"
dotnet user-secrets set "Meta:RedirectUri" "https://SEU-DOMINIO/api/auth/instagram/callback"
dotnet user-secrets set "Meta:PublicBaseUrl" "https://SEU-DOMINIO"
dotnet user-secrets set "TikTok:ClientKey" "SEU_CLIENT_KEY"
dotnet user-secrets set "TikTok:ClientSecret" "SEU_CLIENT_SECRET"
dotnet user-secrets set "TikTok:RedirectUri" "https://SEU-DOMINIO/api/auth/tiktok/callback"
dotnet run
```

Para testar localmente, use um túnel HTTPS apontando para a porta exibida pelo `dotnet run` e configure essa URL tanto nos segredos quanto no painel da Meta.

## Observações importantes

- A API não publica em conta pessoal.
- A mídia deve continuar disponível publicamente até a Meta terminar de processá-la.
- Para várias escolas, substitua o armazenamento JSON por banco de dados e associe cada token a um usuário autenticado.
- Antes de produção, adicione login institucional, política de privacidade, exclusão de dados, antivírus no upload e armazenamento de mídia em nuvem.
