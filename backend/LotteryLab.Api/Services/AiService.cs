using System.Net.Http.Headers; using System.Text; using System.Text.Json;
namespace LotteryLab.Api.Services;
public sealed class AiService(IHttpClientFactory factory,IConfiguration cfg) {
  public async Task<string> Ask(object context,string? question) {
    var key=Environment.GetEnvironmentVariable("OPENAI_API_KEY"); if(string.IsNullOrWhiteSpace(key)) return "OPENAI_API_KEY não configurada. O motor estatístico funciona sem IA.";
    var model=cfg["OpenAI:Model"]??"gpt-5.6-luna"; var client=factory.CreateClient(); client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
    var prompt=$"Analise estes dados de loteria apenas como estatística descritiva. Não afirme que padrões aumentam a probabilidade real de um sorteio independente. Contexto: {JsonSerializer.Serialize(context)}. Pergunta: {question??"Resuma frequências, atrasos e resultado do backtest."}";
    var body=JsonSerializer.Serialize(new{model,input=prompt}); var resp=await client.PostAsync("https://api.openai.com/v1/responses",new StringContent(body,Encoding.UTF8,"application/json")); var raw=await resp.Content.ReadAsStringAsync(); if(!resp.IsSuccessStatusCode)return $"Erro OpenAI: {resp.StatusCode}";
    using var doc=JsonDocument.Parse(raw); if(doc.RootElement.TryGetProperty("output",out var output)) foreach(var item in output.EnumerateArray()) if(item.TryGetProperty("content",out var content)) foreach(var part in content.EnumerateArray()) if(part.TryGetProperty("text",out var t)) return t.GetString()??raw; return raw;
  }
}
