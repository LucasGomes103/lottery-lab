using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace LotteryLab.Api.Services;

public sealed record ResultadoFacilLotteryResult(int Contest, DateOnly DrawDate, int[] Numbers,
    Dictionary<int, (int Winners, decimal Prize)> Prizes, decimal? NextEstimate);

/// <summary>
/// Leitura da página de resultado de uma loteria da Caixa no Resultado Fácil (ex.: /loterias/mega-sena).
/// É a última alternativa: o site é acessível a partir do Render, ao contrário da API da Caixa.
/// </summary>
public static class ResultadoFacilLotteryPage
{
    public const string BaseUrl = "https://www.resultadofacil.com.br/loterias/";
    private static readonly Regex Block = new(@"class=""flat-lot""(?<body>.*?)</table>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Date = new(@"<b>(?<date>\d{2}/\d{2}/\d{4})</b>", RegexOptions.Compiled);
    private static readonly Regex Contest = new(@"Concurso\s*<b>(?<contest>\d+)</b>", RegexOptions.Compiled);
    private static readonly Regex Ball = new(@"<span class=""ball""[^>]*>\s*(?<number>\d{1,2})\s*</span>", RegexOptions.Compiled);
    private static readonly Regex Row = new(@"<tr><td>(?<tier>[^<]*)</td><td>(?<winners>[^<]*)</td><td[^>]*>(?<prize>[^<]*)</td></tr>", RegexOptions.Compiled);
    private static readonly Regex Estimate = new(@"Estimativa para o próximo concurso:\s*<b>(?<value>[^<]+)</b>", RegexOptions.Compiled);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <param name="hitsByTier">Acertos de cada faixa, da principal para a menor (ex.: 6, 5, 4).</param>
    public static ResultadoFacilLotteryResult Parse(string html, int numbersCount, int[] hitsByTier)
    {
        var block = Block.Match(html);
        if (!block.Success) throw new InvalidDataException("A página do Resultado Fácil não contém o bloco do resultado.");
        var body = block.Groups["body"].Value;
        var contest = Contest.Match(body);
        var date = Date.Match(body);
        if (!contest.Success || !date.Success) throw new InvalidDataException("A página do Resultado Fácil não contém o concurso ou a data.");
        var numbers = Ball.Matches(body).Select(x => int.Parse(x.Groups["number"].Value, CultureInfo.InvariantCulture)).Order().ToArray();
        if (numbers.Length != numbersCount) throw new InvalidDataException("A página do Resultado Fácil não contém as dezenas sorteadas.");

        var prizes = Enumerable.Range(1, hitsByTier.Length).ToDictionary(x => x, _ => (0, 0m));
        foreach (Match row in Row.Matches(body))
        {
            var hits = Regex.Match(WebUtility.HtmlDecode(row.Groups["tier"].Value), @"(?<hits>\d+)\s*Acertos", RegexOptions.IgnoreCase);
            var tier = hits.Success ? Array.IndexOf(hitsByTier, int.Parse(hits.Groups["hits"].Value, CultureInfo.InvariantCulture)) + 1 : 0;
            if (tier == 0) continue;
            prizes[tier] = (ParseInt(row.Groups["winners"].Value), ParseMoney(row.Groups["prize"].Value) ?? 0m);
        }
        var estimate = Estimate.Match(body);
        return new ResultadoFacilLotteryResult(int.Parse(contest.Groups["contest"].Value, CultureInfo.InvariantCulture),
            DateOnly.ParseExact(date.Groups["date"].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture), numbers, prizes,
            estimate.Success ? ParseMoney(estimate.Groups["value"].Value) : null);
    }

    private static int ParseInt(string value) =>
        int.TryParse(WebUtility.HtmlDecode(value).Trim(), NumberStyles.AllowThousands, PtBr, out var number) ? number : 0;

    // "R$ 19.433,12" → 19433.12; "-" (faixa sem ganhadores) → nulo.
    private static decimal? ParseMoney(string value)
    {
        var text = WebUtility.HtmlDecode(value).Replace("R$", "").Trim();
        return decimal.TryParse(text, NumberStyles.Number, PtBr, out var number) ? number : null;
    }
}
