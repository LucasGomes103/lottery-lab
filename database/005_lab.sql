-- Laboratório de estratégias: cada execução do backtest e o resultado em JSON.
create table if not exists lab_runs (
    id uuid primary key,
    game varchar(20) not null,
    status varchar(20) not null,
    parameters jsonb not null,
    result jsonb,
    error text,
    created_by varchar(100),
    created_at timestamptz not null default now(),
    finished_at timestamptz
);
create index if not exists ix_lab_runs_created on lab_runs(created_at desc);
