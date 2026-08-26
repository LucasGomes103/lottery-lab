using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LotteryLab.Api.Models;
using UglyToad.PdfPig;

namespace LotteryLab.Api.Services;

public sealed class PdfImportService(IConfiguration configuration, ILogger<PdfImportService> logger)
{
    private static readonly Regex DateRx = new(@"\b(?<d>\d{2}/\d{2}/\d{4})\b", RegexOptions.Compiled);
    private static readonly Regex HeaderRx = new(@"(?im)^\s*[>›»]?\s*(?<bank>LT\s+NACIONAL)\s+(?<h>\d{1,2})\s*H(?:S)?\s*$", RegexOptions.Compiled);
    private static readonly Regex ResultRx = new(@"(?im)^\s*(?<p>[1-7])\s*[:\-]\s*(?<n>\d{1,2}(?:[\.\s]\d{3})|\d{1,4})\s+G\s*\.?\s*(?<g>\d{1,2})(?:\s+(?<animal>[A-ZÁÉÍÓÚÃÕÇ]+))?\s*$", RegexOptions.Compiled);
    private static readonly string[] Animals = ["AVESTRUZ", "AGUIA", "BURRO", "BORBOLETA", "CACHORRO", "CABRA", "CARNEIRO", "CAMELO", "COBRA", "COELHO", "CAVALO", "ELEFANTE", "GALO", "GATO", "JACARE", "LEAO", "MACACO", "PORCO", "PAVAO", "PERU", "TOURO", "TIGRE", "URSO", "VEADO", "VACA"];

    public async Task<ImportPreview> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("O arquivo enviado não possui uma assinatura PDF válida.");

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var warnings = new List<string>();
        var text = ExtractText(bytes);
        var extractions = ParseText(text);
        var usedOcr = false;

        if (!HasUsefulExtraction(extractions))
        {
            usedOcr = true;
            warnings.Add("O PDF não possui texto utilizável; a leitura foi realizada por OCR.");
            var ocrCandidates = await ExtractWithOcrAsync(bytes, cancellationToken);
            extractions = MergeOcrExtractions(ocrCandidates.Select(ParseText));
        }

        if (extractions.Count == 0) warnings.Add("Nenhum horário reconhecido. Revise a qualidade do documento.");
        else if (extractions.Any(x => x.Results.Count != 7)) warnings.Add("Um ou mais horários não possuem exatamente sete resultados e precisam de revisão.");
        return new ImportPreview(fileName, hash, usedOcr, extractions, warnings);
    }

    public List<ParsedExtraction> ParseText(string rawText)
    {
        var text = NormalizeOcrText(rawText);
        var dateMatch = DateRx.Match(text);
        DateOnly? date = dateMatch.Success && DateOnly.TryParseExact(dateMatch.Groups["d"].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ? parsedDate : null;
        var headers = HeaderRx.Matches(text).Cast<Match>().ToList();
        var extractions = new List<ParsedExtraction>();

        for (var index = 0; index < headers.Count; index++)
        {
            var header = headers[index];
            var blockStart = header.Index + header.Length;
            var blockEnd = index + 1 < headers.Count ? headers[index + 1].Index : text.Length;
            var results = ParseResults(text[blockStart..blockEnd]);
            var warnings = new List<string>();
            var hour = int.Parse(header.Groups["h"].Value, CultureInfo.InvariantCulture);
            if (date is null) warnings.Add("Data não reconhecida.");
            if (results.Count != 7) warnings.Add($"Foram reconhecidos {results.Count} de 7 resultados.");
            if (results.Select(x => x.Position).Distinct().Count() != results.Count) warnings.Add("Existem posições repetidas.");
            extractions.Add(new ParsedExtraction(date, "LT NACIONAL", hour is >= 0 and <= 23 ? $"{hour:00}:00" : null, results, warnings));
        }
        return extractions;
    }

    private static List<ParsedResult> ParseResults(string block)
    {
        var results = new List<ParsedResult>();
        foreach (Match match in ResultRx.Matches(block))
        {
            var position = int.Parse(match.Groups["p"].Value, CultureInfo.InvariantCulture);
            var digits = new string(match.Groups["n"].Value.Where(char.IsDigit).ToArray());
            var expectedLength = position == 7 ? 3 : 4;
            if (digits.Length > expectedLength) digits = digits[^expectedLength..];
            var number = digits.PadLeft(expectedLength, '0');
            var dezena = number.PadLeft(2, '0')[^2..];
            var centena = number.PadLeft(3, '0')[^3..];
            var milhar = position == 7 ? null : number.PadLeft(4, '0')[^4..];
            var group = GroupFromDezena(dezena);
            var animal = group is >= 1 and <= 25 ? Animals[group - 1] : null;
            results.Add(new ParsedResult(position, number, milhar, centena, dezena, group, animal));
        }
        return results.OrderBy(x => x.Position).ToList();
    }

    private static int GroupFromDezena(string dezena)
    {
        var value = int.Parse(dezena, CultureInfo.InvariantCulture);
        return value == 0 ? 25 : (value + 3) / 4;
    }

    private static bool HasUsefulExtraction(List<ParsedExtraction> extractions) => extractions.Count > 0 && extractions.Sum(x => x.Results.Count) >= extractions.Count * 5;

    private static string ExtractText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var document = PdfDocument.Open(stream);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }

    private static List<ParsedExtraction> MergeOcrExtractions(IEnumerable<List<ParsedExtraction>> candidates)
    {
        var all = candidates.SelectMany(x => x).ToList();
        return all
            .Where(x => x.Time is not null)
            .GroupBy(x => $"{x.Bank}|{x.Date}|{x.Time}")
            .Select(group =>
            {
                var sample = group.First();
                var results = group
                    .SelectMany(x => x.Results)
                    .GroupBy(x => x.Position)
                    .Select(position => position
                        .GroupBy(x => x.Number)
                        .OrderByDescending(numbers => numbers.Count())
                        .ThenByDescending(numbers => numbers.First().Number.Length)
                        .First().First())
                    .OrderBy(x => x.Position)
                    .ToList();
                var warnings = new List<string>();
                if (sample.Date is null) warnings.Add("Data não reconhecida.");
                if (results.Count != 7) warnings.Add($"Foram reconhecidos {results.Count} de 7 resultados.");
                return new ParsedExtraction(sample.Date, sample.Bank, sample.Time, results, warnings);
            })
            .OrderBy(x => x.Time)
            .ToList();
    }

    private async Task<List<string>> ExtractWithOcrAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "lottery-lab", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var pdfPath = Path.Combine(tempRoot, "source.pdf");
        var pagePrefix = Path.Combine(tempRoot, "page");
        await File.WriteAllBytesAsync(pdfPath, bytes, cancellationToken);
        try
        {
            var pdftoppm = configuration["Ocr:PdftoppmPath"] ?? "pdftoppm";
            await RunProcessAsync(pdftoppm, ["-png", "-gray", "-r", "300", pdfPath, pagePrefix], cancellationToken);
            var pages = Directory.GetFiles(tempRoot, "page-*.png").OrderBy(NaturalPageOrder).ToList();
            if (pages.Count == 0) throw new InvalidOperationException("O renderizador não gerou imagens para o OCR.");

            var tesseract = configuration["Ocr:TesseractPath"] ?? "tesseract";
            var language = configuration["Ocr:Language"] ?? "por";
            var outputs = new List<string>();
            foreach (var segmentationMode in new[] { "4", "6", "11" })
            {
                var output = new StringBuilder();
                foreach (var page in pages)
                    output.AppendLine(await RunProcessAsync(tesseract, [page, "stdout", "-l", language, "--psm", segmentationMode, "-c", "preserve_interword_spaces=1"], cancellationToken));
                outputs.Add(output.ToString());
            }
            return outputs;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Falha no OCR do PDF {FileHash}", Convert.ToHexString(SHA256.HashData(bytes))[..12]);
            throw new InvalidOperationException("Não foi possível executar o OCR do PDF. Verifique as dependências do servidor.", exception);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); }
            catch (Exception exception) { logger.LogWarning(exception, "Não foi possível remover o diretório temporário do OCR."); }
        }
    }

    private static int NaturalPageOrder(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
        return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : int.MaxValue;
    }

    private static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Não foi possível iniciar {executable}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} encerrou com código {process.ExitCode}: {error}");
        return output;
    }

    private static string NormalizeOcrText(string text) => text.Replace('\r', '\n').Replace("Á", "A", StringComparison.OrdinalIgnoreCase).Replace("É", "E", StringComparison.OrdinalIgnoreCase).Replace("Í", "I", StringComparison.OrdinalIgnoreCase).Replace("Ó", "O", StringComparison.OrdinalIgnoreCase).Replace("Ú", "U", StringComparison.OrdinalIgnoreCase).Replace("Ç", "C", StringComparison.OrdinalIgnoreCase);
}
