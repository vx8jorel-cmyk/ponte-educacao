# Automação de legendas de filmes

1. Crie uma chave da Gemini API.
2. No PowerShell, defina-a somente para a sessão atual:
   `$env:GEMINI_API_KEY="SUA_CHAVE"`
3. Coloque vídeos em `cortes/canais/jorel-filmes/entrada`.
4. Execute:
   `dotnet run --project FilmCaption.Automation`
5. Revise as linhas marcadas como `SIM` e importe/agende o CSV no Metricool.

## Publicação automática no YouTube

A credencial OAuth fica em `.secrets/youtube-client.json`. Para analisar os vídeos,
autorizar sua conta Google e agendá-los no YouTube, execute:

`dotnet run --project FilmCaption.Automation -- --youtube`

O primeiro vídeo é agendado para amanhã às 18h (horário de São Paulo), e os seguintes
para um por dia. O último horário usado fica salvo em `.secrets/youtube-schedule.json`.
Após um upload concluído, o arquivo sai de `cortes/entrada` e vai para
`cortes/processados`, evitando publicações duplicadas. Você também pode executar
`PublicarNoYouTube.cmd` na pasta principal com dois cliques.

O monitor automático `MonitorarPastaYouTube.ps1` é iniciado com o Windows pela tarefa
`Jorel Filmes - Automação YouTube`. Ele verifica a pasta `entrada` de cada canal habilitado
em `cortes/canais/canais.json` a cada 30 segundos.
O histórico fica em `cortes/automacao-youtube.log`.

Cada execução automática processa apenas um arquivo. Vídeos acima de 59 segundos são
divididos em partes equilibradas de no máximo 59 segundos com FFmpeg; o original vai
para `processados/originais-longos`. Em caso de limite da API, a fila aguarda 15 minutos
antes de tentar novamente.

Cada upload concluído é gravado imediatamente em `.secrets/youtube-uploaded.json` com
nome do arquivo, ID do vídeo e data de publicação. Isso evita duplicatas e permite mover
o arquivo para `processados` mesmo depois de uma interrupção. Quando o próprio YouTube
atinge o limite diário do canal, a fila pausa por 6 horas e continua automaticamente.

O calendário padrão usa 10 horários por dia: 08:00, 09:30, 11:00, 12:30, 14:00,
15:30, 17:00, 18:30, 20:00 e 21:30, no horário de São Paulo.

Você pode organizar a entrada em `filmes`, `esportes`, `podcasts` e `lives`. A pasta
serve apenas como dica; a IA verifica áudio e imagem, registra evidências com timestamps
e marca para revisão quando a identidade não estiver suficientemente comprovada.
Itens abaixo de 90% também são publicados. Quando a identidade não é confirmada pela
pesquisa, o nome duvidoso é removido e substituído por um título descritivo seguro.

Formato da legenda:

```text
🎬 Nome do filme (ano)

Sinopse curta.

Siga (@jorelfilmes) para descobrir seu filme favorito
```

Use somente cortes que você tenha autorização para publicar. A identificação automática pode errar, por isso vídeos com confiança abaixo de 85% são marcados para revisão.
