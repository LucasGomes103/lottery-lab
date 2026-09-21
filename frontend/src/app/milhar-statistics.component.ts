import { Component, Input, OnDestroy, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { Subscription, finalize } from 'rxjs';

interface Configuration { weights: number[]; windows: number[]; windowWeights: number[]; halfLife: number; alpha: number; schedulePrior: number; }
interface Scores { dezena: number; centena: number; digitos: number; recencia: number; horario: number; pares: number; transicao: number; }
interface Candidate { ranking: number; milhar: string; centena: string; dezena: string; scoreFinal: number; scoreGlobal: number; scores: Scores; evidence: { dezenaFrequencies: Record<string, number>; positionalFrequencies: number[]; pairs: Record<string, number>; transitions: unknown; scheduleSample: number; scheduleReliability: number } | null; }
interface RankingRequest { bank: string; time: string; cutoff: string; prize: number | null; top: number; model: string; configuration: Configuration; }
interface Ranking { version: string; request: RankingRequest; sample: number; excludedInvalidNumbers: number; historyEnd: string; effectiveWeights: number[]; ranking: Candidate[]; elapsedMilliseconds: number; }
interface Trial { extractionId: number; date: string; bank: string; time: string; prize: number; actual: string; partition: string; rank: number; milharHits: boolean[]; centenaHits: boolean[]; dezenaHits: boolean[]; historyCount: number; historyEnd: string; }
interface Rate { kind: string; top: number; hits: number; rate: number; baseline: number; difference: number; lift: number; confidenceLow: number | null; confidenceHigh: number | null; }
interface Metrics { tests: number; meanRank: number | null; medianRank: number | null; mrr: number | null; rates: Rate[]; distribution: Record<string, number>; }
interface ModelResult { model: string; configuration: Configuration; all: Metrics; partitions: Record<string, Metrics>; trials: Trial[]; }
interface BacktestRequest { bank: string; time: string; prize: number | null; start: string; end: string; model: string; configuration: Configuration; minimumHistory: number; compareModels: boolean; ablation: boolean; optimize: Configuration[] | null; }
interface Report { version: string; request: BacktestRequest; models: ModelResult[]; winner: Configuration | null; validationSearch: unknown[]; elapsedMilliseconds: number; methodology: string; }
interface DebugReport { history: unknown[]; previous: unknown; ranking: Ranking; actual: unknown; note: string; }

@Component({ selector: 'app-milhar-statistics', standalone: true, imports: [CommonModule, FormsModule],
  templateUrl: './milhar-statistics.component.html', styles: [`
    :host{display:block;margin-bottom:24px}fieldset{border:1px solid var(--border);border-radius:8px;margin:12px 0;padding:12px}
    .weights{display:grid;grid-template-columns:repeat(auto-fit,minmax(100px,1fr));gap:10px}pre{max-height:360px;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;background:var(--soft);padding:12px}
    summary{cursor:pointer;padding:10px 0;font-weight:600}.score{font-variant-numeric:tabular-nums}.notice{margin-top:10px}textarea{width:100%;font:inherit;min-height:90px}h3{margin-top:0}
  `] })
export class MilharStatisticsComponent implements OnDestroy {
  @Input({ required: true }) api = '';
  private http = inject(HttpClient);
  private pending?: Subscription;
  bank = 'LT NACIONAL'; time = '21:00'; prize: number | null = 1;
  cutoff = this.localDateTime(); start = '2026-01-01T00:00'; end = this.localDateTime();
  model = 'C'; top = 100; minimumHistory = 100; ablation = false; optimize = false;
  config: Configuration = { weights: [.25, .15, .15, .15, .1, .1, .1], windows: [10, 30, 50, 100, 0], windowWeights: [.3, .25, .2, .15, .1], halfLife: 30, alpha: 1, schedulePrior: 50 };
  components: (keyof Scores)[] = ['dezena', 'centena', 'digitos', 'recencia', 'horario', 'pares', 'transicao'];
  labels = ['Dezena', 'Centena', 'Dígitos', 'Recência', 'Horário', 'Pares', 'Transição'];
  optimizationJson = ''; loading = false; error = ''; notice = '';
  ranking: Ranking | null = null; selected: Candidate | null = null;
  report: Report | null = null; selectedModel = 'C'; partition = 'test'; page = 0; debug: DebugReport | null = null;
  private localDateTime() { const date = new Date(); return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16); }
  private snapshot<T>(value: T): T { return JSON.parse(JSON.stringify(value)); }
  get weightSum() { return this.config.weights.reduce((a, b) => a + Number(b), 0); }
  private valid() {
    this.error = ''; this.notice = '';
    if (!this.bank.trim() || !this.time || this.config.weights.some(x => x === null || x < 0 || !Number.isFinite(x)) || Math.abs(this.weightSum - 1) > 1e-8) {
      this.error = 'Informe loteria, horário e pesos não negativos que somem 1.'; return false;
    }
    return true;
  }
  analyze() {
    if (!this.valid()) return;
    this.selected = null; this.ranking = null;
    this.send<Ranking>('milhar-ranking', this.snapshot({ bank: this.bank.trim(), time: this.time, prize: this.prize,
      cutoff: this.cutoff, model: this.model, top: this.top, configuration: this.config }), result => this.ranking = result);
  }
  backtest() {
    if (!this.valid()) return;
    let options: Configuration[] | null = null;
    if (this.optimize) {
      try { options = this.optimizationJson.trim() ? JSON.parse(this.optimizationJson) : [10, 30, 50].map(halfLife => ({ ...this.config, halfLife }));
        if (!Array.isArray(options) || options.length < 1 || options.length > 12) throw new Error();
      } catch { this.error = 'A busca deve conter um array JSON de 1 a 12 configurações completas.'; return; }
    }
    this.report = null; this.debug = null;
    this.send<Report>('milhar-backtest', this.snapshot({ bank: this.bank.trim(), time: this.time, prize: this.prize, start: this.start, end: this.end,
      model: this.model, configuration: this.config, minimumHistory: this.minimumHistory, compareModels: true, ablation: this.ablation, optimize: options }), result => {
      this.report = result; this.selectedModel = result.models[0].model; this.partition = 'test'; this.page = 0;
      if (result.winner) {
        try { localStorage.setItem('lottery-lab-milhar-validation', JSON.stringify({ version: result.version, configuration: result.winner, request: result.request, savedAt: new Date().toISOString() }));
          this.notice = 'Configuração vencedora salva neste navegador. Exporte o relatório para guardar a avaliação completa.';
        } catch { this.notice = 'Não foi possível salvar no navegador; exporte o relatório para guardar a configuração.'; }
      }
    });
  }
  loadWinner() {
    try { const saved = JSON.parse(localStorage.getItem('lottery-lab-milhar-validation') || 'null');
      if (!saved?.configuration) throw new Error(); this.config = saved.configuration; this.notice = 'Configuração da última validação carregada.';
    } catch { this.error = 'Não há configuração de validação salva neste navegador.'; }
  }
  inspect(trial: Trial) {
    if (!this.report || !this.currentModel) return;
    this.debug = null;
    this.send<DebugReport>(`milhar-backtest/${trial.extractionId}/debug`, { bank: this.report.request.bank, time: trial.time,
      prize: this.report.request.prize, cutoff: trial.date, top: 100, model: this.currentModel.model, configuration: this.currentModel.configuration }, result => this.debug = result);
  }
  private send<T>(route: string, body: unknown, done: (value: T) => void) {
    this.pending?.unsubscribe(); this.loading = true; this.error = '';
    this.pending = this.http.post<T>(`${this.api}/statistics/${route}`, body).pipe(finalize(() => this.loading = false)).subscribe({
      next: done, error: e => this.error = e.error?.message || 'Não foi possível concluir a análise. Confira os filtros e a conexão.' });
  }
  cancel() { this.pending?.unsubscribe(); this.notice = 'Execução cancelada.'; }
  ngOnDestroy() { this.pending?.unsubscribe(); }
  get currentModel() { return this.report?.models.find(x => x.model === this.selectedModel); }
  metrics(model: ModelResult) { return this.partition === 'all' ? model.all : model.partitions[this.partition]; }
  hit(model: ModelResult, top: number) { return this.metrics(model).rates.find(x => x.kind === 'milhar' && x.top === top)?.rate; }
  get trials() { return this.currentModel?.trials.filter(x => this.partition === 'all' || x.partition === this.partition) || []; }
  get visibleTrials() { return this.trials.slice(this.page * 50, (this.page + 1) * 50); }
  exportReport() {
    if (!this.report) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(this.report, null, 2)], { type: 'application/json' }));
    const link = document.createElement('a'); link.href = url; link.download = 'milhar-backtest.json'; link.click(); URL.revokeObjectURL(url);
  }
}
