import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';

interface ParsedResult { position: number; number: string; milhar: string | null; centena: string | null; dezena: string | null; group: number | null; animal: string | null; }
interface ParsedExtraction { date: string | null; bank: string; time: string | null; results: ParsedResult[]; warnings: string[]; }
interface ImportPreview { fileName: string; sourceHash: string; usedOcr: boolean; extractions: ParsedExtraction[]; warnings: string[]; }
interface HistoryItem { id: number; bank: string; extraction_date: string; extraction_time: string; results: number; }
interface HistoryResponse { items: HistoryItem[]; total: number; page: number; pageSize: number; totalPages: number; }

@Component({ selector: 'app-root', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './app.component.html' })
export class AppComponent implements OnInit {
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
    loadingHistory = false;
    editingId: number | null = null;
    history: HistoryItem[] = [];
    activeSection: 'import' | 'history' | 'analysis' = 'import';
    historyBank = 'LT NACIONAL';
    historyStartDate = '';
    historyEndDate = '';
    historyTime = '';
    historyPage = 1;
    historyPageSize = 20;
    historyTotal = 0;
    historyTotalPages = 1;
    bank = 'LT NACIONAL';
    time = '21:00';
    windowDays = 15;

    ngOnInit() { this.loadHistory(1); }

    navigate(section: 'import' | 'history' | 'analysis') {
        this.activeSection = section;
        this.error = '';
        this.message = '';
        if (section === 'history') this.loadHistory(this.historyPage);
    }

    select(event: Event) {
        const file = (event.target as HTMLInputElement).files?.[0];
        if (!file) return;
        const form = new FormData();
        form.append('file', file);
        this.loading = true;
        this.editingId = null;
        this.preview = null;
        this.error = '';
        this.message = '';
        this.http.post<ImportPreview>(this.api + '/imports/preview', form).subscribe({
            next: preview => { this.prepareForReview(preview); this.preview = preview; this.loading = false; },
            error: error => { this.error = this.errorMessage(error); this.loading = false; }
        });
    }

    startManualImport() {
        const now = new Date();
        const localDate = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
        this.preview = {
            fileName: `insercao-manual-${localDate}`,
            sourceHash: `manual-${Date.now()}`,
            usedOcr: false,
            warnings: ['Preenchimento manual: confira todos os horários e resultados antes de confirmar.'],
            extractions: [{
                bank: 'LT NACIONAL', date: localDate, time: null,
                results: Array.from({ length: 7 }, (_, index) => this.emptyResult(index + 1)),
                warnings: ['Informe o horário e os sete resultados.']
            }]
        };
        this.editingId = null;
        this.error = '';
        this.message = '';
    }

    normalize(result: ParsedResult) {
        const length = result.position === 7 ? 3 : 4;
        const digits = result.number.replace(/\D/g, '');
        if (!digits) { result.number = ''; result.milhar = null; result.centena = null; result.dezena = null; result.group = null; result.animal = null; return; }
        result.number = digits.slice(-length).padStart(length, '0');
        result.dezena = result.number.slice(-2);
        result.centena = result.number.slice(-3);
        result.milhar = result.position === 7 ? null : result.number;
        const value = Number(result.dezena);
        result.group = value === 0 ? 25 : Math.ceil(value / 4);
        const animals = ['AVESTRUZ','AGUIA','BURRO','BORBOLETA','CACHORRO','CABRA','CARNEIRO','CAMELO','COBRA','COELHO','CAVALO','ELEFANTE','GALO','GATO','JACARE','LEAO','MACACO','PORCO','PAVAO','PERU','TOURO','TIGRE','URSO','VEADO','VACA'];
        result.animal = animals[result.group - 1];
    }

    addExtraction() {
        if (!this.preview) return;
        const sample = this.preview.extractions[0];
        this.preview.extractions.push({
            bank: sample?.bank || 'LT NACIONAL',
            date: sample?.date || null,
            time: null,
            results: Array.from({ length: 7 }, (_, index) => this.emptyResult(index + 1)),
            warnings: ['Horário incluído manualmente. Preencha todos os resultados.']
        });
    }

    removeExtraction(index: number) {
        this.preview?.extractions.splice(index, 1);
    }

    commit() {
        if (!this.preview || this.committing) return;
        this.committing = true;
        this.error = '';
        this.message = '';
        const request = this.editingId === null
            ? this.http.post<any>(this.api + '/imports/commit', this.preview)
            : this.http.put<any>(this.api + `/history/${this.editingId}`, this.preview.extractions[0]);
        request.subscribe({
            next: response => { this.message = this.editingId === null ? `${response.count} extrações importadas com sucesso.` : 'Extração atualizada com sucesso.'; this.committing = false; this.preview = null; this.editingId = null; this.loadHistory(1); },
            error: error => { this.error = this.errorMessage(error); this.committing = false; }
        });
    }

    loadHistory(page = 1) {
        this.historyPage = Math.max(1, page);
        this.loadingHistory = true;
        const params = new URLSearchParams({ page: String(this.historyPage), pageSize: String(this.historyPageSize) });
        if (this.historyBank.trim()) params.set('bank', this.historyBank.trim());
        if (this.historyStartDate) params.set('startDate', this.historyStartDate);
        if (this.historyEndDate) params.set('endDate', this.historyEndDate);
        if (this.historyTime) params.set('time', this.historyTime);
        this.http.get<HistoryResponse>(this.api + `/history?${params}`).subscribe({
            next: response => { this.history = response.items; this.historyTotal = response.total; this.historyPage = response.page; this.historyTotalPages = response.totalPages; this.loadingHistory = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingHistory = false; }
        });
    }

    editHistory(id: number) {
        this.http.get<ParsedExtraction>(this.api + `/history/${id}`).subscribe({
            next: extraction => {
                const preview: ImportPreview = { fileName: 'Edição do histórico', sourceHash: `edit-${id}`, usedOcr: false, extractions: [extraction], warnings: ['Você está editando uma extração já gravada.'] };
                this.prepareForReview(preview);
                this.preview = preview;
                this.editingId = id;
                this.activeSection = 'import';
                this.error = '';
                window.scrollTo({ top: 0, behavior: 'smooth' });
            },
            error: error => this.error = this.errorMessage(error)
        });
    }

    cancelReview() { this.preview = null; this.editingId = null; }

    clearHistoryFilters() {
        this.historyBank = '';
        this.historyStartDate = '';
        this.historyEndDate = '';
        this.historyTime = '';
        this.loadHistory(1);
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

    private prepareForReview(preview: ImportPreview) {
        for (const extraction of preview.extractions) {
            for (let position = 1; position <= 7; position++) {
                if (!extraction.results.some(result => result.position === position)) extraction.results.push(this.emptyResult(position));
            }
            extraction.results.sort((left, right) => left.position - right.position);
        }
    }

    private emptyResult(position: number): ParsedResult {
        return { position, number: '', milhar: null, centena: null, dezena: null, group: null, animal: null };
    }
}
