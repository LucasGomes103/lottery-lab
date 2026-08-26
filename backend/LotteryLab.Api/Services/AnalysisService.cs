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
    var days=rows.GroupBy(x=>x.date.Date).OrderBy(g=>g.Key).ToList(); int tests=0,hits=0;
    for(int i=1;i<days.Count;i++) { var cutoff=days[i].Key; var hist=rows.Where(x=>x.date.Date<cutoff && x.date.Date>=cutoff.AddDays(-windowDays)).ToList(); if(hist.Count==0)continue; var f=hist.GroupBy(x=>x.dezena).OrderByDescending(g=>g.Count()).Take(top).Select(g=>g.Key).ToHashSet(); var actual=days[i].Select(x=>x.dezena).ToHashSet(); tests++; if(f.Overlaps(actual))hits++; }
    return new{tests,hits,hitRate=tests==0?0:Math.Round(100.0*hits/tests,2),warning="Historical descriptive backtest; does not establish future predictability."};
  }
}
