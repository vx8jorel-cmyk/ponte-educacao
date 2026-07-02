using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string Footer = "Siga (@jorelfilmes) para descobrir seu filme favorito";
var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
    ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User)
    ?? ReadLocalKey();
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Defina GEMINI_API_KEY antes de executar.");
    return 2;
}

var publishToYouTube = args.Contains("--youtube", StringComparer.OrdinalIgnoreCase);
var singleVideo = args.Contains("--single", StringComparer.OrdinalIgnoreCase);
var prepareOnly = args.Contains("--prepare-only", StringComparer.OrdinalIgnoreCase);
var rootArg = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
var root = rootArg is not null ? Path.GetFullPath(rootArg) : Path.Combine(Environment.CurrentDirectory, "cortes");
var inbox = Path.Combine(root, "entrada");
var processed = Path.Combine(root, "processados");
Directory.CreateDirectory(inbox);
Directory.CreateDirectory(processed);

await VideoPreprocessor.SplitLongVideosAsync(inbox, root, singleVideo ? 1 : int.MaxValue);
if (prepareOnly) return 0;

var videoQuery = Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories)
    .Where(p => new[] { ".mp4", ".mov", ".m4v", ".webm" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
    .OrderBy(p => p);
var videos = (singleVideo ? videoQuery.Take(1) : videoQuery).ToArray();

if (videos.Length == 0)
{
    Console.WriteLine($"Coloque os cortes em: {inbox}");
    return 0;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
var results = new List<Analysis>();
foreach (var video in videos)
{
    Console.WriteLine($"Analisando {Path.GetFileName(video)}...");
    try
    {
        var uploaded = await UploadAsync(http, apiKey, video);
        await WaitUntilReadyAsync(http, apiKey, uploaded.Name);
        var analysis = await AnalyzeAsync(http, apiKey, uploaded.Uri, uploaded.MimeType,
            Path.GetFileName(video), ContentHint(video, inbox));
        results.Add(analysis with { FilePath = video });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Falha em {Path.GetFileName(video)}: {ex.Message}");
        Console.WriteLine("Usando metadados seguros de contingência para não interromper a fila.");
        results.Add(FallbackAnalysis(video, ContentHint(video, inbox)));
    }
}

var csvPath = Path.Combine(root, $"metricool-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
await using (var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
{
    await writer.WriteLineAsync("arquivo;tipo;identidade;data;confianca;titulo_youtube;legenda;evidencias;revisar");
    foreach (var item in results)
    {
        var caption = Caption(item);
        var review = item.RequiresReview || item.Confidence < 0.90 ? "SIM" : "NÃO";
        await writer.WriteLineAsync(string.Join(';', Csv(item.FilePath), Csv(item.ContentType), Csv(item.Title), Csv(item.Year),
            Csv(item.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), Csv(item.YoutubeTitle),
            Csv(caption), Csv(string.Join(" | ", item.Evidence)), Csv(review)));
    }
}

Console.WriteLine($"CSV criado: {csvPath}");

if (publishToYouTube && results.Count > 0)
{
    var clientPath = Path.Combine(Environment.CurrentDirectory, ".secrets", "youtube-client.json");
    var tokenPath = Path.Combine(Environment.CurrentDirectory, ".secrets", "youtube-token");
    var schedulePath = Path.Combine(Environment.CurrentDirectory, ".secrets", "youtube-schedule.json");
    var publishedFiles = await YouTubePublisher.PublishAsync(results, Caption, clientPath, tokenPath, schedulePath);
    foreach (var publishedFile in publishedFiles)
    {
        var destination = Path.Combine(processed, Path.GetFileName(publishedFile));
        if (File.Exists(destination))
            destination = Path.Combine(processed, $"{Path.GetFileNameWithoutExtension(publishedFile)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(publishedFile)}");
        File.Move(publishedFile, destination);
        Console.WriteLine($"Movido para processados: {Path.GetFileName(destination)}");
    }
}

if (videos.Length > 0 && results.Count == 0)
    return 3;

return 0;

static string Caption(Analysis item) =>
    $"{ContentEmoji(item.ContentType)} {item.YoutubeTitle}\n\n{item.Synopsis}\n\n{ContentFooter(item.ContentType)}";

static string ContentFooter(string type) => type == "FILME"
    ? Footer
    : "Inscreva-se para acompanhar mais cortes autorizados.";

static string ContentEmoji(string type) => type switch
{
    "ESPORTE" => "⚽", "PODCAST" => "🎙️", "LIVE" => "🔴", "FILME" => "🎬", _ => "▶️"
};

static string ContentHint(string path, string inbox)
{
    var relative = Path.GetRelativePath(inbox, path).Replace('\\', '/').ToLowerInvariant();
    if (relative.Contains("/esporte") || relative.StartsWith("esporte")) return "ESPORTE";
    if (relative.Contains("/podcast") || relative.StartsWith("podcast")) return "PODCAST";
    if (relative.Contains("/live") || relative.StartsWith("live")) return "LIVE";
    if (relative.Contains("/filme") || relative.StartsWith("filme")) return "FILME";
    return "AUTO";
}

static Analysis FallbackAnalysis(string path, string hint)
{
    var type = hint == "AUTO" ? "OUTRO" : hint;
    var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(path),
        @"parte-(\d+)-de-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var part = match.Success ? $" — Parte {int.Parse(match.Groups[1].Value)} de {int.Parse(match.Groups[2].Value)}" : "";
    var title = type switch
    {
        "FILME" => $"Cena de filme em destaque{part}",
        "ESPORTE" => $"Momento esportivo em destaque{part}",
        "PODCAST" => $"Trecho de podcast em destaque{part}",
        "LIVE" => $"Momento de live em destaque{part}",
        _ => $"Momento em destaque{part}"
    };
    return new Analysis(path, type, "", "", title,
        "Conteúdo publicado automaticamente. A identificação detalhada não estava disponível no momento.",
        new[] { "Metadados de contingência: análise da IA temporariamente indisponível." }, 0, true);
}

static async Task<UploadedFile> UploadAsync(HttpClient http, string key, string path)
{
    var mime = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        _ => "video/mp4"
    };
    var size = new FileInfo(path).Length;
    using var start = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/upload/v1beta/files?key={key}");
    start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
    start.Headers.Add("X-Goog-Upload-Command", "start");
    start.Headers.Add("X-Goog-Upload-Header-Content-Length", size.ToString());
    start.Headers.Add("X-Goog-Upload-Header-Content-Type", mime);
    start.Content = new StringContent(JsonSerializer.Serialize(new { file = new { display_name = Path.GetFileName(path) } }), Encoding.UTF8, "application/json");
    using var started = await http.SendAsync(start);
    started.EnsureSuccessStatusCode();
    var uploadUrl = started.Headers.GetValues("X-Goog-Upload-URL").Single();

    await using var stream = File.OpenRead(path);
    using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
    upload.Headers.Add("X-Goog-Upload-Offset", "0");
    upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
    upload.Content = new StreamContent(stream);
    upload.Content.Headers.ContentLength = size;
    upload.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
    using var response = await http.SendAsync(upload);
    response.EnsureSuccessStatusCode();
    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    return new UploadedFile(json["file"]!["name"]!.GetValue<string>(), json["file"]!["uri"]!.GetValue<string>(), mime);
}

static async Task WaitUntilReadyAsync(HttpClient http, string key, string name)
{
    for (var i = 0; i < 60; i++)
    {
        var json = JsonNode.Parse(await http.GetStringAsync($"https://generativelanguage.googleapis.com/v1beta/{name}?key={key}"))!;
        var state = json["state"]?.GetValue<string>();
        if (state == "ACTIVE") return;
        if (state == "FAILED") throw new InvalidOperationException("A Gemini não conseguiu processar o vídeo.");
        await Task.Delay(3000);
    }
    throw new TimeoutException("O processamento do vídeo demorou além do esperado.");
}

static async Task<Analysis> AnalyzeAsync(HttpClient http, string key, string uri, string mime, string filename, string hint)
{
    var prompt = $$"""
    Você é um editor factual de vídeos. Analise TODO o áudio e os quadros deste corte, observando falas,
    placares, uniformes, logotipos, créditos, nomes na tela e timestamps. A pasta sugere {{hint}}, mas pode estar errada.

    Classifique como FILME, ESPORTE, PODCAST, LIVE ou OUTRO. Identifique obra, evento ou programa SOMENTE quando
    houver pelo menos duas evidências independentes no vídeo. Não deduza um filme apenas pelos atores ou pela cena.
    Não invente partida, campeonato, placar, convidado, podcast ou streamer. Se faltar prova, deixe identity vazio,
    reduza confidence e crie um título descritivo fiel ao momento, sem afirmar uma identidade incerta.

    Regras do título do YouTube:
    - máximo 90 caracteres, português brasileiro, específico e natural;
    - FILME: nome da obra só com evidência; caso contrário descreva a cena;
    - ESPORTE: destaque jogada/momento; times, atleta, placar e competição só se confirmados;
    - PODCAST/LIVE: pessoa e assunto só se falados, exibidos ou inequivocamente reconhecidos;
    - sem clickbait falso, sem aspas inventadas e sem escrever hashtags no título.

    A descrição deve resumir apenas o que aparece no corte em até 300 caracteres. evidence deve conter de 2 a 6
    fatos curtos com timestamp aproximado (MM:SS), separando evidência visual e falada. Marque requires_review=true
    quando identity estiver vazia, as evidências conflitarem ou confidence for menor que 0.90.
    """;
    var body = new
    {
        contents = new[] { new { parts = new object[] { new { file_data = new { mime_type = mime, file_uri = uri } }, new { text = prompt } } } },
        generationConfig = new
        {
            responseMimeType = "application/json",
            responseSchema = new
            {
                type = "OBJECT",
                properties = new
                {
                    content_type = new { type = "STRING", @enum = new[] { "FILME", "ESPORTE", "PODCAST", "LIVE", "OUTRO" } },
                    identity = new { type = "STRING", description = "Obra, evento ou programa confirmado; vazio se incerto." },
                    year_or_date = new { type = "STRING" }, youtube_title = new { type = "STRING" },
                    synopsis = new { type = "STRING" }, evidence = new { type = "ARRAY", items = new { type = "STRING" } },
                    spoken_clues = new { type = "ARRAY", items = new { type = "STRING" } },
                    visual_clues = new { type = "ARRAY", items = new { type = "STRING" } },
                    candidates = new { type = "ARRAY", items = new { type = "STRING" } },
                    confidence = new { type = "NUMBER" }, requires_review = new { type = "BOOLEAN" }
                },
                required = new[] { "content_type", "identity", "year_or_date", "youtube_title", "synopsis", "evidence",
                    "spoken_clues", "visual_clues", "candidates", "confidence", "requires_review" }
            }
        }
    };
    var model = Environment.GetEnvironmentVariable("GEMINI_ANALYSIS_MODEL") ?? "gemini-3.5-flash";
    var requestJson = JsonSerializer.Serialize(body);
    var response = await http.PostAsync($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}",
        new StringContent(requestJson, Encoding.UTF8, "application/json"));
    if (!response.IsSuccessStatusCode && model != "gemini-2.5-flash")
    {
        response.Dispose();
        Console.WriteLine($"Modelo {model} indisponível; usando Gemini 2.5 Flash com verificação factual...");
        response = await http.PostAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={key}",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));
    }
    using (response)
    {
    response.EnsureSuccessStatusCode();
    var outer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    var text = outer["candidates"]![0]!["content"]!["parts"]![0]!["text"]!.GetValue<string>();
    var parsed = JsonNode.Parse(text)!;
    var evidence = parsed["evidence"]?.AsArray().Select(node => node?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray()
        ?? Array.Empty<string>();
    var identity = parsed["identity"]?.GetValue<string>() ?? "";
    var yearOrDate = parsed["year_or_date"]?.GetValue<string>() ?? "";
    var youtubeTitle = parsed["youtube_title"]?.GetValue<string>() ?? "Momento em destaque";
    var confidence = parsed["confidence"]!.GetValue<double>();
    var requiresReview = parsed["requires_review"]!.GetValue<bool>();

    var identityVerified = false;
    var verification = await VerifyWithSearchAsync(http, key, parsed, filename);
    if (verification is { Verified: true } && !string.IsNullOrWhiteSpace(verification.Identity)
        && verification.Confidence >= Math.Max(0.85, confidence))
    {
        identity = verification.Identity;
        yearOrDate = verification.YearOrDate;
        youtubeTitle = verification.YoutubeTitle;
        confidence = verification.Confidence;
        requiresReview = confidence < 0.90 || verification.Ambiguous;
        identityVerified = true;
        if (!string.IsNullOrWhiteSpace(verification.SourceSummary))
            evidence = evidence.Append($"Pesquisa factual: {verification.SourceSummary}").ToArray();
    }
    if (!string.IsNullOrWhiteSpace(identity) && !identityVerified)
    {
        youtubeTitle = RemoveUnverifiedIdentity(youtubeTitle, identity, parsed["content_type"]!.GetValue<string>());
        identity = "";
        confidence = Math.Min(confidence, 0.89);
        requiresReview = true;
        evidence = evidence.Append("Identidade não confirmada; removida automaticamente do título publicado.").ToArray();
    }
    if (youtubeTitle.Length > 100) youtubeTitle = youtubeTitle[..100];
    return new Analysis(filename, parsed["content_type"]!.GetValue<string>(), identity,
        yearOrDate, youtubeTitle,
        parsed["synopsis"]!.GetValue<string>(), evidence, confidence, requiresReview);
    }
}

static string RemoveUnverifiedIdentity(string title, string identity, string contentType)
{
    var safe = title.Replace(identity, "", StringComparison.OrdinalIgnoreCase).Trim(' ', ':', '-', '–', '|');
    if (safe.Length >= 12) return safe;
    return contentType switch
    {
        "FILME" => "Cena marcante com uma abordagem inesperada",
        "ESPORTE" => "Momento esportivo em destaque",
        "PODCAST" => "Reflexão importante durante a conversa",
        "LIVE" => "Momento inesperado durante a live",
        _ => "Momento em destaque"
    };
}

static async Task<Verification?> VerifyWithSearchAsync(HttpClient http, string key, JsonNode analysis, string filename)
{
    var prompt = $$"""
    Você é a segunda etapa de verificação factual de um sistema de vídeos. Use pesquisa Google para confirmar a
    identidade deste conteúdo a partir de falas exatas, textos visuais, personagens, uniformes, cenário e candidatos.
    Não confie no nome do arquivo e não confirme apenas por semelhança temática. Para FILME, procure combinações de
    falas e personagens em sinopses, roteiros, legendas, páginas oficiais e bases cinematográficas. Para ESPORTE,
    confirme atleta, times, evento e data. Para PODCAST/LIVE, confirme programa, participantes e episódio/assunto.

    Análise multimodal inicial de {{filename}}:
    {{analysis.ToJsonString()}}

    Responda SOMENTE com JSON válido neste formato:
    {"verified":true|false,"identity":"nome confirmado ou vazio","year_or_date":"", "youtube_title":"até 90 caracteres",
    "confidence":0.0,"ambiguous":true|false,"source_summary":"quais sinais e fontes coincidiram"}
    Use verified=true somente se múltiplas evidências pesquisadas convergirem. Havendo mais de uma obra/evento possível,
    use verified=false e ambiguous=true.
    """;
    var body = new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } },
        tools = new[] { new { google_search = new { } } },
        generationConfig = new { temperature = 0.1, maxOutputTokens = 1200 }
    };
    var model = Environment.GetEnvironmentVariable("GEMINI_VERIFICATION_MODEL") ?? "gemini-3.5-flash";
    try
    {
        var response = await http.PostAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode && model != "gemini-2.5-flash")
        {
            response.Dispose();
            response = await http.PostAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={key}",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Verificação por pesquisa indisponível ({(int)response.StatusCode}); mantendo análise visual.");
                return null;
            }
        var outer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var text = outer["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = JsonNode.Parse(text[start..(end + 1)])!;
        return new Verification(
            json["verified"]?.GetValue<bool>() ?? false,
            json["identity"]?.GetValue<string>() ?? "",
            json["year_or_date"]?.GetValue<string>() ?? "",
            json["youtube_title"]?.GetValue<string>() ?? "Momento em destaque",
            json["confidence"]?.GetValue<double>() ?? 0,
            json["ambiguous"]?.GetValue<bool>() ?? true,
            json["source_summary"]?.GetValue<string>() ?? "");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Verificação por pesquisa falhou: {ex.Message}");
        return null;
    }
}

static string Csv(string? value) => $"\"{(value ?? "").Replace("\"", "\"\"").Replace("\r", "").Replace("\n", "\\n")}\"";

static string? ReadLocalKey()
{
    var path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".secrets", "gemini.key"));
    return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}

record UploadedFile(string Name, string Uri, string MimeType);
record Analysis(string FilePath, string ContentType, string Title, string Year, string YoutubeTitle,
    string Synopsis, string[] Evidence, double Confidence, bool RequiresReview);
record Verification(bool Verified, string Identity, string YearOrDate, string YoutubeTitle,
    double Confidence, bool Ambiguous, string SourceSummary);
