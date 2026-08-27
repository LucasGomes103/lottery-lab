using Dapper;
using LotteryLab.Api.Data;
using LotteryLab.Api.Models;
namespace LotteryLab.Api.Services;
public sealed class AnalysisService(Db db) {
  public async Task<ForecastResponse> Forecast(string bank,string time,int windowDays,int top) {
    using var c=db.Open();
    var rows=(await c.QueryAsync<dynamic>(@"select e.extraction_date, r.dezena from results r join extractions e on e.id=r.extraction_id where e.bank=@bank and e.extraction_time=@time::time and e.extraction_date >= current_date-@windowDays order by e.extraction_date", new{bank,time,windowDays})).ToList();
    var all=Enumerable.Range(0,100).Select(i=>i.ToString("00")).ToList();
    var freq=rows.GroupBy(x=>(string)x.dezena).ToDictionary(g=>g.Key,g=>g.Count());
    var dates=rows.GroupBy(x=>(string)x.dezena).ToDictionary(g=>g.Key,g=>((DateTime)g.Max(x=>x.extraction_date)).Date);
    int maxFreq=Math.Max(1,freq.Values.DefaultIfEmpty(0).Max());
    var today=DateTime.UtcNow.Date;
    var list=all.Select(v=>{
      var cont=(double)freq.GetValueOrDefault(v)/maxFreq;
      var delay=Math.Min(1.0,(today-(dates.TryGetValue(v,out var ld)?ld:today.AddDays(-windowDays))).TotalDays/Math.Max(1,windowDays));
      var rev=v[1].ToString()+v[0]; var reversal=(double)freq.GetValueOrDefault(rev)/maxFreq;
      var score=.40*cont+.35*delay+.25*reversal;
      return new ForecastCandidate(v,Math.Round(score*100,2),Math.Round(cont*100,2),Math.Round(delay*100,2),Math.Round(reversal*100,2),0);
    }).OrderByDescending(x=>x.Score).Take(top).Select((x,i)=>x with{Rank=i+1}).ToList();
    return new("HYBRID_40_35_25",list,new{sample=rows.Count,windowDays,note="Heuristic ranking; not a probability estimate."});
  }
  public async Task<object> Backtest(string bank,string time,int windowDays,int top) {
    using var c=db.Open();
    var rows=(await c.QueryAsync<(DateTime date,string dezena)>(@"select e.extraction_date as date, r.dezena from results r join extractions e on e.id=r.extraction_id where e.bank=@bank and e.extraction_time=@time::time order by e.extraction_date",new{bank,time})).ToList();
    var days=rows.GroupBy(x=>x.date.Date).OrderBy(g=>g.Key).ToList();
    var outcomes=new List<(DateTime Date,bool Frequency,bool Delay,bool Random)>();
    for(int i=1;i<days.Count;i++) {
      var cutoff=days[i].Key;
      var hist=rows.Where(x=>x.date.Date<cutoff && x.date.Date>=cutoff.AddDays(-windowDays)).ToList();
      if(hist.Count==0)continue;
      var frequency=hist.GroupBy(x=>x.dezena).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key).Take(top).Select(g=>g.Key).ToHashSet();
      var lastSeen=hist.Select((x,index)=>(x.dezena,index)).GroupBy(x=>x.dezena).ToDictionary(g=>g.Key,g=>g.Max(x=>x.index));
      var delay=Enumerable.Range(0,100).Select(x=>x.ToString("00")).OrderBy(x=>lastSeen.GetValueOrDefault(x,-1)).Take(top).ToHashSet();
      var randomGenerator=new Random(HashCode.Combine(bank,time,cutoff,top));
      var random=Enumerable.Range(0,100).OrderBy(_=>randomGenerator.Next()).Take(top).Select(x=>x.ToString("00")).ToHashSet();
      var actual=days[i].Select(x=>x.dezena).ToHashSet();
      outcomes.Add((cutoff,frequency.Overlaps(actual),delay.Overlaps(actual),random.Overlaps(actual)));
    }
    var trainEnd=(int)Math.Floor(outcomes.Count*.6); var validationEnd=(int)Math.Floor(outcomes.Count*.8);
    object Metrics(IEnumerable<(DateTime Date,bool Frequency,bool Delay,bool Random)> source) {
      var data=source.ToList();
      return new { tests=data.Count,
        frequencyHitRate=Rate(data.Count(x=>x.Frequency),data.Count),
        delayHitRate=Rate(data.Count(x=>x.Delay),data.Count),
        randomHitRate=Rate(data.Count(x=>x.Random),data.Count) };
    }
    var hits=outcomes.Count(x=>x.Frequency);
    return new{tests=outcomes.Count,hits,hitRate=Rate(hits,outcomes.Count),
      partitions=new{train=Metrics(outcomes.Take(trainEnd)),validation=Metrics(outcomes.Skip(trainEnd).Take(validationEnd-trainEnd)),test=Metrics(outcomes.Skip(validationEnd))},
      baselines=Metrics(outcomes),
      methodology="Walk-forward cronológico: cada data usa somente observações anteriores; divisão 60% treino, 20% validação e 20% teste.",
      warning="Backtest descritivo fora da amostra; não estabelece previsibilidade futura nem garantia de acerto."};
  }
  private static double Rate(int hits,int tests)=>tests==0?0:Math.Round(100.0*hits/tests,2);
}
