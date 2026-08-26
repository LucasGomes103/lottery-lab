import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
@Component({ selector: 'app-root', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './app.component.html' })
export class AppComponent {
    private http = inject(HttpClient); api = location.hostname === 'localhost' ? 'http://localhost:8080/api' : 'https://lottery-lab.onrender.com/api';
    preview: any = null; forecast: any = null; backtest: any = null; ai = ''; loading = false; bank = 'LT NACIONAL'; time = '21:00'; windowDays = 15;
    select(ev: Event) { const file = (ev.target as HTMLInputElement).files?.[0]; if (!file) return; const fd = new FormData(); fd.append('file', file); this.loading = true; this.http.post(this.api + '/imports/preview', fd).subscribe({ next: x => { this.preview = x; this.loading = false }, error: () => this.loading = false }); }
    commit() { if (!this.preview) return; this.http.post(this.api + '/imports/commit', this.preview).subscribe(() => alert('Importado com sucesso')); }
    analyze() { this.http.get(this.api + `/forecast?bank=${encodeURIComponent(this.bank)}&time=${this.time}&windowDays=${this.windowDays}&top=10`).subscribe(x => this.forecast = x); this.http.get(this.api + `/backtest?bank=${encodeURIComponent(this.bank)}&time=${this.time}&windowDays=${this.windowDays}&top=10`).subscribe(x => this.backtest = x); }
    ask() { this.ai = 'Analisando...'; this.http.post<any>(this.api + '/ai/analyze', { bank: this.bank, time: this.time, windowDays: this.windowDays, question: 'Compare continuidade, atraso, reversão e o ranking híbrido com base nos dados disponíveis.' }).subscribe(x => this.ai = x.answer); }
}
