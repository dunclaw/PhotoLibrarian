using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.ModelBench;

public static class BenchmarkReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(BenchmarkRun run)
    {
        Directory.CreateDirectory(run.OutputDirectory);
        var thumbnailDirectory = Path.Combine(
            run.OutputDirectory,
            "thumbnails");
        Directory.CreateDirectory(thumbnailDirectory);

        var thumbnails = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var imagePath in run.Images)
        {
            var thumbnailName = CreateThumbnailName(imagePath);
            var thumbnailPath = Path.Combine(
                thumbnailDirectory,
                thumbnailName);
            await WriteThumbnailAsync(imagePath, thumbnailPath);
            thumbnails[imagePath] =
                $"thumbnails/{Uri.EscapeDataString(thumbnailName)}";
        }

        await File.WriteAllTextAsync(
            Path.Combine(run.OutputDirectory, "results.json"),
            JsonSerializer.Serialize(run, JsonOptions),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(run.OutputDirectory, "scorecard.csv"),
            CreateScorecardCsv(run),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(run.OutputDirectory, "predictions.csv"),
            CreatePredictionsCsv(run),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(run.OutputDirectory, "summary.csv"),
            CreateSummaryCsv(run),
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(run.OutputDirectory, "report.html"),
            CreateHtml(run, thumbnails),
            Encoding.UTF8);
    }

    public static string CreateScorecardCsv(BenchmarkRun run)
    {
        var builder = new StringBuilder();
        AppendCsvRow(
            builder,
            "Image",
            "Model",
            "Task",
            "Predictions",
            "PreprocessMs",
            "InferenceMs",
            "QualityScore0To5",
            "Notes",
            "Error",
            "ScoreKind",
            "Threshold",
            "PostprocessMs");
        foreach (var result in run.Results.OrderBy(result =>
            result.RelativeImagePath).ThenBy(result => result.ModelId))
        {
            AppendCsvRow(
                builder,
                result.RelativeImagePath,
                result.ModelName,
                result.Task.ToString(),
                string.Join(
                    "; ",
                    result.Predictions.Select(prediction =>
                        $"{prediction.Label} ({FormatScore(prediction.Confidence, result.ScoreKind)})" +
                        (prediction.AboveThreshold
                            ? string.Empty
                            : " [below threshold]"))),
                Format(result.PreprocessMilliseconds),
                Format(result.InferenceMilliseconds),
                string.Empty,
                string.Empty,
                result.Error ?? string.Empty,
                result.ScoreKind,
                Format(result.Threshold),
                Format(result.PostprocessMilliseconds));
        }

        return builder.ToString();
    }

    public static string CreatePredictionsCsv(BenchmarkRun run)
    {
        var builder = new StringBuilder();
        AppendCsvRow(
            builder,
            "Image",
            "Model",
            "Task",
            "Rank",
            "Label",
            "Confidence",
            "AboveThreshold",
            "X",
            "Y",
            "Width",
            "Height",
            "Correct",
            "Notes",
            "ScoreKind",
            "Threshold");
        foreach (var result in run.Results.OrderBy(result =>
            result.RelativeImagePath).ThenBy(result => result.ModelId))
        {
            foreach (var prediction in result.Predictions)
            {
                AppendCsvRow(
                    builder,
                    result.RelativeImagePath,
                    result.ModelName,
                    result.Task.ToString(),
                    prediction.Rank.ToString(
                        CultureInfo.InvariantCulture),
                    prediction.Label,
                    prediction.Confidence.ToString(
                        "0.000000",
                        CultureInfo.InvariantCulture),
                    prediction.AboveThreshold
                        ? "true"
                        : "false",
                    Format(prediction.X),
                    Format(prediction.Y),
                    Format(prediction.Width),
                    Format(prediction.Height),
                    string.Empty,
                    string.Empty,
                    result.ScoreKind,
                    Format(result.Threshold));
            }
        }

        return builder.ToString();
    }

    public static string CreateSummaryCsv(BenchmarkRun run)
    {
        var builder = new StringBuilder();
        AppendCsvRow(
            builder,
            "Model",
            "Provider",
            "LoadMs",
            "SuccessfulImages",
            "FailedImages",
            "AveragePreprocessMs",
            "AverageInferenceMs",
            "MedianInferenceMs",
            "P95InferenceMs",
            "Error",
            "ModelBytes",
            "AuxiliaryBytes",
            "ScoreKind",
            "Threshold",
            "AveragePostprocessMs",
            "AverageTotalMs",
            "Precision",
            "ModelSha256",
            "VocabularySha256");
        foreach (var model in run.Models)
        {
            AppendCsvRow(
                builder,
                model.ModelName,
                model.Provider,
                Format(model.LoadMilliseconds),
                model.SuccessfulImages.ToString(
                    CultureInfo.InvariantCulture),
                model.FailedImages.ToString(
                    CultureInfo.InvariantCulture),
                Format(model.AveragePreprocessMilliseconds),
                Format(model.AverageInferenceMilliseconds),
                Format(model.MedianInferenceMilliseconds),
                Format(model.P95InferenceMilliseconds),
                model.Error ?? string.Empty,
                model.ModelBytes.ToString(CultureInfo.InvariantCulture),
                model.AuxiliaryBytes.ToString(CultureInfo.InvariantCulture),
                model.ScoreKind,
                Format(model.Threshold),
                Format(model.AveragePostprocessMilliseconds),
                Format(model.AveragePreprocessMilliseconds +
                    model.AverageInferenceMilliseconds +
                    model.AveragePostprocessMilliseconds),
                model.Precision ?? "",
                model.ModelSha256 ?? "",
                model.VocabularySha256 ?? "");
        }

        return builder.ToString();
    }

    private static string CreateHtml(
        BenchmarkRun run,
        IReadOnlyDictionary<string, string> thumbnails)
    {
        var builder = new StringBuilder(
            """
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>PhotoLibrarian model comparison</title>
            <style>
            :root { color-scheme: light dark; font-family: Segoe UI, sans-serif; }
            body { margin: 0; background: Canvas; color: CanvasText; }
            header { position: sticky; top: 0; z-index: 2; padding: 16px 24px; background: Canvas; border-bottom: 1px solid GrayText; }
            h1 { margin: 0 0 8px; font-size: 24px; }
            header p { margin: 4px 0; }
            button { padding: 8px 14px; margin-top: 8px; }
            main { padding: 24px; display: grid; gap: 24px; }
            .photo { display: grid; grid-template-columns: minmax(280px, 440px) 1fr; gap: 20px; border-bottom: 1px solid GrayText; padding-bottom: 24px; }
            .photo img { width: 100%; max-height: 360px; object-fit: contain; background: #202020; }
            .path { overflow-wrap: anywhere; font-weight: 600; margin: 8px 0; }
            .models { display: grid; gap: 12px; }
            .model { border: 1px solid GrayText; border-radius: 6px; padding: 12px; }
            .model h3 { margin: 0 0 4px; }
            .timing { color: GrayText; font-size: 12px; overflow-wrap: anywhere; }
            .predictions { display: flex; flex-wrap: wrap; gap: 6px; margin: 10px 0; padding: 0; list-style: none; }
            .prediction { border: 1px solid GrayText; border-radius: 4px; padding: 5px 7px; }
            .below-threshold { opacity: .62; }
            .score { display: grid; grid-template-columns: auto minmax(180px, 1fr); gap: 8px 12px; align-items: center; }
            .score input, .score select { width: 100%; box-sizing: border-box; padding: 6px; }
            .error { color: #d13438; font-weight: 600; }
            table { border-collapse: collapse; width: 100%; }
            th, td { border-bottom: 1px solid GrayText; padding: 7px; text-align: left; }
            @media (max-width: 900px) { .photo { grid-template-columns: 1fr; } }
            </style>
            </head>
            <body>
            <header>
              <h1>PhotoLibrarian model comparison</h1>
              <p>Score each model from 0 (unusable) to 5 (excellent). Prediction checkboxes mark individual labels as correct.</p>
              <p><strong>Raw labels may include sensitive or inappropriate vocabulary.</strong> This report intentionally bypasses the app safety allowlist.</p>
              <p>SigLIP 2 and TinyCLIP score the same provisional home-photo vocabulary, not RAM++'s full vocabulary.
              Their cutoffs are experimental: cosine similarity and sigmoid match scores are not comparable confidence percentages.
              Review the ranked candidates, including those below the cutoff; no model is automatically approved.</p>
              <button type="button" id="export">Export scored CSV</button>
            </header>
            <main>
            """);

        builder.Append("<p>Build configuration: ");
        builder.Append(WebUtility.HtmlEncode(run.BuildConfiguration));
        builder.Append(". Use Release builds for performance comparisons.</p>");
        builder.Append(
            """
            <section>
              <h2>Performance summary</h2>
              <table>
                <thead><tr><th>Model</th><th>Provider requested</th><th>Assets</th><th>Load</th><th>Average inference</th><th>Median</th><th>P95</th><th>Total/photo</th><th>Failures</th><th>Status</th></tr></thead>
                <tbody>
            """);
        foreach (var model in run.Models)
        {
            builder.Append("<tr><td>");
            builder.Append(WebUtility.HtmlEncode(model.ModelName));
            if (!string.IsNullOrWhiteSpace(model.Precision))
            {
                builder.Append(WebUtility.HtmlEncode($" ({model.Precision})"));
            }
            builder.Append("</td><td>");
            builder.Append(WebUtility.HtmlEncode(model.Provider));
            builder.Append("</td><td>");
            builder.Append($"{(model.ModelBytes + model.AuxiliaryBytes) / 1_000_000d:N1} MB");
            builder.Append("</td><td>");
            builder.Append($"{model.LoadMilliseconds:N1} ms");
            builder.Append("</td><td>");
            builder.Append($"{model.AverageInferenceMilliseconds:N1} ms");
            builder.Append("</td><td>");
            builder.Append($"{model.MedianInferenceMilliseconds:N1} ms");
            builder.Append("</td><td>");
            builder.Append($"{model.P95InferenceMilliseconds:N1} ms");
            builder.Append("</td><td>");
            builder.Append($"{model.AveragePreprocessMilliseconds + model.AverageInferenceMilliseconds + model.AveragePostprocessMilliseconds:N1} ms");
            builder.Append("</td><td>");
            builder.Append(model.FailedImages);
            builder.Append("</td><td>");
            builder.Append(WebUtility.HtmlEncode(model.Error ?? "Ready"));
            builder.Append("</td></tr>");
        }
        builder.Append("</tbody></table></section>");

        foreach (var imagePath in run.Images)
        {
            var relativePath = Path.GetRelativePath(
                run.ImagesDirectory,
                imagePath);
            builder.Append("<section class=\"photo\"><div>");
            builder.Append("<img loading=\"lazy\" src=\"");
            builder.Append(WebUtility.HtmlEncode(thumbnails[imagePath]));
            builder.Append("\" alt=\"");
            builder.Append(WebUtility.HtmlEncode(relativePath));
            builder.Append("\"><div class=\"path\">");
            builder.Append(WebUtility.HtmlEncode(relativePath));
            builder.Append("</div></div><div class=\"models\">");

            foreach (var result in run.Results
                .Where(result =>
                    result.ImagePath.Equals(
                        imagePath,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(result => result.ModelId))
            {
                var key = $"{relativePath}|{result.ModelId}";
                builder.Append("<article class=\"model\" data-key=\"");
                builder.Append(WebUtility.HtmlEncode(key));
                builder.Append("\"><h3>");
                builder.Append(WebUtility.HtmlEncode(result.ModelName));
                builder.Append("</h3><div class=\"timing\">");
                builder.Append(
                    $"Preprocess {result.PreprocessMilliseconds:N1} ms · " +
                    $"inference {result.InferenceMilliseconds:N1} ms · " +
                    $"postprocess {result.PostprocessMilliseconds:N1} ms · " +
                    $"{WebUtility.HtmlEncode(result.Provider)} · " +
                    $"labels: {WebUtility.HtmlEncode(result.LabelSource)}");
                builder.Append("</div>");
                if (result.Task == ModelTask.ZeroShotTagging)
                {
                    builder.Append("<p>");
                    builder.Append(WebUtility.HtmlEncode(
                        $"{result.ScoreKind} score; provisional cutoff {result.Threshold:0.000}. " +
                        "Not a calibrated probability that the tag is correct."));
                    builder.Append("</p>");
                }
                else if (result.ScoreKind == "binary")
                {
                    builder.Append("<p>Detected tags from the model's binary mask, not confidence percentages. " +
                        "Inactive classes are omitted; fewer than the requested maximum is normal.</p>");
                }

                if (result.Error is not null)
                {
                    builder.Append("<p class=\"error\">");
                    builder.Append(WebUtility.HtmlEncode(result.Error));
                    builder.Append("</p>");
                }
                else if (result.Predictions.Count == 0)
                {
                    builder.Append("<p>No predictions above threshold.</p>");
                }
                else
                {
                    builder.Append("<ul class=\"predictions\">");
                    foreach (var prediction in result.Predictions)
                    {
                        builder.Append("<li class=\"prediction");
                        if (!prediction.AboveThreshold)
                        {
                            builder.Append(" below-threshold");
                        }
                        builder.Append("\"><label><input type=\"checkbox\" class=\"correct\" data-rank=\"");
                        builder.Append(prediction.Rank);
                        builder.Append("\"> ");
                        builder.Append(WebUtility.HtmlEncode(prediction.Label));
                        builder.Append(" <small>");
                        builder.Append(FormatScore(prediction.Confidence, result.ScoreKind));
                        if (!prediction.AboveThreshold)
                        {
                            builder.Append(" · below threshold");
                        }
                        if (prediction.X is not null)
                        {
                            builder.Append(
                                $" · box {prediction.X:N0},{prediction.Y:N0} " +
                                $"{prediction.Width:N0}×{prediction.Height:N0}");
                        }
                        builder.Append("</small></label></li>");
                    }

                    builder.Append("</ul>");
                }

                builder.Append(
                    """
                    <div class="score">
                      <label>Quality</label>
                      <select class="quality">
                        <option value="">Unrated</option>
                        <option value="0">0 - unusable</option>
                        <option value="1">1 - mostly wrong</option>
                        <option value="2">2 - weak</option>
                        <option value="3">3 - useful</option>
                        <option value="4">4 - very good</option>
                        <option value="5">5 - excellent</option>
                      </select>
                      <label>Notes</label>
                      <input class="notes" type="text" placeholder="What was missing or incorrect?">
                    </div>
                    """);
                builder.Append("</article>");
            }

            builder.Append("</div></section>");
        }

        builder.Append(
            """
            </main>
            <script>
            const storageKey = "PhotoLibrarian.ModelBench:" + location.pathname;
            const state = JSON.parse(localStorage.getItem(storageKey) || "{}");
            const cards = [...document.querySelectorAll(".model")];
            function save() {
              for (const card of cards) {
                state[card.dataset.key] = {
                  quality: card.querySelector(".quality").value,
                  notes: card.querySelector(".notes").value,
                  correct: [...card.querySelectorAll(".correct")].filter(x => x.checked).map(x => x.dataset.rank)
                };
              }
              localStorage.setItem(storageKey, JSON.stringify(state));
            }
            for (const card of cards) {
              const saved = state[card.dataset.key];
              if (saved) {
                card.querySelector(".quality").value = saved.quality || "";
                card.querySelector(".notes").value = saved.notes || "";
                for (const checkbox of card.querySelectorAll(".correct")) {
                  checkbox.checked = (saved.correct || []).includes(checkbox.dataset.rank);
                }
              }
              card.addEventListener("change", save);
              card.querySelector(".notes").addEventListener("input", save);
            }
            function csv(value) {
              const text = String(value ?? "");
              return '"' + text.replaceAll('"', '""') + '"';
            }
            document.getElementById("export").addEventListener("click", () => {
              save();
              const rows = [["Image", "Model", "QualityScore0To5", "CorrectPredictionRanks", "Notes"]];
              for (const card of cards) {
                const separator = card.dataset.key.lastIndexOf("|");
                const item = state[card.dataset.key] || {};
                rows.push([
                  card.dataset.key.slice(0, separator),
                  card.dataset.key.slice(separator + 1),
                  item.quality || "",
                  (item.correct || []).join(";"),
                  item.notes || ""
                ]);
              }
              const content = rows.map(row => row.map(csv).join(",")).join("\r\n");
              const link = document.createElement("a");
              link.href = URL.createObjectURL(new Blob([content], { type: "text/csv;charset=utf-8" }));
              link.download = "human-scores.csv";
              link.click();
              URL.revokeObjectURL(link.href);
            });
            </script>
            </body>
            </html>
            """);
        return builder.ToString();
    }

    private static async Task WriteThumbnailAsync(
        string sourcePath,
        string destinationPath)
    {
        using var inputStream = File.OpenRead(sourcePath);
        var decoder = await BitmapDecoder.CreateAsync(
            inputStream.AsRandomAccessStream());
        var width = checked((int)decoder.OrientedPixelWidth);
        var height = checked((int)decoder.OrientedPixelHeight);
        var scale = Math.Min(720d / width, 480d / height);
        scale = Math.Min(1, scale);
        var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(height * scale));
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform
            {
                ScaledWidth = (uint)outputWidth,
                ScaledHeight = (uint)outputHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            },
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        var pixels = pixelData.DetachPixelData();

        using var outputStream = File.Create(destinationPath);
        var randomAccessStream = outputStream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.JpegEncoderId,
            randomAccessStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)outputWidth,
            (uint)outputHeight,
            96,
            96,
            pixels);
        await encoder.FlushAsync();
    }

    private static string CreateThumbnailName(string imagePath)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(imagePath)))[..12];
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var safeStem = string.Concat(stem.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character)
                ? '_'
                : character));
        return $"{safeStem}-{hash}.jpg";
    }

    private static void AppendCsvRow(
        StringBuilder builder,
        params string[] values)
    {
        builder.AppendLine(string.Join(
            ',',
            values.Select(value =>
                $"\"{value.Replace("\"", "\"\"")}\"")));
    }

    private static string Format(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string FormatScore(float value, string scoreKind) =>
        scoreKind switch
        {
            "binary" => value == 1 ? "detected" : "not detected",
            "confidence" => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
            _ => value.ToString("0.000", CultureInfo.InvariantCulture) + " " + scoreKind
        };

    private static string Format(float? value) =>
        value?.ToString("0.000", CultureInfo.InvariantCulture) ??
        string.Empty;
}
