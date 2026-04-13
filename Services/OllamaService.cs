using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ImageTagger.Helpers;

namespace ImageTagger.Services;

public sealed class OllamaService : IDisposable
{
    private readonly HttpClient _http;
    private string _baseUrl;

    public OllamaService(string baseUrl = "http://127.0.0.1:11434")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        // Timeout.InfiniteTimeSpan — we control all timeouts via CancellationTokenSource
        // so the HttpClient's own timer never races against the user-configured timeout.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void SetBaseUrl(string url) => _baseUrl = url.TrimEnd('/');

    // ── Status & model discovery ──────────────────────────────────────────────

    /// <summary>
    /// Checks if Ollama is reachable and returns the list of available models.
    /// Never throws — returns (false, message, empty list) on any failure.
    /// </summary>
    public async Task<(bool Available, string Message, List<string> Models)> CheckStatusAsync()
    {
        try
        {
            // 10-second hard cap for the connectivity check — model loads are not involved here.
            using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", cts.Token);
            resp.EnsureSuccessStatusCode();
            var json   = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            var models = json?["models"]?.AsArray()
                             .Select(m => m?["name"]?.GetValue<string>() ?? "")
                             .Where(s => s.Length > 0)
                             .OrderBy(s => s)
                             .ToList() ?? [];
            return (true, "Connected", models);
        }
        catch (Exception ex)
        {
            var msg = ex is HttpRequestException or TaskCanceledException
                ? "Ollama not reachable — is it running?"
                : ex.Message;
            return (false, msg, []);
        }
    }

    // ── Model warm-up ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a minimal text-only prompt to force the model into GPU memory so the
    /// first real image request does not pay the cold-start penalty.
    /// Returns <c>true</c> if the model responded within <paramref name="timeoutSeconds"/>.
    /// </summary>
    public async Task<bool> WarmUpAsync(string model, int timeoutSeconds, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            model,
            prompt  = ".",          // minimal prompt
            stream  = false,
            options = new { num_predict = 1 },   // generate only 1 token — just enough to load
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var resp = await _http.SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;          // user pressed Stop — propagate
        }
        catch
        {
            return false;   // warm-up timed out or errored — non-fatal
        }
    }

    // ── Image analysis ────────────────────────────────────────────────────────

    /// <summary>
    /// Sends an image to Ollama and returns (long description, condensed title).
    /// LINE 1 is written in <paramref name="language"/>;
    /// LINE 2 (filename title) is always in English.
    /// </summary>
    public async Task<(string LongDescription, string CondensedTitle)> AnalyseImageAsync(
        string imagePath,
        string model,
        string language,
        int    timeoutSeconds,
        int    maxWords,
        CancellationToken ct)
    {
        // Resize to ≤ 1024 px on the longest edge before encoding.
        // Models with dynamic resolution (e.g. Qwen2.5-VL) can generate
        // tens of thousands of visual tokens from a full-resolution JPEG,
        // exhausting 24 GB VRAM and causing infinite load/unload loops.
        // 1024 px preserves enough detail for accurate scene description
        // while keeping visual-token counts manageable on any GPU.
        var imageBytes = await PrepareImageAsync(imagePath, maxDimension: 1024, ct);
        var imageB64   = Convert.ToBase64String(imageBytes);

        var langNote = language.Equals("English", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" Write this sentence in {language}.";

        var prompt =
            "You are an image analysis assistant. Look at this image carefully " +
            "and respond with EXACTLY two lines and nothing else:\n" +
            "LINE 1: A single natural-language sentence (20-40 words) describing " +
            $"the main subject, setting, and any notable details. Be specific and factual.{langNote}\n" +
            "LINE 2: A condensed 4-5 word title in English suitable for a filename " +
            "(Title_Case_With_Underscores, no punctuation, no articles like a/an/the). " +
            "Example: Mountain_Sunset_Golden_Hour\n" +
            "Do not include labels like 'LINE 1:' or 'LINE 2:' in your response.";

        // Use /api/chat so Ollama applies the model's chat template.
        // /api/generate bypasses the template, which causes newer models
        // (Qwen3-VL, Qwen2.5-VL, etc.) to return an empty response field.
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            model,
            stream  = false,
            // 512 tokens: enough for thinking models (Qwen3-VL) to finish their
            // <think> block AND produce the two-line answer we expect.
            options = new { temperature = 0.2, num_predict = 512 },
            messages = new[]
            {
                new
                {
                    role    = "user",
                    content = prompt,
                    images  = new[] { imageB64 },
                }
            },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();

        // Keep the raw JSON string so we can embed it in error messages if needed.
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(responseBody);

        // /api/chat response: { "message": { "role": "assistant", "content": "..." } }
        var raw = json?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";

        // Strip chain-of-thought thinking blocks that some models embed inline in content.
        // These appear as <think>…</think> before the actual answer.  When num_predict
        // cuts the sequence short the closing tag may be absent.
        raw = Regex.Replace(raw, @"<think>[\s\S]*?</think>",
                            "", RegexOptions.IgnoreCase).Trim();
        raw = Regex.Replace(raw, @"<think>[\s\S]*",          // unclosed / truncated
                            "", RegexOptions.IgnoreCase).Trim();

        // Qwen3-VL in Ollama separates chain-of-thought into a dedicated "thinking"
        // field and leaves "content" empty.  The thinking text is raw internal
        // reasoning — using it verbatim produces garbled filenames like
        // "Now_LINE_1_needs_to.jpg".  Instead, extract the structured LINE 1/LINE 2
        // answer that the model planned to emit inside its reasoning.
        if (string.IsNullOrWhiteSpace(raw))
        {
            var thinking = json?["message"]?["thinking"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(thinking))
            {
                var l1 = Regex.Match(thinking, @"LINE\s*1\s*:\s*(.+)", RegexOptions.IgnoreCase);
                var l2 = Regex.Match(thinking, @"LINE\s*2\s*:\s*(.+)", RegexOptions.IgnoreCase);
                if (l1.Success || l2.Success)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    if (l1.Success) parts.Add(l1.Groups[1].Value.Trim());
                    if (l2.Success) parts.Add(l2.Groups[1].Value.Trim());
                    raw = string.Join("\n", parts);
                }
                // No LINE 1/2 pattern found → leave raw empty so a proper error
                // is reported instead of a garbled filename.
            }
        }

        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => StripPromptBleed(l.Trim()))
                       .Where(l => l.Length > 0)
                       .ToList();

        if (lines.Count >= 2)
            return (lines[0], FilenameHelper.SanitizeCondensed(lines[1], maxWords));

        if (lines.Count == 1)
        {
            var words = Regex.Matches(lines[0], @"[A-Za-z0-9]+").Select(m => m.Value).Take(maxWords);
            var auto  = string.Join("_", words.Select(w => char.ToUpper(w[0]) + w[1..]));
            return (lines[0], FilenameHelper.SanitizeCondensed(auto, maxWords));
        }

        // Include the first 300 chars of the raw JSON in the error so the Activity
        // Log shows exactly what Ollama returned — useful for diagnosing model quirks.
        var snippet = responseBody.Length > 300
            ? responseBody[..300] + "…"
            : responseBody;
        throw new InvalidOperationException(
            $"Ollama returned an empty response. Raw JSON: {snippet}");
    }

    // ── Image preparation ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="imagePath"/> and, if either dimension exceeds
    /// <paramref name="maxDimension"/>, down-scales proportionally and re-encodes
    /// as JPEG at 85 % quality.  Small images are returned as-is (zero overhead).
    /// GDI+ operations are CPU-bound, so they run inside Task.Run.
    /// </summary>
    private static async Task<byte[]> PrepareImageAsync(
        string            imagePath,
        int               maxDimension,
        CancellationToken ct)
    {
        var srcBytes = await File.ReadAllBytesAsync(imagePath, ct);

        return await Task.Run(() =>
        {
            using var srcStream = new MemoryStream(srcBytes);
            // false, false = skip colour management + skip slow validation
            using var original  = System.Drawing.Image.FromStream(srcStream, false, false);

            // No resize needed — return original bytes unchanged
            if (original.Width <= maxDimension && original.Height <= maxDimension)
                return srcBytes;

            var ratio     = (float)maxDimension / Math.Max(original.Width, original.Height);
            var newWidth  = (int)Math.Round(original.Width  * ratio);
            var newHeight = (int)Math.Round(original.Height * ratio);

            using var resized = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode      = SmoothingMode.AntiAlias;
                g.DrawImage(original, 0, 0, newWidth, newHeight);
            }

            using var outStream  = new MemoryStream();
            var       jpegCodec  = ImageCodecInfo.GetImageEncoders()
                                       .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encParams  = new EncoderParameters(1);
            encParams.Param[0]   = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            resized.Save(outStream, jpegCodec, encParams);
            return outStream.ToArray();
        }, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StripPromptBleed(string text)
    {
        text = Regex.Replace(text, @"^LINE\s*\d+\s*:\s*",                   "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^A single natural[- ]language[^:]*:\s*","", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^A condensed \d[^:]*:\s*",             "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^(Title|Description|Filename):\s*",    "", RegexOptions.IgnoreCase);
        return text.Trim();
    }

    public void Dispose() => _http.Dispose();
}
