import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

export const DEFAULT_PAGE_SIZE = 10;
export const PAGE_SIZE_OPTIONS = [10, 20, 50, 100];

/** Paginação feita no navegador para listas que já vêm completas da API. */
export class ClientPager {
    page = 1;
    constructor(public pageSize = DEFAULT_PAGE_SIZE) {}

    totalPages(rows: readonly unknown[] | null | undefined) { return Math.max(1, Math.ceil((rows?.length || 0) / this.pageSize)); }

    slice<T = any>(rows: T[] | null | undefined): T[] {
        if (!rows?.length) return [];
        this.page = Math.min(Math.max(1, this.page), this.totalPages(rows));
        const start = (this.page - 1) * this.pageSize;
        return rows.slice(start, start + this.pageSize);
    }

    setPageSize(size: number) { this.pageSize = size; this.page = 1; }
}

type PageSlot = number | 'gap';

@Component({
    selector: 'app-pager',
    standalone: true,
    imports: [CommonModule, FormsModule],
    template: `
<nav class="pager" aria-label="Paginação">
  <div class="pager-meta">
    <label class="pager-size"><span>Linhas</span>
      <select [ngModel]="pageSize" (ngModelChange)="changeSize($event)" [disabled]="disabled" aria-label="Linhas por página">
        <option *ngFor="let size of pageSizes" [ngValue]="size">{{size}}</option>
      </select>
    </label>
    <span class="pager-range" *ngIf="total !== null">{{first}}–{{last}} de {{total}}</span>
  </div>
  <div class="pager-controls" *ngIf="pages > 1">
    <button type="button" class="pager-btn" (click)="go(1)" [disabled]="disabled || page <= 1" aria-label="Primeira página" title="Primeira página"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M17 18l-6-6 6-6M11 18l-6-6 6-6"/></svg></button>
    <button type="button" class="pager-btn" (click)="go(page - 1)" [disabled]="disabled || page <= 1" aria-label="Página anterior" title="Página anterior"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 18l-6-6 6-6"/></svg></button>
    <ng-container *ngFor="let slot of slots()">
      <span *ngIf="slot === 'gap'" class="pager-gap" aria-hidden="true">…</span>
      <button *ngIf="slot !== 'gap'" type="button" class="pager-btn pager-num" [class.active]="slot === page" [attr.aria-current]="slot === page ? 'page' : null" (click)="go(+slot)" [disabled]="disabled">{{slot}}</button>
    </ng-container>
    <span class="pager-compact">{{page}} / {{pages}}</span>
    <button type="button" class="pager-btn" (click)="go(page + 1)" [disabled]="disabled || page >= pages" aria-label="Próxima página" title="Próxima página"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 18l6-6-6-6"/></svg></button>
    <button type="button" class="pager-btn" (click)="go(pages)" [disabled]="disabled || page >= pages" aria-label="Última página" title="Última página"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 18l6-6-6-6M13 18l6-6-6-6"/></svg></button>
  </div>
</nav>`
})
export class PagerComponent {
    @Input() page = 1;
    @Input() set totalPages(value: number | null | undefined) { this.pages = Math.max(1, Number(value) || 1); }
    @Input() total: number | null = null;
    @Input() pageSize = DEFAULT_PAGE_SIZE;
    @Input() pageSizes = PAGE_SIZE_OPTIONS;
    @Input() disabled = false;
    @Output() pageChange = new EventEmitter<number>();
    @Output() pageSizeChange = new EventEmitter<number>();
    pages = 1;

    get first() { return this.total ? (this.page - 1) * this.pageSize + 1 : 0; }
    get last() { return Math.min(this.page * this.pageSize, this.total || 0); }

    go(page: number) {
        const target = Math.min(Math.max(1, page), this.pages);
        if (target !== this.page) this.pageChange.emit(target);
    }

    changeSize(size: number) { this.pageSizeChange.emit(Number(size)); }

    /** Números visíveis: primeira, última e vizinhas da atual, com reticências nos saltos. */
    slots(): PageSlot[] {
        const wanted = new Set([1, this.pages, this.page - 1, this.page, this.page + 1]);
        if (this.page <= 3) [2, 3, 4].forEach(n => wanted.add(n));
        if (this.page >= this.pages - 2) [this.pages - 3, this.pages - 2, this.pages - 1].forEach(n => wanted.add(n));
        const numbers = [...wanted].filter(n => n >= 1 && n <= this.pages).sort((a, b) => a - b);
        const slots: PageSlot[] = [];
        numbers.forEach((n, i) => { if (i && n - numbers[i - 1] > 1) slots.push('gap'); slots.push(n); });
        return slots;
    }
}
