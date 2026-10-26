import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { forkJoin, TimeoutError, timeout } from 'rxjs';
import { ClientPager, DEFAULT_PAGE_SIZE, PagerComponent } from './pager.component';

interface ParsedResult { position: number; number: string; milhar: string | null; centena: string | null; dezena: string | null; group: number | null; animal: string | null; }
interface ParsedExtraction { date: string | null; bank: string; time: string | null; results: ParsedResult[]; warnings: string[]; }
interface ImportPreview { fileName: string; sourceHash: string; usedOcr: boolean; extractions: ParsedExtraction[]; warnings: string[]; }
interface HistoryItem { id: number; bank: string; extraction_date: string; extraction_time: string; results: number; }
interface HistoryResponse { items: HistoryItem[]; total: number; page: number; pageSize: number; totalPages: number; }
interface ImportQueueItem { id: number; file?: File; fileName: string; status: 'waiting' | 'processing' | 'ready' | 'imported' | 'error'; preview?: ImportPreview; error?: string; }
interface GeneratedNumber { rank: number; milhar: string; centena: string; dezena: string; group: number; selectionType: string; statisticalScore: number; finalScore: number; features: any; reasons: string[]; }
interface GenerationResponse { id: string; algorithm: string; algorithmVersion: number; bank: string; time: string; targetDate: string; windowDays: number; quantity: number; randomSeed: number; sampleExtractions: number; sampleResults: number; robustness: string; composition: any; numbers: GeneratedNumber[]; warning: string; betAmount: number; dezenaPayout: number; centenaPayout: number; milharPayout: number; usedRecommendedWindow: boolean; prizeRange: number; dezenaStake: number; centenaStake: number; milharStake: number; }
interface BankDayGenerationResponse { sourcePrediction: GenerationResponse; predictions: GenerationResponse[]; totalBetAmount: number; message: string; }

@Component({ selector: 'app-root', standalone: true, imports: [CommonModule, FormsModule, PagerComponent], templateUrl: './app.component.html' })
export class AppComponent implements OnInit {
    private http = inject(HttpClient);
    api = location.hostname === 'localhost' ? 'http://localhost:8080/api' : 'https://lottery-lab.onrender.com/api';
    preview: ImportPreview | null = null;
    forecast: any = null;
    backtest: any = null;
    decisionBattery: any = null;
    loadingDecisionBattery = false;
    decisionBatteryJob: any = null;
    decisionBatteryMaxEvaluations = 3000;
    decisionBatteryTestFrom = '';
    randomness: any = null;
    loadingRandomness = false;
    randomnessPrizeRange = 5;
    private decisionBatteryPoll: ReturnType<typeof setInterval> | null = null;
    ai = '';
    message = '';
    error = '';
    loading = false;
    queueProcessing = false;
    syncDate = this.localDate();
    syncingExternal = false;
    syncingHistory = false;
    historySyncJob: any = null;
    private historySyncPoll: ReturnType<typeof setInterval> | null = null;
    historySyncBank = 'LT NACIONAL';
    historySyncStartDate = '2026-01-01';
    historySyncEndDate = this.localDate();
    historySyncResult: any = null;
    externalSyncStatus: any = null;
    externalSyncResult: any = null;
    importQueue: ImportQueueItem[] = [];
    activeQueueId: number | null = null;
    committing = false;
    loadingHistory = false;
    editingId: number | null = null;
    editingIds: number[] = [];
    selectedHistoryIds = new Set<number>();
    history: HistoryItem[] = [];
    activeSection: 'import' | 'history' | 'milhar-search' | 'analysis' | 'predictions' | 'megasena' | 'lotofacil' | 'dashboard' | 'users' | 'account' = 'import';
    historyBank = 'LT NACIONAL';
    historyStartDate = '';
    historyEndDate = '';
    historyTime = '';
    historyPage = 1;
    historyPageSize = DEFAULT_PAGE_SIZE;
    historyTotal = 0;
    historyTotalPages = 1;
    milharSearchBank = 'LT NACIONAL';
    milharSearchValue = '';
    milharSearchResult: any = null;
    searchingMilhar = false;
    bank = 'LT NACIONAL';
    time = '21:00';
    analysisTime = '21:00';
    windowDays = 15;
    generationDate = this.localDate();
    generationQuantity = 10;
    ternoQuantity = 10;
    ternoBank = 'LT NACIONAL';
    ternoTime = '21:00';
    ternoDate = this.localDate();
    ternoWindowDays = 240;
    ternoAnimalTrends: any = null;
    loadingTernoAnimalTrends = false;
    selectedTernoAnimalGroups = new Set<number>();
    selectedTernoIds = new Set<string>();
    deletingTernos = false;
    checkingTerno = false;
    ternoHistoryBank = 'LT NACIONAL';
    ternoTotalStake = 10;
    generatingTernos = false;
    loadingTernos = false;
    ternoGeneration: any = null;
    ternoHistory: any[] = [];
    ternoPage = 1;
    ternoPageSize = DEFAULT_PAGE_SIZE;
    ternoTotal = 0;
    ternoTotalPages = 1;
    selectedTerno: any = null;
    generationWindowDays = 240;
    useRecommendedWindow = true;
    betAmount = 30;
    prizeRange = 5;
    dezenaStake = 10;
    centenaStake = 10;
    milharStake = 10;
    // single: só o horário base · same: a mesma lista em todos os horários · each: um cálculo para cada horário
    bankDayMode: 'single' | 'same' | 'each' = 'single';
    get generateForAllBankTimes() { return this.bankDayMode !== 'single'; }
    dayGenerations: GenerationResponse[] = [];
    dezenaPayout = 180;
    centenaPayout = 1800;
    milharPayout = 18000;
    manualBank = 'LT NACIONAL';
    manualTime = '12:00';
    manualDate = this.localDate();
    manualPrizeRange = 5;
    manualDezenaStake = 0;
    manualCentenaStake = 0;
    manualMilharStake = 10;
    manualNumbersText = '';
    savingManualBet = false;
    generation: GenerationResponse | null = null;
    animalTrends: any = null;
    loadingAnimalTrends = false;
    selectedAnimalGroups = new Set<number>();
    predictionHistory: any[] = [];
    selectedPrediction: any = null;
    loadingPrediction = false;
    loadingPredictions = false;
    predictionBank = 'LT NACIONAL';
    predictionDate = this.localDate();
    predictionTime = '';
    predictionStatusFilter = '';
    predictionPage = 1;
    predictionPageSize = DEFAULT_PAGE_SIZE;
    predictionTotal = 0;
    predictionTotalPages = 1;
    selectedPredictionIds = new Set<string>();
    dashboard: any = null;
    dashboardDatePager = new ClientPager();
    candidatePager = new ClientPager();
    milharOccurrencePager = new ClientPager();
    loadingDashboard = false;
    dashboardBank = 'LT NACIONAL';
    dashboardStartDate = '';
    dashboardEndDate = '';
    dashboardTime = '';
    generating = false;
    megaTab: 'generate' | 'fechamento' | 'history' | 'results' = 'generate';
    megaTargetContest: number | null = null;
    megaQuantity = 5;
    megaNumbersPerGame = 6;
    megaWindowContests = 100;
    megaSeed: number | null = null;
    megaGenerating = false;
    megaGeneration: any = null;
    megaPredictions: any[] = [];
    megaPredictionPage = 1;
    megaPredictionPageSize = DEFAULT_PAGE_SIZE;
    megaPredictionTotalPages = 1;
    megaPredictionTotal = 0;
    loadingMegaPredictions = false;
    selectedMegaPrediction: any = null;
    selectedMegaIds = new Set<string>();
    deletingMega = false;
    megaDraws: any[] = [];
    megaDrawPage = 1;
    megaDrawPageSize = DEFAULT_PAGE_SIZE;
    megaDrawTotalPages = 1;
    megaDrawTotal = 0;
    megaLatestContest: number | null = null;
    megaMissingDraws = 0;
    loadingMegaDraws = false;
    megaSyncJob: any = null;
    megaSyncingHistory = false;
    megaSyncingLatest = false;
    private megaSyncPoll: ReturnType<typeof setInterval> | null = null;
    megaStatistics: any = null;
    loadingMegaStatistics = false;
    megaBacktest: any = null;
    loadingMegaBacktest = false;
    megaBacktestContests = 200;
    megaBacktestNumbers = 6;
    megaBacktestQuantity = 5;
    readonly megaBetPrice = 6;
    readonly megaGrid = Array.from({ length: 60 }, (_, i) => i + 1);
    wheelNumbers = new Set<number>();
    wheelGuarantees: any[] = [];
    wheelGuarantee = '4se4';
    wheelSuggestQuantity = 10;
    wheelSuggesting = false;
    wheelPreview: any = null;
    wheelPreviewError = '';
    wheelPreviewLoading = false;
    wheelSaving = false;
    wheelSaved: any = null;
    private wheelPreviewTimer: ReturnType<typeof setTimeout> | null = null;
    private wheelPreviewSequence = 0;
    lotoTab: 'generate' | 'history' | 'results' = 'generate';
    lotoTargetContest: number | null = null;
    lotoQuantity = 5;
    lotoNumbersPerGame = 15;
    lotoWindowContests = 100;
    lotoSeed: number | null = null;
    lotoGenerating = false;
    lotoGeneration: any = null;
    lotoPredictions: any[] = [];
    lotoPredictionPage = 1;
    lotoPredictionPageSize = DEFAULT_PAGE_SIZE;
    lotoPredictionTotalPages = 1;
    lotoPredictionTotal = 0;
    loadingLotoPredictions = false;
    selectedLotoPrediction: any = null;
    selectedLotoIds = new Set<string>();
    deletingLoto = false;
    lotoDraws: any[] = [];
    lotoDrawPage = 1;
    lotoDrawPageSize = DEFAULT_PAGE_SIZE;
    lotoDrawTotalPages = 1;
    lotoDrawTotal = 0;
    lotoLatestContest: number | null = null;
    lotoMissingDraws = 0;
    loadingLotoDraws = false;
    lotoSyncJob: any = null;
    lotoSyncingHistory = false;
    lotoSyncingLatest = false;
    private lotoSyncPoll: ReturnType<typeof setInterval> | null = null;
    lotoStatistics: any = null;
    loadingLotoStatistics = false;
    readonly lotoBetPrice = 3.5;
    readonly lotoSizes = [15, 16, 17, 18, 19, 20];
    authReady = false;
    currentUser: any = null;
    loginUsername = '';
    loginPassword = '';
    loggingIn = false;
    currentPassword = '';
    newPassword = '';
    confirmPassword = '';
    users: any[] = [];
    editingUser: any = null;
    userForm = { username: '', displayName: '', password: '', isActive: true, permissions: [] as string[] };
    readonly permissionOptions = [
        { value: 'imports.write', label: 'Importar e sincronizar resultados' },
        { value: 'history.read', label: 'Consultar a base histórica' },
        { value: 'history.write', label: 'Editar a base histórica' },
        { value: 'analysis.use', label: 'Usar análises e tendências' },
        { value: 'predictions.read', label: 'Consultar previsões' },
        { value: 'predictions.write', label: 'Gerar, verificar e excluir previsões' },
        { value: 'dashboard.read', label: 'Consultar o dashboard' },
        { value: 'users.manage', label: 'Gerenciar usuários' }
    ];

    ngOnInit() {
        const token = localStorage.getItem('lotteryLabToken');
        if (!token) { this.authReady = true; return; }
        this.http.get<any>(this.api + '/auth/me').subscribe({
            next: user => { this.currentUser = user; this.authReady = true; this.afterLogin(); },
            error: () => { localStorage.removeItem('lotteryLabToken'); this.authReady = true; }
        });
    }

    login() {
        if (this.loggingIn) return;
        this.loggingIn = true; this.error = '';
        this.http.post<any>(this.api + '/auth/login', { username: this.loginUsername, password: this.loginPassword }).subscribe({
            next: response => {
                localStorage.setItem('lotteryLabToken', response.token);
                this.currentUser = response.user; this.loginPassword = ''; this.loggingIn = false; this.afterLogin();
            },
            error: error => { this.error = this.errorMessage(error); this.loggingIn = false; }
        });
    }

    logout() {
        this.http.post(this.api + '/auth/logout', {}).subscribe({ next: () => this.finishLogout(), error: () => this.finishLogout() });
    }

    changePassword() {
        if (this.newPassword !== this.confirmPassword) { this.error = 'A confirmação da nova senha não confere.'; return; }
        this.http.post<any>(this.api + '/auth/change-password', { currentPassword: this.currentPassword, newPassword: this.newPassword }).subscribe({
            next: response => { this.message = response.message; this.currentPassword = ''; this.newPassword = ''; this.confirmPassword = ''; this.finishLogout(); },
            error: error => this.error = this.errorMessage(error)
        });
    }

    hasPermission(permission: string) { return this.currentUser?.role === 'ADMIN' || this.currentUser?.permissions?.includes(permission); }

    private afterLogin() {
        this.setNextTarget();
        this.onTernoBankChange(this.ternoBank);
        if (this.currentUser?.mustChangePassword) return;
        if (this.hasPermission('imports.write')) { this.activeSection = 'import'; this.loadExternalSyncStatus(); }
        else if (this.hasPermission('history.read')) { this.activeSection = 'history'; this.loadHistory(1); }
        else if (this.hasPermission('predictions.read')) { this.activeSection = 'predictions'; this.loadPredictionHistory(1); }
        else if (this.hasPermission('dashboard.read')) { this.activeSection = 'dashboard'; this.loadDashboard(); this.loadMegaStatistics(); this.loadLotoStatistics(); }
        else if (this.hasPermission('analysis.use')) this.activeSection = 'analysis';
        if (this.hasPermission('users.manage')) this.loadUsers();
    }

    private finishLogout() { localStorage.removeItem('lotteryLabToken'); this.currentUser = null; }

    navigate(section: 'import' | 'history' | 'milhar-search' | 'analysis' | 'predictions' | 'megasena' | 'lotofacil' | 'dashboard' | 'users' | 'account') {
        this.activeSection = section;
        this.error = '';
        this.message = '';
        if (section === 'history') this.loadHistory(this.historyPage);
        if (section === 'predictions') this.loadPredictionHistory(this.predictionPage);
        if (section === 'dashboard') { this.loadDashboard(); this.loadMegaStatistics(); this.loadLotoStatistics(); }
        if (section === 'megasena') this.openMegaTab(this.megaTab);
        if (section === 'lotofacil') this.openLotoTab(this.lotoTab);
        if (section === 'users') this.loadUsers();
        if (section === 'analysis') this.openAnalysis();
    }

    // Estado visual da tela de Análises: abas, filtros compartilhados e painéis recolhíveis.
    analysisTab: 'generate' | 'result' | 'validation' | 'ranking' = 'generate';
    generatorKind: 'hybrid' | 'terno' = 'hybrid';
    showAdvancedOptions = false;
    showManualBet = false;
    expandedReasonRanks = new Set<number>();
    openValidationDetails = new Set<string>();
    private topNumbersSource: GeneratedNumber[] | null = null;
    private topNumbersRows: GeneratedNumber[] = [];

    private openAnalysis() {
        try {
            const saved = localStorage.getItem('lotteryLabAnalysisTab');
            if (saved === 'generate' || saved === 'result' || saved === 'validation' || saved === 'ranking') this.analysisTab = saved;
        } catch { }
        this.syncAnalysisContext();
    }

    setAnalysisTab(tab: 'generate' | 'result' | 'validation' | 'ranking') {
        this.analysisTab = tab;
        try { localStorage.setItem('lotteryLabAnalysisTab', tab); } catch { }
    }

    // Os filtros do topo alimentam os modelos já usados pelo motor híbrido, pelos ternos e pelo ranking.
    syncAnalysisContext() {
        this.ternoBank = this.bank;
        this.ternoDate = this.generationDate;
        this.ternoTime = this.time;
        this.ternoWindowDays = this.generationWindowDays;
        this.analysisTime = this.time;
        this.windowDays = this.generationWindowDays;
    }

    private resetAnimalSelections() {
        this.animalTrends = null;
        this.selectedAnimalGroups.clear();
        this.ternoAnimalTrends = null;
        this.selectedTernoAnimalGroups.clear();
    }

    setAnalysisBank(bank: string) {
        if (bank === this.bank) return;
        this.onGenerationBankChange(bank);
        this.resetAnimalSelections();
        this.syncAnalysisContext();
    }

    setAnalysisTime(time: string) {
        this.onGenerationTimeChange(time);
        this.resetAnimalSelections();
        this.syncAnalysisContext();
    }

    setAnalysisDate(date: string) {
        this.generationDate = date;
        this.syncAnalysisContext();
    }

    setAnalysisWindow(days: number) {
        this.generationWindowDays = Number(days) || 0;
        this.syncAnalysisContext();
    }

    setRecommendedWindow(enabled: boolean) {
        this.useRecommendedWindow = enabled;
        this.applyRecommendedWindow();
        this.syncAnalysisContext();
    }

    toggleReasons(rank: number) {
        if (this.expandedReasonRanks.has(rank)) this.expandedReasonRanks.delete(rank); else this.expandedReasonRanks.add(rank);
    }

    toggleValidationDetails(key: string) {
        if (this.openValidationDetails.has(key)) this.openValidationDetails.delete(key); else this.openValidationDetails.add(key);
    }

    topNumbers(numbers: GeneratedNumber[] | null | undefined) {
        if (numbers !== this.topNumbersSource) {
            this.topNumbersSource = numbers || null;
            this.topNumbersRows = [...(numbers || [])].sort((a, b) => b.finalScore - a.finalScore).slice(0, 5);
        }
        return this.topNumbersRows;
    }

    animalTitle(animal: any) {
        return `Score ${animal.score ?? '—'}${animal.reasons?.length ? ' · ' + animal.reasons.join(' / ') : ''} · dezenas ${(animal.dezenas || []).join(', ')}`;
    }

    rankingPercent(score: any, rows: any[] | null | undefined) {
        const max = Math.max(0, ...(rows || []).map(row => Math.abs(Number(row.score) || 0)));
        return max ? Math.round(Math.abs(Number(score) || 0) / max * 100) : 0;
    }

    roiBarPercent(value: number, models: any[] | null | undefined) {
        const max = Math.max(0, ...(models || []).map(row => Math.abs(Number(row.roiPercent) || 0)));
        return max ? Math.round(Math.abs(Number(value) || 0) / max * 100) : 0;
    }

    validationVerdict() {
        if (this.decisionBattery?.decision) {
            return String(this.decisionBattery.decision).startsWith('SINAL POSITIVO')
                ? { tone: 'positive', title: 'O modelo mostrou sinal positivo no replay', text: 'Mesmo assim, mantenha só em teste controlado: com mais dados o resultado pode mudar.' }
                : { tone: 'neutral', title: 'O modelo não superou o aleatório', text: 'A diferença está dentro do que o acaso explica. Use as previsões como experimento, não como vantagem.' };
        }
        if (this.randomness?.verdict) {
            return String(this.randomness.verdict).startsWith('DESVIO')
                ? { tone: 'warning', title: 'Foi detectado um desvio nos sorteios', text: 'Confirme com o replay histórico antes de tirar qualquer conclusão.' }
                : { tone: 'neutral', title: 'Os sorteios se comportam como aleatórios', text: 'Nenhum padrão foi encontrado no histórico, então nenhuma fórmula consegue prever melhor que o acaso.' };
        }
        return { tone: 'idle', title: 'Nenhum teste executado ainda', text: 'Execute o replay histórico para saber se o modelo supera o aleatório.' };
    }

    searchMilhar() {
        const milhar = String(this.milharSearchValue || '').replace(/\D/g, '');
        if (milhar.length !== 4) { this.error = 'Informe uma milhar com exatamente quatro dígitos.'; return; }
        this.searchingMilhar = true;
        this.error = '';
        this.milharSearchResult = null;
        const params = new URLSearchParams({ bank: this.milharSearchBank, milhar });
        this.http.get<any>(this.api + `/history/search-milhar?${params}`).subscribe({
            next: response => { this.milharSearchValue = response.milhar; this.milharSearchResult = response; this.milharOccurrencePager.page = 1; this.searchingMilhar = false; },
            error: error => { this.error = this.errorMessage(error); this.searchingMilhar = false; }
        });
    }

    milharPositionsLabel(items: Array<{ position: number }> | null | undefined) {
        return (items || []).map(item => `${item.position}º`).join(', ') || 'Nenhuma';
    }

    loadUsers() {
        this.http.get<any[]>(this.api + '/auth/users').subscribe({ next: users => this.users = users, error: error => this.error = this.errorMessage(error) });
    }

    startCreateUser() {
        this.editingUser = null;
        this.userForm = { username: '', displayName: '', password: '', isActive: true, permissions: [] };
    }

    editUser(user: any) {
        this.editingUser = user;
        this.userForm = { username: user.username, displayName: user.display_name, password: '', isActive: user.is_active, permissions: [...(user.permissions || [])] };
    }

    toggleUserPermission(permission: string, checked: boolean) {
        this.userForm.permissions = checked
            ? Array.from(new Set([...this.userForm.permissions, permission]))
            : this.userForm.permissions.filter(value => value !== permission);
    }

    saveUser() {
        const request = this.editingUser
            ? this.http.put<any>(this.api + `/auth/users/${this.editingUser.id}`, { displayName: this.userForm.displayName, isActive: this.userForm.isActive, permissions: this.userForm.permissions, newPassword: this.userForm.password || null })
            : this.http.post<any>(this.api + '/auth/users', { username: this.userForm.username, displayName: this.userForm.displayName, password: this.userForm.password, permissions: this.userForm.permissions });
        request.subscribe({ next: () => { this.message = this.editingUser ? 'Usuário atualizado.' : 'Usuário criado.'; this.startCreateUser(); this.loadUsers(); }, error: error => this.error = this.errorMessage(error) });
    }

    deleteUser(user: any) {
        if (!window.confirm(`Excluir o usuário ${user.username}?`)) return;
        this.http.delete<any>(this.api + `/auth/users/${user.id}`).subscribe({ next: response => { this.message = response.message; this.loadUsers(); }, error: error => this.error = this.errorMessage(error) });
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
                results: Array.from({ length: 10 }, (_, index) => this.emptyResult(index + 1)),
                warnings: ['Informe o horário e os dez resultados.']
            }]
        };
        this.editingId = null;
        this.activeQueueId = null;
        this.error = '';
        this.message = '';
    }

    syncExternalResults() {
        if (this.syncingExternal) return;
        this.syncingExternal = true;
        this.error = '';
        this.http.post<any>(this.api + `/imports/sync?date=${this.syncDate}`, {}).subscribe({
            next: response => {
                this.externalSyncResult = response;
                this.syncingExternal = false;
                this.message = response.national?.inserted
                    ? `${response.national.inserted} horários ausentes foram importados na atualização das bancas.`
                    : 'A atualização das bancas foi concluída; confira os status por banca.';
                this.loadExternalSyncStatus();
                this.loadHistory(1);
            },
            error: error => { this.error = this.errorMessage(error); this.syncingExternal = false; }
        });
    }

    syncHistoryRange() {
        if (this.syncingHistory) return;
        this.syncingHistory = true;
        this.error = '';
        const params = new URLSearchParams({ bank: this.historySyncBank, startDate: this.historySyncStartDate, endDate: this.historySyncEndDate });
        this.historySyncResult = null;
        this.historySyncJob = null;
        this.http.post<any>(this.api + `/imports/history-sync?${params}`, {}).subscribe({
            next: job => { this.historySyncJob = job; this.pollHistorySync(job.id); },
            error: error => { this.error = this.errorMessage(error); this.syncingHistory = false; }
        });
    }

    private pollHistorySync(id: string) {
        if (this.historySyncPoll) clearInterval(this.historySyncPoll);
        const poll = () => this.http.get<any>(this.api + `/imports/history-sync/jobs/${id}`).subscribe({
            next: job => {
                this.historySyncJob = job;
                if (job.status === 'COMPLETED' || job.status === 'COMPLETED_WITH_ERRORS') { this.historySyncResult = job.result; this.syncingHistory = false; this.stopHistorySyncPoll(); this.loadHistory(1); }
                if (job.status === 'FAILED') { this.error = job.error || 'A carga histórica falhou.'; this.syncingHistory = false; this.stopHistorySyncPoll(); }
            },
            error: error => { this.error = this.errorMessage(error); this.syncingHistory = false; this.stopHistorySyncPoll(); }
        });
        poll(); this.historySyncPoll = setInterval(poll, 1200);
    }

    private stopHistorySyncPoll() {
        if (this.historySyncPoll) clearInterval(this.historySyncPoll);
        this.historySyncPoll = null;
    }

    loadExternalSyncStatus() {
        this.http.get<any>(this.api + '/imports/sync/status').subscribe({
            next: response => this.externalSyncStatus = response,
            error: () => this.externalSyncStatus = null
        });
    }

    setNextTarget() {
        const now = new Date();
        let targetDate = new Date(now.getFullYear(), now.getMonth(), now.getDate());
        let schedules = this.schedulesFor();
        const currentMinutes = now.getHours() * 60 + now.getMinutes();
        let next = schedules.find(value => this.minutes(value) > currentMinutes);
        if (!next) {
            targetDate.setDate(targetDate.getDate() + 1);
            schedules = this.schedulesFor();
            next = schedules[0];
        }
        this.generationDate = `${targetDate.getFullYear()}-${String(targetDate.getMonth() + 1).padStart(2, '0')}-${String(targetDate.getDate()).padStart(2, '0')}`;
        this.time = next;
        this.applyRecommendedWindow();
    }

    schedulesFor(bank = this.bank) {
        return bank === 'LOOK LOTERIAS'
            ? ['07:00', '09:00', '11:00', '14:00', '16:00', '18:00', '21:00', '23:00']
            : ['02:00', '08:00', '10:00', '12:00', '15:00', '17:00', '21:00', '23:00'];
    }

    private minutes(value: string) {
        const [hour, minute] = value.split(':').map(Number);
        return hour * 60 + minute;
    }

    normalize(result: ParsedResult) {
        const length = 4;
        const digits = result.number.replace(/\D/g, '');
        if (!digits) { result.number = ''; result.milhar = null; result.centena = null; result.dezena = null; result.group = null; result.animal = null; return; }
        result.number = digits.slice(-length).padStart(length, '0');
        result.dezena = result.number.slice(-2);
        result.centena = result.number.slice(-3);
        result.milhar = result.number;
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
            results: Array.from({ length: 10 }, (_, index) => this.emptyResult(index + 1)),
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
        this.historyBank = 'LT NACIONAL';
        this.historyStartDate = '';
        this.historyEndDate = '';
        this.historyTime = '';
        this.loadHistory(1);
    }

    analyze() {
        this.http.get(this.api + `/forecast?bank=${encodeURIComponent(this.bank)}&time=${this.analysisTime}&windowDays=${this.windowDays}&top=10`).subscribe(x => this.forecast = x);
        this.http.get(this.api + `/backtest?bank=${encodeURIComponent(this.bank)}&time=${this.analysisTime}&windowDays=${this.windowDays}&top=10`).subscribe(x => this.backtest = x);
    }

    runDecisionBattery() {
        this.loadingDecisionBattery = true;
        this.decisionBattery = null;
        this.decisionBatteryJob = null;
        this.error = '';
        const params = new URLSearchParams({ bank: this.bank, quantity: String(this.generationQuantity),
            betAmount: String(this.betAmount), dezenaPayout: String(this.dezenaPayout),
            centenaPayout: String(this.centenaPayout), milharPayout: String(this.milharPayout),
            prizeRange: String(this.prizeRange), maxEvaluations: String(Math.min(3000, Math.max(100, Number(this.decisionBatteryMaxEvaluations) || 3000))) });
        if (this.decisionBatteryTestFrom) params.set('testFrom', this.decisionBatteryTestFrom);
        this.http.post<any>(this.api + `/predictions/decision-battery/jobs?${params}`, {}).subscribe({
            next: job => { this.decisionBatteryJob = job; this.pollDecisionBattery(job.id); },
            error: error => { this.error = this.errorMessage(error); this.loadingDecisionBattery = false; }
        });
    }

    private pollDecisionBattery(id: string) {
        if (this.decisionBatteryPoll) clearInterval(this.decisionBatteryPoll);
        const poll = () => this.http.get<any>(this.api + `/predictions/decision-battery/jobs/${id}`).subscribe({
            next: job => {
                this.decisionBatteryJob = job;
                if (job.status === 'COMPLETED') { this.decisionBattery = job.result; this.loadingDecisionBattery = false; this.stopDecisionBatteryPoll(); }
                if (job.status === 'FAILED') { this.error = job.error || 'A bateria não foi concluída.'; this.loadingDecisionBattery = false; this.stopDecisionBatteryPoll(); }
            },
            error: error => { this.error = this.errorMessage(error); this.loadingDecisionBattery = false; this.stopDecisionBatteryPoll(); }
        });
        poll();
        this.decisionBatteryPoll = setInterval(poll, 1500);
    }

    runRandomness() {
        this.loadingRandomness = true;
        this.randomness = null;
        this.error = '';
        const params = new URLSearchParams({ bank: this.bank, prizeRange: String(Math.min(10, Math.max(1, Number(this.randomnessPrizeRange) || 5))) });
        this.http.get<any>(this.api + `/analysis/randomness?${params}`).subscribe({
            next: result => { this.randomness = result; this.loadingRandomness = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingRandomness = false; }
        });
    }

    private stopDecisionBatteryPoll() {
        if (this.decisionBatteryPoll) clearInterval(this.decisionBatteryPoll);
        this.decisionBatteryPoll = null;
    }

    get maximumGenerationQuantity() { return (this.selectedAnimalGroups.size || 25) * 400; }

    get maximumTernoQuantity() {
        const n = (this.selectedTernoAnimalGroups.size || 25) * 4;
        return Math.min(10000, n * (n - 1) * (n - 2) / 6);
    }

    get ternoPayoutPreview() {
        return this.ternoQuantity > 0 ? this.ternoTotalStake / this.ternoQuantity * 13000 : 0;
    }

    generateTernos() {
        this.error = '';
        if (!Number.isInteger(this.ternoQuantity) || this.ternoQuantity < 1 || this.ternoQuantity > this.maximumTernoQuantity) {
            this.error = `A seleção permite de 1 a ${this.maximumTernoQuantity} ternos distintos.`;
            return;
        }
        if (!Number.isFinite(this.ternoTotalStake) || this.ternoTotalStake <= 0 || this.ternoTotalStake > 1000000) {
            this.error = 'Informe um valor total positivo de até R$ 1.000.000.';
            return;
        }
        this.generatingTernos = true;
        this.http.post<any>(this.api + '/ternos/generate', {
            bank: this.ternoBank, time: this.ternoTime, targetDate: this.ternoDate,
            quantity: this.ternoQuantity, windowDays: this.ternoWindowDays,
            groups: Array.from(this.selectedTernoAnimalGroups), totalStake: this.ternoTotalStake
        }).subscribe({
            next: result => {
                this.ternoGeneration = result;
                this.generatorKind = 'terno';
                this.setAnalysisTab('result');
                this.generatingTernos = false;
                this.message = `${result.quantity} ternos salvos. Conferência do 1º ao 5º prêmio, cotação 13.000×.`;
            },
            error: error => { this.generatingTernos = false; this.error = this.errorMessage(error); }
        });
    }

    loadTernoHistory(page = 1) {
        if (!this.hasPermission('predictions.read')) return;
        this.loadingTernos = true;
        const params = new URLSearchParams({ bank: this.ternoHistoryBank, page: String(page), pageSize: String(this.ternoPageSize) });
        this.http.get<any>(this.api + `/ternos?${params}`).subscribe({
            next: result => {
                this.ternoHistory = result.items;
                this.selectedTernoIds.clear();
                this.ternoPage = result.page;
                this.ternoTotal = result.total;
                this.ternoTotalPages = result.totalPages;
                this.loadingTernos = false;
                if (this.selectedTerno) {
                    const selected = result.items.find((item: any) => item.id === this.selectedTerno.id);
                    if (selected) this.openTerno(selected.id); else this.selectedTerno = null;
                }
            },
            error: error => { this.loadingTernos = false; this.error = this.errorMessage(error); }
        });
    }

    openTerno(id: string) {
        this.http.get<any>(this.api + `/ternos/${id}`).subscribe({
            next: result => this.selectedTerno = result,
            error: error => this.error = this.errorMessage(error)
        });
    }

    checkTernoInHistory(id: string) {
        this.checkingTerno = true;
        this.http.get<any>(this.api + `/ternos/${id}`).subscribe({
            next: result => {
                if (this.ternoGeneration?.id === id) this.ternoGeneration = result;
                if (this.selectedTerno?.id === id) this.selectedTerno = result;
                this.ternoHistory = this.ternoHistory.map(item => item.id === id ? result : item);
                this.checkingTerno = false;
                this.message = result.status === 'EVALUATED'
                    ? `Conferência concluída: ${result.hits} ternos acertados. Retorno: ${this.money(result.returnAmount)}.`
                    : 'A base ainda não contém todos os resultados do 1º ao 5º para a banca, data e horário deste terno.';
            },
            error: error => { this.checkingTerno = false; this.error = this.errorMessage(error); }
        });
    }

    onTernoBankChange(bank: string) {
        this.ternoBank = bank;
        const now = new Date();
        const schedules = this.schedulesFor(bank);
        const next = schedules.find(value => this.minutes(value) > now.getHours() * 60 + now.getMinutes());
        if (!next) now.setDate(now.getDate() + 1);
        this.ternoTime = next || schedules[0];
        this.ternoDate = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
        this.ternoAnimalTrends = null;
        this.selectedTernoAnimalGroups.clear();
    }

    analyzeTernoAnimalTrends() {
        this.loadingTernoAnimalTrends = true;
        const params = new URLSearchParams({ bank: this.ternoBank, time: this.ternoTime,
            targetDate: this.ternoDate, windowDays: String(this.ternoWindowDays) });
        this.http.get<any>(this.api + `/predictions/animal-trends?${params}`).subscribe({
            next: response => { this.ternoAnimalTrends = response; this.loadingTernoAnimalTrends = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingTernoAnimalTrends = false; }
        });
    }

    toggleTernoAnimalGroup(group: number, selected: boolean) {
        if (selected) this.selectedTernoAnimalGroups.add(group); else this.selectedTernoAnimalGroups.delete(group);
    }

    toggleTernoSelection(id: string, selected: boolean) {
        if (selected) this.selectedTernoIds.add(id); else this.selectedTernoIds.delete(id);
    }

    toggleTernoPageSelection() {
        const all = this.ternoHistory.every(item => this.selectedTernoIds.has(item.id));
        for (const item of this.ternoHistory) this.toggleTernoSelection(item.id, !all);
    }

    deleteTernos(ids = Array.from(this.selectedTernoIds)) {
        if (this.deletingTernos || !ids.length || !window.confirm(`Excluir ${ids.length} registros de ternos e seus jogos?`)) return;
        this.deletingTernos = true;
        this.http.post<any>(this.api + '/ternos/delete-batch', { ids }).subscribe({
            next: result => {
                this.deletingTernos = false;
                this.message = result.message;
                if (ids.includes(this.selectedTerno?.id)) this.selectedTerno = null;
                if (ids.includes(this.ternoGeneration?.id)) this.ternoGeneration = null;
                this.loadTernoHistory(this.ternoPage);
            },
            error: error => { this.deletingTernos = false; this.error = this.errorMessage(error); }
        });
    }

    async copyTernos(terno: any) {
        try {
            await navigator.clipboard.writeText(terno.games.map((game: any) => game.dezenas.join('-')).join('\n'));
            this.message = `${terno.quantity} ternos copiados.`;
        } catch { this.error = 'Não foi possível copiar os ternos. Selecione e copie os jogos da tabela.'; }
    }

    generateNumbers() {
        if (!Number.isInteger(this.generationQuantity) || this.generationQuantity < 1 || this.generationQuantity > this.maximumGenerationQuantity) {
            this.error = `Informe uma quantidade inteira entre 1 e ${this.maximumGenerationQuantity} para os animais selecionados.`;
            return;
        }
        this.recalculatePayouts();
        this.generating = true;
        this.error = '';
        const payload = { bank: this.bank, time: this.time, targetDate: this.generationDate,
            windowDays: this.generationWindowDays, quantity: this.generationQuantity,
            useRecommendedWindow: this.useRecommendedWindow, betAmount: this.betAmount, prizeRange: this.prizeRange,
            dezenaStake: this.dezenaStake, centenaStake: this.centenaStake, milharStake: this.milharStake,
            groups: Array.from(this.selectedAnimalGroups) };
        const mode = this.bankDayMode;
        const endpoint = mode === 'each' ? '/predictions/generate-bank-day-each'
            : mode === 'same' ? '/predictions/generate-bank-day' : '/predictions/generate';
        // Sem resposta em 3 minutos o botão é liberado, em vez de ficar em "Calculando..." para sempre.
        this.http.post<GenerationResponse | BankDayGenerationResponse>(this.api + endpoint, payload).pipe(timeout(180000)).subscribe({
            next: response => {
                const batch = mode !== 'single' ? response as BankDayGenerationResponse : null;
                const generated = batch ? batch.sourcePrediction : response as GenerationResponse;
                this.dayGenerations = mode === 'each' && batch ? batch.predictions : [];
                for (const item of this.dayGenerations.length ? this.dayGenerations : [generated])
                    item.numbers = this.sortByGroupAndMilhar(item.numbers);
                this.generation = generated;
                this.expandedReasonRanks.clear();
                this.generatorKind = 'hybrid';
                this.setAnalysisTab('result');
                this.generationWindowDays = generated.windowDays;
                this.message = batch ? batch.message + ` Total do dia: ${this.money(batch.totalBetAmount)}.` : '';
                this.generating = false;
            },
            error: error => {
                this.error = error instanceof TimeoutError
                    ? 'O servidor demorou mais de 3 minutos para gerar a previsão. Tente novamente em instantes.'
                    : this.errorMessage(error);
                this.generating = false;
            }
        });
    }

    manualNumbersCount() {
        const text = String(this.manualNumbersText || '').replace(/\[[^\]]*\]/g, ' ');
        return Array.from(new Set(text.match(/(?<!\d)\d{4}(?!\d)/g) || [])).slice(0, 100).length;
    }

    manualBetAmount() { return Number(this.manualDezenaStake || 0) + Number(this.manualCentenaStake || 0) + Number(this.manualMilharStake || 0); }

    manualPayout(stake: number, quote: number) {
        const count = this.manualNumbersCount(); const range = Math.min(10, Math.max(1, Number(this.manualPrizeRange) || 1));
        return count ? Math.round(Math.max(0, Number(stake) || 0) / count * quote / range * 100) / 100 : 0;
    }

    saveManualBet() {
        if (!this.manualNumbersCount()) { this.error = 'Cole pelo menos uma milhar de quatro dígitos.'; return; }
        this.savingManualBet = true; this.error = '';
        const payload = { bank: this.manualBank, time: this.manualTime, targetDate: this.manualDate, numbers: this.manualNumbersText,
            prizeRange: this.manualPrizeRange, dezenaStake: this.manualDezenaStake, centenaStake: this.manualCentenaStake, milharStake: this.manualMilharStake };
        this.http.post<GenerationResponse>(this.api + '/predictions/manual', payload).subscribe({
            next: response => { response.numbers = this.sortByGroupAndMilhar(response.numbers); this.generation = response; this.dayGenerations = []; this.savingManualBet = false; this.message = 'Aposta manual registrada e pronta para conferência.'; },
            error: error => { this.error = this.errorMessage(error); this.savingManualBet = false; }
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

    onGenerationTimeChange(value: string) {
        this.time = value;
        this.applyRecommendedWindow();
    }

    onGenerationBankChange(value: string) {
        this.bank = value;
        this.setNextTarget();
        this.analysisTime = this.time;
    }

    applyRecommendedWindow() {
        if (!this.useRecommendedWindow) return;
        const windows: Record<string, number> = { '02:00': 120, '07:00': 180, '08:00': 180, '09:00': 180, '10:00': 240, '11:00': 240, '12:00': 240,
            '14:00': 240, '15:00': 240, '16:00': 240, '17:00': 30, '18:00': 180, '21:00': 240, '23:00': 60 };
        const normalizedTime = String(this.time || '').slice(0, 5);
        this.generationWindowDays = windows[normalizedTime] || 180;
    }

    recalculatePayouts() {
        const range = Math.min(10, Math.max(1, Number(this.prizeRange) || 1));
        const quantity = Math.max(1, Number(this.generationQuantity) || 1);
        this.prizeRange = range;
        this.dezenaStake = Math.max(0, Number(this.dezenaStake) || 0);
        this.centenaStake = Math.max(0, Number(this.centenaStake) || 0);
        this.milharStake = Math.max(0, Number(this.milharStake) || 0);
        this.betAmount = Math.round((this.dezenaStake + this.centenaStake + this.milharStake) * 100) / 100;
        this.dezenaPayout = Math.round(this.dezenaStake / quantity * 90 / range * 100) / 100;
        this.centenaPayout = Math.round(this.centenaStake / quantity * 900 / range * 100) / 100;
        this.milharPayout = Math.round(this.milharStake / quantity * 9000 / range * 100) / 100;
    }

    bankDayBetAmount() { return this.betAmount * this.schedulesFor().length; }

    dayGenerationsTotal() { return this.dayGenerations.reduce((total, item) => total + item.betAmount, 0); }

    showDayGeneration(item: GenerationResponse) {
        this.generation = item;
        this.expandedReasonRanks.clear();
    }

    stakePerNumber(stake: number) {
        const quantity = Math.max(1, Number(this.generationQuantity) || 1);
        return Math.max(0, Number(stake) || 0) / quantity;
    }

    applyFrequentReturnSuggestion() {
        const total = Math.max(0, Number(this.betAmount) || 0);
        this.dezenaStake = total;
        this.centenaStake = 0;
        this.milharStake = 0;
        this.recalculatePayouts();
    }

    expectedReturn() {
        return Math.round(Math.max(0, Number(this.betAmount) || 0) * 0.9 * 100) / 100;
    }

    barPercent(value: number | null | undefined, rows: any[] | null | undefined) {
        const max = Math.max(0, ...(rows || []).map(row => Math.abs(Number(row.profit_amount) || 0)));
        return max ? Math.round(Math.abs(Number(value) || 0) / max * 100) : 0;
    }

    private chronologicalSource: any[] | null = null;
    private chronologicalRows: any[] = [];

    chronological(rows: any[] | null | undefined) {
        if (rows !== this.chronologicalSource) {
            this.chronologicalSource = rows || null;
            this.chronologicalRows = [...(rows || [])].sort((a, b) => String(a.target_date).localeCompare(String(b.target_date)));
        }
        return this.chronologicalRows;
    }

    money(value: number | null | undefined) {
        return (Number(value) || 0).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
    }

    loadPredictionHistory(page = 1) {
        this.loadTernoHistory();
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
        this.predictionBank = 'LT NACIONAL';
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
            next: response => { this.dashboard = response; this.dashboardDatePager.page = 1; this.loadingDashboard = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingDashboard = false; }
        });
    }

    clearDashboardFilters() {
        this.dashboardBank = 'LT NACIONAL';
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
            next: response => { response.candidates = this.sortByGroupAndMilhar(response.candidates); this.selectedPrediction = response; this.candidatePager.page = 1; this.loadingPrediction = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingPrediction = false; }
        });
    }

    verifyPrediction(id: string) {
        this.loadingPrediction = true;
        this.http.post<any>(this.api + `/predictions/${id}/evaluate`, {}).subscribe({
            next: response => {
                response.candidates = this.sortByGroupAndMilhar(response.candidates);
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

    private sortByGroupAndMilhar<T extends { group: number; milhar: string }>(items: T[] | null | undefined) {
        return [...(items || [])].sort((left, right) => Number(left.group) - Number(right.group)
            || String(left.milhar).localeCompare(String(right.milhar), 'pt-BR', { numeric: true }));
    }

    matchLabel(matches: Array<{ time?: string; position: number; number: string }>) {
        return matches?.map(match => `${match.time ? match.time + ' ' : ''}${match.position}º: ${match.number}`).join(', ') || '';
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

    copyGeneratedValues(type: 'milhar' | 'centena' | 'dezena') {
        this.copyValues(this.generation?.numbers || [], type);
    }

    copySelectedValues(type: 'milhar' | 'centena' | 'dezena') {
        this.copyValues(this.selectedPrediction?.candidates || [], type);
    }

    private async copyValues(candidates: any[], type: 'milhar' | 'centena' | 'dezena') {
        const text = candidates.map(candidate => String(candidate[type] ?? '')).filter(Boolean).join(' ');
        if (!text) return;
        try {
            await navigator.clipboard.writeText(text);
        } catch {
            const area = document.createElement('textarea');
            area.value = text; area.style.position = 'fixed'; area.style.opacity = '0';
            document.body.appendChild(area); area.select(); document.execCommand('copy'); area.remove();
        }
        const labels = { milhar: 'milhares', centena: 'centenas', dezena: 'dezenas' };
        this.message = `${candidates.length} ${labels[type]} copiadas, separadas por espaço.`;
        this.error = '';
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
                this.matchLabel(hits.dezenaMatches || []),
                this.matchLabel(candidate.beforeTargetHits?.milharMatches || []),
                this.matchLabel(candidate.beforeTargetHits?.centenaMatches || []),
                this.matchLabel(candidate.beforeTargetHits?.dezenaMatches || []),
                this.matchLabel(candidate.wholeDayHits?.milharMatches || []),
                this.matchLabel(candidate.wholeDayHits?.centenaMatches || []),
                this.matchLabel(candidate.wholeDayHits?.dezenaMatches || [])
            ];
        });
        const headers = ['Rank', 'Milhar', 'Centena', 'Dezena', 'Grupo/Bicho', 'Estratégia',
            'Score estatístico', 'Score final', 'Principais razões', 'Acertou milhar', 'Acertou centena',
            'Acertou dezena', 'Resultados da milhar', 'Resultados da centena', 'Resultados da dezena'];
        headers.push('Milhar antes do horário', 'Centena antes do horário', 'Dezena antes do horário',
            'Milhar no dia', 'Centena no dia', 'Dezena no dia');
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
        this.http.post<any>(this.api + '/ai/analyze', { bank: this.bank, time: this.analysisTime, windowDays: this.windowDays, question: 'Compare continuidade, atraso, reversão e o ranking híbrido com base nos dados disponíveis.' }).subscribe(x => this.ai = x.answer);
    }

    openMegaTab(tab: 'generate' | 'fechamento' | 'history' | 'results') {
        this.megaTab = tab;
        if (tab === 'fechamento' && !this.wheelGuarantees.length) this.loadWheelGuarantees();
        if (tab === 'history') this.loadMegaPredictions(this.megaPredictionPage);
        if (tab === 'results') { this.loadMegaDraws(this.megaDrawPage); if (this.hasPermission('imports.write')) this.loadMegaSyncStatus(); }
    }

    megaCombinations(n: number, k = 6) {
        if (k < 0 || k > n) return 0;
        let result = 1;
        for (let i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return Math.round(result);
    }

    megaGameCost(numbersPerGame = this.megaNumbersPerGame) { return this.megaCombinations(Number(numbersPerGame) || 6) * this.megaBetPrice; }

    megaTotalCost() { return this.megaGameCost() * (Number(this.megaQuantity) || 0); }

    megaBall(value: number) { return String(value).padStart(2, '0'); }

    megaIsHit(game: any, value: number) { return (game.matchedNumbers || []).includes(value); }

    generateMegaSena() {
        if (this.megaGenerating) return;
        this.megaGenerating = true; this.error = ''; this.message = '';
        const body: any = { quantity: Number(this.megaQuantity), numbersPerGame: Number(this.megaNumbersPerGame), windowContests: Number(this.megaWindowContests) };
        if (this.megaTargetContest) body.targetContest = Number(this.megaTargetContest);
        if (this.megaSeed !== null && String(this.megaSeed) !== '') body.seed = Number(this.megaSeed);
        this.http.post<any>(this.api + '/megasena/generate', body).subscribe({
            next: response => { this.megaGeneration = response; this.megaGenerating = false; this.message = `${response.quantity} jogos gerados para o concurso ${response.targetContest}.`; },
            error: error => { this.error = this.errorMessage(error); this.megaGenerating = false; }
        });
    }

    loadWheelGuarantees() {
        this.http.get<any[]>(this.api + '/megasena/fechamento/garantias').subscribe({
            next: guarantees => this.wheelGuarantees = guarantees,
            error: error => this.error = this.errorMessage(error)
        });
    }

    megaBallList(values: number[] | null | undefined) { return (values || []).map(n => this.megaBall(n)).join(' '); }

    toggleWheelNumber(n: number) {
        if (this.wheelNumbers.has(n)) this.wheelNumbers.delete(n);
        else if (this.wheelNumbers.size < 20) this.wheelNumbers.add(n);
        this.wheelNumbers = new Set(this.wheelNumbers);
        this.scheduleWheelPreview();
    }

    selectWheelGuarantee(code: string) { this.wheelGuarantee = code; this.scheduleWheelPreview(); }

    clearWheel() { this.wheelNumbers = new Set<number>(); this.wheelSaved = null; this.scheduleWheelPreview(); }

    suggestWheel() {
        if (this.wheelSuggesting) return;
        const quantity = Math.min(20, Math.max(7, Number(this.wheelSuggestQuantity) || 10));
        this.wheelSuggestQuantity = quantity; this.wheelSuggesting = true; this.error = '';
        this.http.get<any>(this.api + `/megasena/fechamento/sugestao?quantidade=${quantity}`).subscribe({
            next: response => { this.wheelNumbers = new Set<number>(response.numbers); this.wheelSuggesting = false; this.scheduleWheelPreview(); },
            error: error => { this.error = this.errorMessage(error); this.wheelSuggesting = false; }
        });
    }

    // O cálculo pode levar segundos com 20 dezenas: espera o usuário parar de clicar e descarta respostas antigas.
    private scheduleWheelPreview() {
        if (this.wheelPreviewTimer) clearTimeout(this.wheelPreviewTimer);
        const sequence = ++this.wheelPreviewSequence;
        this.wheelPreviewError = '';
        if (this.wheelNumbers.size < 7 || this.wheelNumbers.size > 20) { this.wheelPreview = null; this.wheelPreviewLoading = false; return; }
        this.wheelPreviewLoading = true;
        this.wheelPreviewTimer = setTimeout(() => {
            this.http.post<any>(this.api + '/megasena/fechamento/preview', { numbers: [...this.wheelNumbers], guarantee: this.wheelGuarantee }).subscribe({
                next: response => { if (sequence !== this.wheelPreviewSequence) return; this.wheelPreview = response; this.wheelPreviewLoading = false; },
                error: error => { if (sequence !== this.wheelPreviewSequence) return; this.wheelPreview = null; this.wheelPreviewError = this.errorMessage(error); this.wheelPreviewLoading = false; }
            });
        }, 500);
    }

    saveWheel() {
        if (this.wheelSaving || this.wheelNumbers.size < 7 || this.wheelNumbers.size > 20) return;
        this.wheelSaving = true; this.error = ''; this.message = '';
        this.http.post<any>(this.api + '/megasena/fechamento', { numbers: [...this.wheelNumbers], guarantee: this.wheelGuarantee }).subscribe({
            next: response => { this.wheelSaved = response; this.wheelSaving = false; this.message = `Fechamento com ${response.quantity} jogos salvo para o concurso ${response.targetContest}.`; },
            error: error => { this.error = this.errorMessage(error); this.wheelSaving = false; }
        });
    }

    copyMegaGames(source: any) {
        const text = (source?.games || []).map((game: any) => game.numbers.map((n: number) => this.megaBall(n)).join(' ')).join('\n');
        navigator.clipboard.writeText(text).then(() => this.message = 'Jogos copiados.', () => this.error = 'Não foi possível copiar os jogos.');
    }

    loadMegaPredictions(page = 1) {
        this.loadingMegaPredictions = true;
        this.http.get<any>(this.api + `/megasena/predictions?page=${Math.max(1, page)}&pageSize=${this.megaPredictionPageSize}`).subscribe({
            next: response => {
                this.megaPredictions = response.items; this.megaPredictionPage = response.page;
                this.megaPredictionTotalPages = response.totalPages; this.megaPredictionTotal = response.total;
                this.selectedMegaIds = new Set([...this.selectedMegaIds].filter(id => response.items.some((x: any) => x.id === id)));
                this.loadingMegaPredictions = false;
            },
            error: error => { this.error = this.errorMessage(error); this.loadingMegaPredictions = false; }
        });
    }

    openMegaPrediction(id: string) {
        this.http.get<any>(this.api + `/megasena/predictions/${id}`).subscribe({
            next: response => this.selectedMegaPrediction = response,
            error: error => this.error = this.errorMessage(error)
        });
    }

    toggleMegaSelection(id: string, checked: boolean) {
        if (checked) this.selectedMegaIds.add(id); else this.selectedMegaIds.delete(id);
        this.selectedMegaIds = new Set(this.selectedMegaIds);
    }

    toggleAllMega(checked: boolean) {
        this.selectedMegaIds = checked ? new Set(this.megaPredictions.map(x => x.id)) : new Set<string>();
    }

    deleteSelectedMega() {
        if (!this.selectedMegaIds.size || this.deletingMega) return;
        if (!window.confirm(`Excluir ${this.selectedMegaIds.size} registro(s) de jogos da Mega-Sena?`)) return;
        this.deletingMega = true;
        this.http.post<any>(this.api + '/megasena/predictions/delete-batch', { ids: [...this.selectedMegaIds] }).subscribe({
            next: response => {
                this.message = `${response.deleted} registro(s) excluído(s).`; this.deletingMega = false;
                if (this.selectedMegaPrediction && this.selectedMegaIds.has(this.selectedMegaPrediction.id)) this.selectedMegaPrediction = null;
                this.selectedMegaIds = new Set<string>(); this.loadMegaPredictions(this.megaPredictionPage);
            },
            error: error => { this.error = this.errorMessage(error); this.deletingMega = false; }
        });
    }

    loadMegaDraws(page = 1) {
        this.loadingMegaDraws = true;
        this.http.get<any>(this.api + `/megasena/draws?page=${Math.max(1, page)}&pageSize=${this.megaDrawPageSize}`).subscribe({
            next: response => {
                this.megaDraws = response.items; this.megaDrawPage = response.page; this.megaDrawTotalPages = response.totalPages;
                this.megaDrawTotal = response.total; this.megaLatestContest = response.latestContest; this.megaMissingDraws = response.missing;
                this.loadingMegaDraws = false;
            },
            error: error => { this.error = this.errorMessage(error); this.loadingMegaDraws = false; }
        });
    }

    syncMegaLatest() {
        if (this.megaSyncingLatest) return;
        this.megaSyncingLatest = true; this.error = '';
        this.http.post<any>(this.api + '/megasena/sync-latest', {}).subscribe({
            next: draw => { this.megaSyncingLatest = false; this.message = `Concurso ${draw.contest} sincronizado.`; this.loadMegaDraws(1); },
            error: error => { this.error = this.errorMessage(error); this.megaSyncingLatest = false; }
        });
    }

    syncMegaHistory() {
        if (this.megaSyncingHistory) return;
        this.megaSyncingHistory = true; this.error = '';
        this.http.post<any>(this.api + '/megasena/sync-history', {}).subscribe({
            next: job => { this.megaSyncJob = job; this.pollMegaSync(); },
            error: error => { this.error = this.errorMessage(error); this.megaSyncingHistory = false; }
        });
    }

    private loadMegaSyncStatus() {
        this.http.get<any>(this.api + '/megasena/sync-history/status').subscribe({
            next: job => {
                if (job.status === 'IDLE') { this.megaSyncJob = job.automatic?.lastAttempt ? { automatic: job.automatic } : null; return; }
                this.megaSyncJob = job;
                if (job.status === 'RUNNING') { this.megaSyncingHistory = true; this.pollMegaSync(); }
            },
            error: () => { }
        });
    }

    private pollMegaSync() {
        if (this.megaSyncPoll) clearInterval(this.megaSyncPoll);
        const poll = () => this.http.get<any>(this.api + '/megasena/sync-history/status').subscribe({
            next: job => {
                this.megaSyncJob = job;
                if (job.status === 'RUNNING') return;
                this.megaSyncingHistory = false; this.stopMegaSyncPoll();
                if (job.status === 'FAILED') this.error = job.error || 'A carga histórica da Mega-Sena falhou.';
                else this.message = `Carga concluída: ${job.result?.imported ?? 0} concursos importados.`;
                this.loadMegaDraws(1);
            },
            error: error => { this.error = this.errorMessage(error); this.megaSyncingHistory = false; this.stopMegaSyncPoll(); }
        });
        poll(); this.megaSyncPoll = setInterval(poll, 1500);
    }

    private stopMegaSyncPoll() {
        if (this.megaSyncPoll) clearInterval(this.megaSyncPoll);
        this.megaSyncPoll = null;
    }

    loadMegaStatistics() {
        if (!this.hasPermission('dashboard.read')) return;
        this.loadingMegaStatistics = true;
        this.http.get<any>(this.api + '/megasena/statistics').subscribe({
            next: response => { this.megaStatistics = response; this.loadingMegaStatistics = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingMegaStatistics = false; }
        });
    }

    runMegaBacktest() {
        if (this.loadingMegaBacktest) return;
        this.loadingMegaBacktest = true; this.megaBacktest = null; this.error = '';
        const params = new URLSearchParams({ contests: String(this.megaBacktestContests), numbersPerGame: String(this.megaBacktestNumbers), quantity: String(this.megaBacktestQuantity) });
        this.http.get<any>(this.api + `/megasena/backtest?${params}`).subscribe({
            next: response => { this.megaBacktest = response; this.loadingMegaBacktest = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingMegaBacktest = false; }
        });
    }

    openLotoTab(tab: 'generate' | 'history' | 'results') {
        this.lotoTab = tab;
        if (tab === 'history') this.loadLotoPredictions(this.lotoPredictionPage);
        if (tab === 'results') { this.loadLotoDraws(this.lotoDrawPage); if (this.hasPermission('imports.write')) this.loadLotoSyncStatus(); }
    }

    lotoGameCost(numbersPerGame = this.lotoNumbersPerGame) { return this.megaCombinations(Number(numbersPerGame) || 15, 15) * this.lotoBetPrice; }

    lotoTotalCost() { return this.lotoGameCost() * (Number(this.lotoQuantity) || 0); }

    generateLotofacil() {
        if (this.lotoGenerating) return;
        this.lotoGenerating = true; this.error = ''; this.message = '';
        const body: any = { quantity: Number(this.lotoQuantity), numbersPerGame: Number(this.lotoNumbersPerGame), windowContests: Number(this.lotoWindowContests) };
        if (this.lotoTargetContest) body.targetContest = Number(this.lotoTargetContest);
        if (this.lotoSeed !== null && String(this.lotoSeed) !== '') body.seed = Number(this.lotoSeed);
        this.http.post<any>(this.api + '/lotofacil/generate', body).subscribe({
            next: response => { this.lotoGeneration = response; this.lotoGenerating = false; this.message = `${response.quantity} jogos gerados para o concurso ${response.targetContest}.`; },
            error: error => { this.error = this.errorMessage(error); this.lotoGenerating = false; }
        });
    }

    lotoPrizeSummary(item: any) {
        return [15, 14, 13, 12, 11].filter(tier => item['count' + tier]).map(tier => `${item['count' + tier]}× ${tier} pts`).join(' · ');
    }

    loadLotoPredictions(page = 1) {
        this.loadingLotoPredictions = true;
        this.http.get<any>(this.api + `/lotofacil/predictions?page=${Math.max(1, page)}&pageSize=${this.lotoPredictionPageSize}`).subscribe({
            next: response => {
                this.lotoPredictions = response.items; this.lotoPredictionPage = response.page;
                this.lotoPredictionTotalPages = response.totalPages; this.lotoPredictionTotal = response.total;
                this.selectedLotoIds = new Set([...this.selectedLotoIds].filter(id => response.items.some((x: any) => x.id === id)));
                this.loadingLotoPredictions = false;
            },
            error: error => { this.error = this.errorMessage(error); this.loadingLotoPredictions = false; }
        });
    }

    openLotoPrediction(id: string) {
        this.http.get<any>(this.api + `/lotofacil/predictions/${id}`).subscribe({
            next: response => this.selectedLotoPrediction = response,
            error: error => this.error = this.errorMessage(error)
        });
    }

    toggleLotoSelection(id: string, checked: boolean) {
        if (checked) this.selectedLotoIds.add(id); else this.selectedLotoIds.delete(id);
        this.selectedLotoIds = new Set(this.selectedLotoIds);
    }

    toggleAllLoto(checked: boolean) {
        this.selectedLotoIds = checked ? new Set(this.lotoPredictions.map(x => x.id)) : new Set<string>();
    }

    deleteSelectedLoto() {
        if (!this.selectedLotoIds.size || this.deletingLoto) return;
        if (!window.confirm(`Excluir ${this.selectedLotoIds.size} registro(s) de jogos da Lotofácil?`)) return;
        this.deletingLoto = true;
        this.http.post<any>(this.api + '/lotofacil/predictions/delete-batch', { ids: [...this.selectedLotoIds] }).subscribe({
            next: response => {
                this.message = `${response.deleted} registro(s) excluído(s).`; this.deletingLoto = false;
                if (this.selectedLotoPrediction && this.selectedLotoIds.has(this.selectedLotoPrediction.id)) this.selectedLotoPrediction = null;
                this.selectedLotoIds = new Set<string>(); this.loadLotoPredictions(this.lotoPredictionPage);
            },
            error: error => { this.error = this.errorMessage(error); this.deletingLoto = false; }
        });
    }

    loadLotoDraws(page = 1) {
        this.loadingLotoDraws = true;
        this.http.get<any>(this.api + `/lotofacil/draws?page=${Math.max(1, page)}&pageSize=${this.lotoDrawPageSize}`).subscribe({
            next: response => {
                this.lotoDraws = response.items; this.lotoDrawPage = response.page; this.lotoDrawTotalPages = response.totalPages;
                this.lotoDrawTotal = response.total; this.lotoLatestContest = response.latestContest; this.lotoMissingDraws = response.missing;
                this.loadingLotoDraws = false;
            },
            error: error => { this.error = this.errorMessage(error); this.loadingLotoDraws = false; }
        });
    }

    syncLotoLatest() {
        if (this.lotoSyncingLatest) return;
        this.lotoSyncingLatest = true; this.error = '';
        this.http.post<any>(this.api + '/lotofacil/sync-latest', {}).subscribe({
            next: draw => { this.lotoSyncingLatest = false; this.message = `Concurso ${draw.contest} sincronizado.`; this.loadLotoDraws(1); },
            error: error => { this.error = this.errorMessage(error); this.lotoSyncingLatest = false; }
        });
    }

    syncLotoHistory() {
        if (this.lotoSyncingHistory) return;
        this.lotoSyncingHistory = true; this.error = '';
        this.http.post<any>(this.api + '/lotofacil/sync-history', {}).subscribe({
            next: job => { this.lotoSyncJob = job; this.pollLotoSync(); },
            error: error => { this.error = this.errorMessage(error); this.lotoSyncingHistory = false; }
        });
    }

    private loadLotoSyncStatus() {
        this.http.get<any>(this.api + '/lotofacil/sync-history/status').subscribe({
            next: job => {
                if (job.status === 'IDLE') { this.lotoSyncJob = job.automatic?.lastAttempt ? { automatic: job.automatic } : null; return; }
                this.lotoSyncJob = job;
                if (job.status === 'RUNNING') { this.lotoSyncingHistory = true; this.pollLotoSync(); }
            },
            error: () => { }
        });
    }

    private pollLotoSync() {
        if (this.lotoSyncPoll) clearInterval(this.lotoSyncPoll);
        const poll = () => this.http.get<any>(this.api + '/lotofacil/sync-history/status').subscribe({
            next: job => {
                this.lotoSyncJob = job;
                if (job.status === 'RUNNING') return;
                this.lotoSyncingHistory = false; this.stopLotoSyncPoll();
                if (job.status === 'FAILED') this.error = job.error || 'A carga histórica da Lotofácil falhou.';
                else this.message = `Carga concluída: ${job.result?.imported ?? 0} concursos importados.`;
                this.loadLotoDraws(1);
            },
            error: error => { this.error = this.errorMessage(error); this.lotoSyncingHistory = false; this.stopLotoSyncPoll(); }
        });
        poll(); this.lotoSyncPoll = setInterval(poll, 1500);
    }

    private stopLotoSyncPoll() {
        if (this.lotoSyncPoll) clearInterval(this.lotoSyncPoll);
        this.lotoSyncPoll = null;
    }

    loadLotoStatistics() {
        if (!this.hasPermission('dashboard.read')) return;
        this.loadingLotoStatistics = true;
        this.http.get<any>(this.api + '/lotofacil/statistics').subscribe({
            next: response => { this.lotoStatistics = response; this.loadingLotoStatistics = false; },
            error: error => { this.error = this.errorMessage(error); this.loadingLotoStatistics = false; }
        });
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
            for (let position = 1; position <= 10; position++) {
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
