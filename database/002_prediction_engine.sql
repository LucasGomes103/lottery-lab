-- Executado automaticamente pela API na inicializacao; mantido tambem para auditoria/manual.
-- A definicao canonica esta em backend/LotteryLab.Api/Data/PredictionSchema.cs.
create table if not exists algorithm_versions (
  id bigserial primary key, code varchar(80) not null, version integer not null,
  name varchar(160) not null, weights jsonb not null, config jsonb not null,
  is_production boolean not null default false, created_at timestamptz not null default now(),
  unique(code, version)
);
create table if not exists predictions (
  id uuid primary key, bank varchar(120) not null, target_date date not null,
  target_time time not null, algorithm_code varchar(80) not null, algorithm_version integer not null,
  window_days integer not null, quantity integer not null, random_seed bigint not null,
  sample_extractions integer not null, sample_results integer not null, robustness varchar(30) not null,
  config jsonb not null, status varchar(30) not null default 'PENDING', generated_at timestamptz not null default now()
);
create table if not exists prediction_candidates (
  id bigserial primary key, prediction_id uuid not null references predictions(id) on delete cascade,
  rank integer not null, milhar char(4) not null, centena char(3) not null, dezena char(2) not null,
  group_no integer not null, selection_type varchar(30) not null,
  statistical_score numeric(8,4) not null, final_score numeric(8,4) not null,
  features jsonb not null, reasons jsonb not null, unique(prediction_id, rank)
);
create table if not exists prediction_evaluations (
  prediction_id uuid primary key references predictions(id) on delete cascade,
  extraction_id bigint not null references extractions(id), evaluated_at timestamptz not null default now(),
  hit_milhar boolean not null, hit_centena boolean not null, hit_dezena boolean not null,
  best_milhar_position integer, best_centena_position integer, best_dezena_position integer,
  details jsonb not null
);
create index if not exists ix_predictions_target on predictions(bank,target_date desc,target_time);
create index if not exists ix_predictions_status on predictions(status,bank,target_date,target_time);
create index if not exists ix_prediction_candidates_suffixes on prediction_candidates(milhar,centena,dezena);
