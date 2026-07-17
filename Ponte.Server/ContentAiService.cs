using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ponte.Server;

public sealed record ContentAnalysis(string Title, string Caption, string ContentType, string Identity,
    string Synopsis, string[] Evidence, double Confidence);

public sealed class ContentAiService(HttpClient http, IWebHostEnvironment environment)
{
    private readonly string _keyPath = Path.Combine(Directory.GetParent(environment.ContentRootPath)!.FullName, ".secrets", "gemini.key");
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ReadKey());

    public async Task<ContentAnalysis> AnalyzeAsync(string path, CancellationToken ct)
    {
        var key = ReadKey() ?? throw new InvalidOperationException("Configure a chave da IA Gemini.");
        var uploaded = await UploadAsync(key, path, ct);
        await WaitUntilReadyAsync(key, uploaded.Name, ct);
        var initial = await AnalyzeVideoAsync(key, uploaded, Path.GetFileName(path), ct);
        var verified = await VerifyAsync(key, initial, ct);
        if (verified is not null && verified.Confidence >= 0.85 && !string.IsNullOrWhiteSpace(verified.Identity))
            initial = initial with { Identity = verified.Identity, Title = verified.Title, Confidence = verified.Confidence,
                Evidence = initial.Evidence.Append($"Pesquisa factual: {verified.Summary}").ToArray() };
        else if (RequiresFactualIdentity(initial))
            initial = initial with
            {
                Identity = "",
                Title = CreativeSceneTitle(initial),
                Confidence = Math.Min(initial.Confidence, 0.74),
                Evidence = initial.Evidence.Append("Identidade exata não confirmada; usando título criativo seguro baseado na cena.").ToArray()
            };

        var emoji = initial.ContentType switch { "FILME" => "🎬", "ESPORTE" => "⚽", "PODCAST" => "🎙️", "LIVE" => "🔴", _ => "▶️" };
        var footer = initial.ContentType == "FILME"
            ? "Siga (@{usuario}) para descobrir seu filme favorito"
            : "Siga (@{usuario}) para acompanhar mais conteúdos autorizados.";
        var caption = $"{emoji} {initial.Title}\n\n{initial.Synopsis}\n\n{footer}";
        return initial with { Caption = caption };
    }

    private async Task<Uploaded> UploadAsync(string key, string path, CancellationToken ct)
    {
        var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".mov" => "video/quicktime", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => "video/mp4" };
        var size = new FileInfo(path).Length;
        using var start = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/upload/v1beta/files?key={key}");
        start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        start.Headers.Add("X-Goog-Upload-Command", "start");
        start.Headers.Add("X-Goog-Upload-Header-Content-Length", size.ToString());
        start.Headers.Add("X-Goog-Upload-Header-Content-Type", mime);
        start.Content = new StringContent(JsonSerializer.Serialize(new { file = new { display_name = Path.GetFileName(path) } }), Encoding.UTF8, "application/json");
        using var started = await http.SendAsync(start, ct); started.EnsureSuccessStatusCode();
        var uploadUrl = started.Headers.GetValues("X-Goog-Upload-URL").Single();
        await using var stream = File.OpenRead(path);
        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        upload.Headers.Add("X-Goog-Upload-Offset", "0"); upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        upload.Content = new StreamContent(stream); upload.Content.Headers.ContentLength = size; upload.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        using var response = await http.SendAsync(upload, ct); response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return new Uploaded(json["file"]!["name"]!.GetValue<string>(), json["file"]!["uri"]!.GetValue<string>(), mime);
    }

    private async Task WaitUntilReadyAsync(string key, string name, CancellationToken ct)
    {
        for (var i = 0; i < 80; i++)
        {
            var json = JsonNode.Parse(await http.GetStringAsync($"https://generativelanguage.googleapis.com/v1beta/{name}?key={key}", ct))!;
            var state = json["state"]?.GetValue<string>();
            if (state == "ACTIVE") return;
            if (state == "FAILED") throw new InvalidOperationException("A IA não conseguiu processar a mídia.");
            await Task.Delay(3000, ct);
        }
        throw new TimeoutException("A análise da mídia demorou além do esperado.");
    }

    private async Task<ContentAnalysis> AnalyzeVideoAsync(string key, Uploaded file, string filename, CancellationToken ct)
    {
        var prompt = """
        Analise integralmente esta mídia autorizada: áudio, falas, textos visíveis, rostos, placas, uniformes, créditos, cenário,
        época, atores, personagens e contexto. Classifique como FILME, ESPORTE, PODCAST, LIVE ou OUTRO.

        Regras críticas:
        - Não use o nome do arquivo como evidência.
        - Para FILME/SÉRIE, só preencha identity se reconhecer a obra com sinais fortes: personagem + ator, fala marcante,
          cenário específico, crédito, texto visual ou combinação rara de elementos.
        - Se não tiver certeza do nome exato, deixe identity vazio e crie um título chamativo sobre a cena, sem nome de obra.
        - Nunca chute nome de filme, ator, evento, podcast ou live.
        - Gere título natural em português do Brasil com máximo de 90 caracteres.
        - A sinopse deve explicar a cena em até 300 caracteres, sem prometer identidade que não foi confirmada.
        - Liste 2 a 6 evidências curtas e objetivas.
        """;
        var body = new
        {
            contents = new[] { new { parts = new object[] { new { file_data = new { mime_type = file.Mime, file_uri = file.Uri } }, new { text = prompt + $"\nNome original: {filename}" } } } },
            generationConfig = new { responseMimeType = "application/json", responseSchema = new { type = "OBJECT", properties = new {
                content_type = new { type = "STRING", @enum = new[] { "FILME", "ESPORTE", "PODCAST", "LIVE", "OUTRO" } },
                identity = new { type = "STRING" }, title = new { type = "STRING" }, synopsis = new { type = "STRING" },
                evidence = new { type = "ARRAY", items = new { type = "STRING" } }, confidence = new { type = "NUMBER" }
            }, required = new[] { "content_type", "identity", "title", "synopsis", "evidence", "confidence" } } }
        };
        var parsed = await GenerateJsonAsync(key, body, ct);
        return new ContentAnalysis(parsed["title"]!.GetValue<string>(), "", parsed["content_type"]!.GetValue<string>(),
            parsed["identity"]?.GetValue<string>() ?? "", parsed["synopsis"]!.GetValue<string>(),
            parsed["evidence"]!.AsArray().Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray(),
            parsed["confidence"]!.GetValue<double>());
    }

    private async Task<Verification?> VerifyAsync(string key, ContentAnalysis initial, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(initial.Identity)) return null;
        var prompt = $$"""
        Verifique com pesquisa Google a identidade sugerida para um conteúdo. Não confirme por mera semelhança.
        Tipo: {{initial.ContentType}}; identidade: {{initial.Identity}}; título: {{initial.Title}}; sinopse: {{initial.Synopsis}};
        evidências: {{string.Join(" | ", initial.Evidence)}}.
        Responda somente JSON: {"verified":true|false,"identity":"","title":"","confidence":0.0,"summary":""}.
        """;
        var body = new { contents = new[] { new { parts = new[] { new { text = prompt } } } }, tools = new[] { new { google_search = new { } } }, generationConfig = new { temperature = 0.1, maxOutputTokens = 900 } };
        try
        {
            var json = await GenerateJsonAsync(key, body, ct, schemaMode: false);
            if (!(json["verified"]?.GetValue<bool>() ?? false)) return null;
            return new Verification(json["identity"]?.GetValue<string>() ?? "", json["title"]?.GetValue<string>() ?? initial.Title,
                json["confidence"]?.GetValue<double>() ?? 0, json["summary"]?.GetValue<string>() ?? "");
        }
        catch { return null; }
    }

    private static bool RequiresFactualIdentity(ContentAnalysis analysis)
    {
        if (analysis.ContentType is not ("FILME" or "ESPORTE" or "PODCAST" or "LIVE")) return false;
        if (string.IsNullOrWhiteSpace(analysis.Identity)) return analysis.Confidence < 0.9;
        return analysis.Confidence < 0.97;
    }

    private static string CreativeSceneTitle(ContentAnalysis analysis)
    {
        var synopsis = analysis.Synopsis.Trim();
        if (synopsis.Length > 0)
        {
            var compact = synopsis.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? synopsis;
            if (compact.Length > 86) compact = compact[..86].TrimEnd();
            return compact.Length >= 18 ? compact : $"Cena intensa: {compact}";
        }
        return analysis.ContentType switch
        {
            "FILME" => "Uma cena intensa que prende até o último segundo",
            "ESPORTE" => "Um lance decisivo que mudou o ritmo do jogo",
            "PODCAST" => "Uma fala forte que merece ser ouvida",
            "LIVE" => "Um momento inesperado que chamou atenção",
            _ => "Um momento marcante em destaque"
        };
    }

    private async Task<JsonNode> GenerateJsonAsync(string key, object body, CancellationToken ct, bool schemaMode = true)
    {
        foreach (var model in new[] { "gemini-3.5-flash", "gemini-2.5-flash" })
        {
            using var response = await http.PostAsync($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={key}",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
            if (!response.IsSuccessStatusCode) continue;
            var outer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var text = outer["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>() ?? "";
            var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
            if (start >= 0 && end > start) return JsonNode.Parse(text[start..(end + 1)])!;
        }
        throw new InvalidOperationException("A IA não retornou uma análise válida.");
    }

    private sealed record Uploaded(string Name, string Uri, string Mime);
    private sealed record Verification(string Identity, string Title, double Confidence, string Summary);

    private string? ReadKey()
    {
        var environmentKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey)) return environmentKey.Trim();
        return File.Exists(_keyPath) ? File.ReadAllText(_keyPath).Trim() : null;
    }
}
