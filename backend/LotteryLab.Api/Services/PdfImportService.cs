using System.Text.RegularExpressions;
using LotteryLab.Api.Models;
using UglyToad.PdfPig;
namespace LotteryLab.Api.Services;
public sealed class PdfImportService {
  static readonly Regex DateRx = new(@"\b(?<d>\d{2}/\d{2}/\d{4})\b", RegexOptions.Compiled);
  static readonly Regex TimeRx = new(@"\b(?<h>\d{1,2})\s*H(?:S)?\b", RegexOptions.IgnoreCase|RegexOptions.Compiled);
  static readonly Regex ResultRx = new(@"(?m)^\s*(?<p>[1-7])\s*[:\-]\s*(?<n>[\d\.]{1,6})\s+G\.?\s*(?<g>\d{1,2})\s*$", RegexOptions.IgnoreCase|RegexOptions.Compiled);
  static readonly string[] Animals = ["AVESTRUZ","AGUIA","BURRO","BORBOLETA","CACHORRO","CABRA","CARNEIRO","CAMELO","COBRA","COELHO","CAVALO","ELEFANTE","GALO","GATO","JACARE","LEAO","MACACO","PORCO","PAVAO","PERU","TOURO","TIGRE","URSO","VEADO","VACA"];

  public ImportPreview Parse(Stream stream, string fileName) {
    using var doc = PdfDocument.Open(stream); var text = string.Join("\n", doc.GetPages().Select(p => p.Text));
    var date = DateRx.Match(text); DateOnly? parsedDate = date.Success && DateOnly.TryParseExact(date.Groups["d"].Value,"dd/MM/yyyy",out var d) ? d : null;
    var time = TimeRx.Match(text); var bank = text.Contains("NACIONAL", StringComparison.OrdinalIgnoreCase) ? "LT NACIONAL" : "OUTRA";
    var results = new List<ParsedResult>();
    foreach(Match m in ResultRx.Matches(text)) {
      var n = new string(m.Groups["n"].Value.Where(char.IsDigit).ToArray()).PadLeft(4,'0'); if(n.Length>4)n=n[^4..];
      int g = int.Parse(m.Groups["g"].Value); string? animal = g is >=1 and <=25 ? Animals[g-1] : null;
      results.Add(new(int.Parse(m.Groups["p"].Value), n, n[^2..], n[^3..], g, animal));
    }
    return new(fileName, parsedDate, bank, time.Success ? $"{int.Parse(time.Groups["h"].Value):00}:00" : null, results, text);
  }
}
