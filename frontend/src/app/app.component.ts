import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';

interface ParsedResult { position: number; number: string; milhar: string | null; centena: string | null; dezena: string | null; group: number | null; animal: string | null; }
interface ParsedExtraction { date: string | null; bank: string; time: string | null; results: ParsedResult[]; warnings: string[]; }
interface ImportPreview { fileName: string; sourceHash: string; usedOcr: boolean; extractions: ParsedExtraction[]; warnings: string[]; }

@Component({ selector: 'app-root', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './app.component.html' })
export class AppComponent {
    private http = inject(HttpClient);
    api = location.hostname === 'localhost' ? 'http://localhost:8080/api' : 'https://lottery-lab.onrender.com/api';
    preview: ImportPreview | null = null;
    forecast: any = null;
    backtest: any = null;
    ai = '';
    message = '';
    error = '';
    loading = false;
    committing = false;
    bank = 'LT NACIONAL';
    time = '21:00';
    windowDays = 15;

    select(event: Event) {
        const file = (event.target as HTMLInputElement).files?.[0];
        if (!file) return;
        const form = new FormData();
        form.append('file', file);
        this.loading = true;
        this.preview = null;
        this.error = '';
        this.message = '';
        this.http.post<ImportPreview>(this.api + '/imports/preview', form).subscribe({
            next: preview => { this.preview = preview; this.loading = false; },
            error: error => { this.error = this.errorMessage(error); this.loading = false; }
        });
    }

    normalize(result: ParsedResult) {
        const length = result.position === 7 ? 3 : 4;
        result.number = result.number.replace(/\D/g, '').slice(-length).padStart(length, '0');
        result.dezena = result.number.slice(-2);
        result.centena = result.number.slice(-3);
        result.milhar = result.position === 7 ? null : result.number;
        const value = Number(result.dezena);
        result.group = value === 0 ? 25 : Math.ceil(value / 4);
        const animals = ['AVESTRUZ','AGUIA','BURRO','BORBOLETA','CACHORRO','CABRA','CARNEIRO','CAMELO','COBRA','COELHO','CAVALO','ELEFANTE','GALO','GATO','JACARE','LEAO','MACACO','PORCO','PAVAO','PERU','TOURO','TIGRE','URSO','VEADO','VACA'];
        result.animal = animals[result.group - 1];
    }

    commit() {
        if (!this.preview || this.committing) return;
        this.committing = true;
        this.error = '';
        this.message = '';
        this.http.post<any>(this.api + '/imports/commit', this.preview).subscribe({
            next: response => { this.message = `${response.count} extrações importadas com sucesso.`; this.committing = false; this.preview = null; },
            error: error => { this.error = this.errorMessage(error); this.committing = false; }
        });
    }

    analyze() {
        this.http.get(this.api + `/forecast?bank=${encodeURIComponent(this.bank)}&time=${this.time}&windowDays=${this.windowDays}&top=10`).subscribe(x => this.forecast = x);
        this.http.get(this.api + `/backtest?bank=${encodeURIComponent(this.bank)}&time=${this.time}&windowDays=${this.windowDays}&top=10`).subscribe(x => this.backtest = x);
    }

    ask() {
        this.ai = 'Analisando...';
        this.http.post<any>(this.api + '/ai/analyze', { bank: this.bank, time: this.time, windowDays: this.windowDays, question: 'Compare continuidade, atraso, reversão e o ranking híbrido com base nos dados disponíveis.' }).subscribe(x => this.ai = x.answer);
    }

    private errorMessage(error: HttpErrorResponse): string {
        const duplicates = error.error?.duplicates as Array<{ bank: string; date: string; time: string }> | undefined;
        if (duplicates?.length) return `${error.error.message} ${duplicates.map(x => `${x.bank} ${x.date} ${x.time}`).join(', ')}`;
        const errors = error.error?.errors as string[] | undefined;
        if (errors?.length) return `${error.error.message} ${errors.join(' ')}`;
        return error.error?.message || 'Não foi possível concluir a operação. Tente novamente.';
    }
}
