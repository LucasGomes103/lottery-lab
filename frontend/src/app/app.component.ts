import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { forkJoin } from 'rxjs';

interface ParsedResult { position: number; number: string; milhar: string | null; centena: string | null; dezena: string | null; group: number | null; animal: string | null; }
interface ParsedExtraction { date: string | null; bank: string; time: string | null; results: ParsedResult[]; warnings: string[]; }
interface ImportPreview { fileName: string; sourceHash: string; usedOcr: boolean; extractions: ParsedExtraction[]; warnings: string[]; }
interface HistoryItem { id: number; bank: string; extraction_date: string; extraction_time: string; results: number; }
interface HistoryResponse { items: HistoryItem[]; total: number; page: number; pageSize: number; totalPages: number; }
interface ImportQueueItem { id: number; file?: File; fileName: string; status: 'waiting' | 'processing' | 'ready' | 'imported' | 'error'; preview?: ImportPreview; error?: string; }
interface GeneratedNumber { rank: number; milhar: string; centena: string; dezena: string; group: number; selectionType: string; statisticalScore: number; finalScore: number; features: any; reasons: string[]; }
interface GenerationResponse { id: string; algorithm: string; algorithmVersion: number; bank: string; time: string; targetDate: string; windowDays: number; quantity: number; randomSeed: number; sampleExtractions: number; sampleResults: number; robustness: string; composition: any; numbers: GeneratedNumber[]; warning: string; }

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
    queueProcessing = false;
    importQueue: ImportQueueItem[] = [];
    activeQueueId: number | null = null;
    committing = false;
    loadingHistory = false;
    editingId: number | null = null;
    editingIds: number[] = [];
    selectedHistoryIds = new Set<number>();
    history: HistoryItem[] = [];
    activeSection: 'import' | 'history' | 'analysis' | 'predictions' | 'dashboard' = 'import';
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
    generationDate = this.localDate();
    generationQuantity = 10;
    generationWindowDays = 90;
    generation: GenerationResponse | null = null;
    animalTrends: any = null;
    loadingAnimalTrends = false;
    selectedAnimalGroups = new Set<number>();
    predictionHistory: any[] = [];
    selectedPrediction: any = null;
    loadingPrediction = false;
    loadingPredictions = false;
    predictionBank = 'LT NACIONAL';
    predictionDate = '';
    predictionTime = '';
    predictionStatusFilter = '';
    predictionPage = 1;
    predictionPageSize = 20;
    predictionTotal = 0;
    predictionTotalPages = 1;
    selectedPredictionIds = new Set<string>();
    dashboard: any = null;
    loadingDashboard = false;
    dashboardBank = 'LT NACIONAL';
    dashboardStartDate = '';
    dashboardEndDate = '';
    dashboardTime = '';
    generating = false;

    ngOnInit() { this.loadHistory(1); }

    navigate(section: 'import' | 'history' | 'analysis' | 'predictions' | 'dashboard') {
        this.activeSection = section;
        this.error = '';
        this.message = '';
        if (section === 'history') this.loadHistory(this.historyPage);
        if (section === 'predictions') this.loadPredictionHistory(this.predictionPage);
        if (section === 'dashboard') this.loadDashboard();
    }

    select(event: Event) {
        const input = event.target as HTMLInputElement;
        const files = Array.from(input.files || []);
        if (!files.length) return;
        this.editingId = null;
        this.editingIds = [];
        this.error = '';
        this.message = '';
        for (const file of files) this.importQueue.push({ id: Date.now() + Math.random(), file, fileName: file.name, status: 'waiting' });
        input.value = '';
        this.processQueue();
    }

    processQueue() {
        if (this.queueProcessing) return;
        const item = this.importQueue.find(candidate => candidate.status === 'waiting');
        if (!item?.file) return;
        this.queueProcessing = true;
        item.status = 'processing';
        const form = new FormData();
        form.append('file', item.file);
        this.http.post<ImportPreview>(this.api + '/imports/preview', form).subscribe({
            next: preview => {
                this.prepareForReview(preview);
                item.preview = preview;
                item.file = undefined;
                item.status = 'ready';
                if (!this.preview) this.openQueueItem(item.id);
                this.queueProcessing = false;
                this.processQueue();
            },
            error: error => {
                item.error = this.errorMessage(error);
                item.file = undefined;
                item.status = 'error';
                this.queueProcessing = false;
                this.processQueue();
            }
        });
    }

    openQueueItem(id: number) {
        const item = this.importQueue.find(candidate => candidate.id === id);
        if (!item?.preview) return;
        this.preview = item.preview;
        this.activeQueueId = id;
        this.editingId = null;
        this.editingIds = [];
        this.editingIds = [];
        this.activeSection = 'import';
    }

    removeQueueItem(id: number) {
        const item = this.importQueue.find(candidate => candidate.id === id);
        if (!item || item.status === 'processing') return;
        this.importQueue = this.importQueue.filter(candidate => candidate.id !== id);
        if (this.activeQueueId === id) { this.preview = null; this.activeQueueId = null; }
    }

    queueStatus(item: ImportQueueItem) {
        return { waiting: 'Aguardando', processing: 'Lendo PDF...', ready: 'Pronto para revisar', imported: 'Importado', error: 'Falhou' }[item.status];
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
        this.activeQueueId = null;
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
        const request = this.editingIds.length > 1
            ? this.http.put<any>(this.api + '/history/batch', { items: this.editingIds.map((id, index) => ({ id, extraction: this.preview!.extractions[index] })) })
            : this.editingId === null
                ? this.http.post<any>(this.api + '/imports/commit', this.preview)
                : this.http.put<any>(this.api + `/history/${this.editingId}`, this.preview.extractions[0]);
        request.subscribe({
            next: response => {
                this.message = this.editingId === null ? (response.message || `${response.count} extrações importadas com sucesso.`) : this.editingIds.length > 1 ? `${response.count} extrações atualizadas com sucesso.` : 'Extração atualizada com sucesso.';
                const queueItem = this.importQueue.find(item => item.id === this.activeQueueId);
                if (queueItem) { queueItem.status = 'imported'; queueItem.preview = undefined; }
                this.committing = false; this.preview = null; this.editingId = null; this.editingIds = []; this.activeQueueId = null; this.selectedHistoryIds.clear(); this.loadHistory(1);
                const nextReady = this.importQueue.find(item => item.status === 'ready');
                if (nextReady) this.openQueueItem(nextReady.id);
            },
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
            next: response => { this.history = response.items; this.historyTotal = response.total; this.historyPage = response.page; this.historyTotalPages = response.totalPages; this.selectedHistoryIds.clear(); this.loadingHistory = false; },
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
                this.editingIds = [id];
                this.activeQueueId = null;
                this.activeSection = 'import';
                this.error = '';
                window.scrollTo({ top: 0, behavior: 'smooth' });
            },
            error: error => this.error = this.errorMessage(error)
        });
    }

    toggleHistorySelection(id: number, selected: boolean) {
        if (selected) this.selectedHistoryIds.add(id); else this.selectedHistoryIds.delete(id);
    }

    toggleCurrentPageSelection() {
        const allSelected = this.history.length > 0 && this.history.every(item => this.selectedHistoryIds.has(item.id));
        for (const item of this.history) allSelected ? this.selectedHistoryIds.delete(item.id) : this.selectedHistoryIds.add(item.id);
    }

    editSelectedHistory() {
        const ids = this.history.filter(item => this.selectedHistoryIds.has(item.id)).map(item => item.id);
        if (!ids.length) return;
        forkJoin(ids.map(id => this.http.get<ParsedExtraction>(this.api + `/history/${id}`))).subscribe({
            next: extractions => {
                const preview: ImportPreview = { fileName: `Edição de ${ids.length} extrações`, sourceHash: `batch-${Date.now()}`, usedOcr: false, extractions, warnings: ['Você está editando vários registros. Todas as alterações serão salvas juntas.'] };
                this.prepareForReview(preview);
                this.preview = preview;
                this.editingId = ids[0];
                this.editingIds = ids;
                this.activeQueueId = null;
                this.activeSection = 'import';
                this.error = '';
                window.scrollTo({ top: 0, behavior: 'smooth' });
            },
            error: error => this.error = this.errorMessage(error)
        });
    }

    cancelReview() { this.preview = null; this.editingId = null; this.editingIds = []; this.activeQueueId = null; }

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

    generateNumbers() {
        this.generating = true;
        this.error = '';
        const payload = { bank: this.bank, time: this.time, targetDate: this.generationDate,
            windowDays: this.generationWindowDays, quantity: this.generationQuantity,
            groups: Array.from(this.selectedAnimalGroups) };
        this.http.post<GenerationResponse>(this.api + '/predictions/generate', payload).subscribe({
            next: response => { this.generation = response; this.generating = false; },
            error: error => { this.error = this.errorMessage(error); this.generating = false; }
        });
    }

    analyzeAnimalTrends() {
        this.loadingAnimalTrends = true;
        const params = new URLSearchParams({ bank: this.bank, time: this.time, targetDate: this.generationDate,
            windowDays: String(this.generationWindowDays) });
        this.http.get<any>(this.api + `/predictions/animal-trends?${params}`).subscribe({
            next: response => { this.animalTrends = response; this.loadingAnimalTrends = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingAnimalTrends = false; }
        });
    }

    toggleAnimalGroup(group: number, selected: boolean) {
        if (selected) this.selectedAnimalGroups.add(group); else this.selectedAnimalGroups.delete(group);
    }

    clearAnimalGroups() { this.selectedAnimalGroups.clear(); }

    loadPredictionHistory(page = 1) {
        this.predictionPage = Math.max(1, page);
        this.loadingPredictions = true;
        const params = new URLSearchParams({ page: String(this.predictionPage), pageSize: String(this.predictionPageSize) });
        if (this.predictionBank.trim()) params.set('bank', this.predictionBank.trim());
        if (this.predictionDate) params.set('targetDate', this.predictionDate);
        if (this.predictionTime) params.set('time', this.predictionTime);
        if (this.predictionStatusFilter) params.set('status', this.predictionStatusFilter);
        this.http.get<any>(this.api + `/predictions?${params}`).subscribe({
            next: response => {
                this.predictionHistory = response.items || [];
                this.predictionTotal = response.total;
                this.predictionPage = response.page;
                this.predictionTotalPages = response.totalPages;
                this.selectedPredictionIds.clear();
                this.loadingPredictions = false;
            },
            error: error => { this.error = this.errorMessage(error); this.loadingPredictions = false; }
        });
    }

    clearPredictionFilters() {
        this.predictionBank = '';
        this.predictionDate = '';
        this.predictionTime = '';
        this.predictionStatusFilter = '';
        this.loadPredictionHistory(1);
    }

    togglePredictionSelection(id: string, selected: boolean) {
        if (selected) this.selectedPredictionIds.add(id); else this.selectedPredictionIds.delete(id);
    }

    togglePredictionPageSelection() {
        const allSelected = this.predictionHistory.length > 0 && this.predictionHistory.every(item => this.selectedPredictionIds.has(item.id));
        for (const item of this.predictionHistory)
            allSelected ? this.selectedPredictionIds.delete(item.id) : this.selectedPredictionIds.add(item.id);
    }

    deletePrediction(id: string) {
        if (!window.confirm('Excluir esta previsão e toda a conferência associada? A base histórica não será alterada.')) return;
        this.http.delete<any>(this.api + `/predictions/${id}`).subscribe({
            next: response => { this.message = response.message; if (this.selectedPrediction?.prediction?.id === id) this.selectedPrediction = null; this.loadPredictionHistory(this.predictionPage); },
            error: error => this.error = this.errorMessage(error)
        });
    }

    deleteSelectedPredictions() {
        const ids = Array.from(this.selectedPredictionIds);
        if (!ids.length || !window.confirm(`Excluir as ${ids.length} previsões selecionadas? A base histórica não será alterada.`)) return;
        this.http.post<any>(this.api + '/predictions/delete-batch', { ids }).subscribe({
            next: response => { this.message = response.message; this.selectedPrediction = null; this.loadPredictionHistory(this.predictionPage); },
            error: error => this.error = this.errorMessage(error)
        });
    }

    loadDashboard() {
        this.loadingDashboard = true;
        const params = new URLSearchParams();
        if (this.dashboardBank.trim()) params.set('bank', this.dashboardBank.trim());
        if (this.dashboardStartDate) params.set('startDate', this.dashboardStartDate);
        if (this.dashboardEndDate) params.set('endDate', this.dashboardEndDate);
        if (this.dashboardTime) params.set('time', this.dashboardTime);
        this.http.get<any>(this.api + `/predictions/statistics?${params}`).subscribe({
            next: response => { this.dashboard = response; this.loadingDashboard = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingDashboard = false; }
        });
    }

    clearDashboardFilters() {
        this.dashboardBank = '';
        this.dashboardStartDate = '';
        this.dashboardEndDate = '';
        this.dashboardTime = '';
        this.loadDashboard();
    }

    hitRate(hitPredictions: number, evaluated: number) {
        return evaluated ? (100 * hitPredictions / evaluated).toFixed(1) : '0.0';
    }

    viewPrediction(id: string) {
        this.loadingPrediction = true;
        this.selectedPrediction = null;
        this.http.get<any>(this.api + `/predictions/${id}`).subscribe({
            next: response => { this.selectedPrediction = response; this.loadingPrediction = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingPrediction = false; }
        });
    }

    verifyPrediction(id: string) {
        this.loadingPrediction = true;
        this.http.post<any>(this.api + `/predictions/${id}/evaluate`, {}).subscribe({
            next: response => {
                this.selectedPrediction = response;
                this.loadingPrediction = false;
                this.message = response.evaluation ? 'Previsão conferida com o resultado existente na base.' : 'O resultado desse horário ainda não foi importado.';
                this.loadPredictionHistory(this.predictionPage);
            },
            error: error => { this.error = this.errorMessage(error); this.loadingPrediction = false; }
        });
    }

    closePrediction() { this.selectedPrediction = null; }

    predictionStatus(item: any) {
        return item.status === 'EVALUATED' ? 'Conferida' : 'Aguardando resultado';
    }

    matchLabel(matches: Array<{ position: number; number: string }>) {
        return matches?.map(match => `${match.position}º: ${match.number}`).join(', ') || '';
    }

    exportGeneratedPrediction() {
        if (!this.generation) return;
        this.downloadPredictionExcel({
            prediction: {
                id: this.generation.id, bank: this.generation.bank, target_date: this.generation.targetDate,
                target_time: this.generation.time, algorithm_code: this.generation.algorithm,
                algorithm_version: this.generation.algorithmVersion, quantity: this.generation.quantity,
                generated_at: new Date().toISOString(), status: 'PENDING'
            },
            candidates: this.generation.numbers,
            evaluation: null,
            actualResults: []
        });
    }

    exportSelectedPrediction() {
        if (this.selectedPrediction) this.downloadPredictionExcel(this.selectedPrediction);
    }

    private downloadPredictionExcel(detail: any) {
        const prediction = detail.prediction;
        const evaluated = !!detail.evaluation;
        const rows = (detail.candidates || []).map((candidate: any) => {
            const hits = candidate.hits || {};
            return [
                candidate.rank, candidate.milhar, candidate.centena, candidate.dezena,
                `G.${String(candidate.group).padStart(2, '0')}`, candidate.selectionType,
                candidate.statisticalScore, candidate.finalScore, (candidate.reasons || []).join(' | '),
                evaluated ? (hits.milhar ? 'SIM' : 'NÃO') : 'PENDENTE',
                evaluated ? (hits.centena ? 'SIM' : 'NÃO') : 'PENDENTE',
                evaluated ? (hits.dezena ? 'SIM' : 'NÃO') : 'PENDENTE',
                this.matchLabel(hits.milharMatches || []), this.matchLabel(hits.centenaMatches || []),
                this.matchLabel(hits.dezenaMatches || [])
            ];
        });
        const headers = ['Rank', 'Milhar', 'Centena', 'Dezena', 'Grupo/Bicho', 'Estratégia',
            'Score estatístico', 'Score final', 'Principais razões', 'Acertou milhar', 'Acertou centena',
            'Acertou dezena', 'Resultados da milhar', 'Resultados da centena', 'Resultados da dezena'];
        const metadata = [
            ['ID da previsão', prediction.id], ['Banca', prediction.bank],
            ['Data alvo', String(prediction.target_date).slice(0, 10)], ['Horário alvo', String(prediction.target_time).slice(0, 5)],
            ['Algoritmo', `${prediction.algorithm_code} V${prediction.algorithm_version}`],
            ['Quantidade', prediction.quantity], ['Situação', evaluated ? 'CONFERIDA' : 'AGUARDANDO RESULTADO'],
            ['Gerada em', prediction.generated_at || '']
        ];
        const cell = (value: any, style = '') => {
            const numeric = typeof value === 'number';
            return `<Cell${style ? ` ss:StyleID="${style}"` : ''}><Data ss:Type="${numeric ? 'Number' : 'String'}">${this.xmlEscape(value ?? '')}</Data></Cell>`;
        };
        const metadataXml = metadata.map(row => `<Row>${cell(row[0], 'Label')}${cell(row[1])}</Row>`).join('');
        const headerXml = `<Row>${headers.map(value => cell(value, 'Header')).join('')}</Row>`;
        const dataXml = rows.map((row: any[]) => `<Row>${row.map(value => cell(value)).join('')}</Row>`).join('');
        const actualXml = (detail.actualResults || []).map((actual: any) =>
            `<Row>${cell(actual.position)}${cell(actual.number)}${cell(actual.centena)}${cell(actual.dezena)}${cell(`G.${String(actual.group).padStart(2, '0')}`)}</Row>`).join('');
        const workbook = `<?xml version="1.0" encoding="UTF-8"?><?mso-application progid="Excel.Sheet"?>
<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
<Styles><Style ss:ID="Default"><Alignment ss:Vertical="Center"/><Font ss:FontName="Arial" ss:Size="10"/></Style><Style ss:ID="Header"><Font ss:Bold="1" ss:Color="#FFFFFF"/><Interior ss:Color="#111827" ss:Pattern="Solid"/></Style><Style ss:ID="Label"><Font ss:Bold="1"/><Interior ss:Color="#E5E7EB" ss:Pattern="Solid"/></Style></Styles>
<Worksheet ss:Name="Previsão"><Table><Column ss:Width="110"/><Column ss:Width="130"/>${metadataXml}<Row/><Row>${cell('Números previstos', 'Header')}</Row>${headerXml}${dataXml}</Table></Worksheet>
<Worksheet ss:Name="Resultados reais"><Table><Row>${['Posição','Milhar','Centena','Dezena','Grupo/Bicho'].map(value => cell(value, 'Header')).join('')}</Row>${actualXml}</Table></Worksheet>
</Workbook>`;
        const blob = new Blob(['\ufeff', workbook], { type: 'application/vnd.ms-excel;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `previsao-${String(prediction.target_date).slice(0, 10)}-${String(prediction.target_time).slice(0, 5).replace(':', 'h')}.xls`;
        link.click();
        URL.revokeObjectURL(url);
    }

    private xmlEscape(value: any) {
        return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');
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

    private localDate() {
        const now = new Date();
        return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
    }
}
