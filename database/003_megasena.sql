-- Executado automaticamente pela API na inicializacao; mantido tambem para auditoria/manual.
-- A definicao canonica esta em backend/LotteryLab.Api/Data/MegaSenaSchema.cs.
create table if not exists megasena_draws (
  contest integer primary key check(contest > 0),
  draw_date date not null,
  numbers smallint[] not null check(cardinality(numbers) = 6), -- seis dezenas em ordem crescente
  sena_winners integer not null default 0, sena_prize numeric(16,2) not null default 0,
  quina_winners integer not null default 0, quina_prize numeric(16,2) not null default 0,
  quadra_winners integer not null default 0, quadra_prize numeric(16,2) not null default 0,
  accumulated boolean not null default false,
  next_estimate numeric(16,2),
  source varchar(200) not null,
  imported_at timestamptz not null default now()
);
create index if not exists ix_megasena_draws_date on megasena_draws(draw_date desc);

create table if not exists megasena_predictions (
  id uuid primary key, target_contest integer not null check(target_contest > 0),
  generated_at timestamptz not null default now(), seed bigint not null,
  window_contests integer not null,
  numbers_per_game integer not null check(numbers_per_game between 6 and 15),
  quantity integer not null constraint megasena_predictions_quantity_max500 check(quantity between 1 and 500),
  total_cost numeric(14,2) not null check(total_cost > 0),
  config jsonb not null, scores jsonb not null,
  status varchar(30) not null default 'PENDING'
);
create index if not exists ix_megasena_predictions_target on megasena_predictions(target_contest desc, generated_at desc);

create table if not exists megasena_prediction_games (
  prediction_id uuid not null references megasena_predictions(id) on delete cascade,
  rank integer not null, numbers smallint[] not null,
  primary key(prediction_id, rank)
);
