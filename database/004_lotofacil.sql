-- Executado automaticamente pela API na inicializacao; mantido tambem para auditoria/manual.
-- A definicao canonica esta em backend/LotteryLab.Api/Data/LotofacilSchema.cs.
create table if not exists lotofacil_draws (
  contest integer primary key check(contest > 0),
  draw_date date not null,
  numbers smallint[] not null check(cardinality(numbers) = 15), -- quinze dezenas em ordem crescente
  winners15 integer not null default 0, prize15 numeric(16,2) not null default 0,
  winners14 integer not null default 0, prize14 numeric(16,2) not null default 0,
  winners13 integer not null default 0, prize13 numeric(16,2) not null default 0,
  winners12 integer not null default 0, prize12 numeric(16,2) not null default 0,
  winners11 integer not null default 0, prize11 numeric(16,2) not null default 0,
  accumulated boolean not null default false,
  next_estimate numeric(16,2),
  source varchar(200) not null,
  imported_at timestamptz not null default now()
);
create index if not exists ix_lotofacil_draws_date on lotofacil_draws(draw_date desc);

create table if not exists lotofacil_predictions (
  id uuid primary key,
  target_contest integer not null check(target_contest > 0),
  generated_at timestamptz not null default now(),
  seed bigint not null,
  window_contests integer not null,
  numbers_per_game integer not null check(numbers_per_game between 15 and 20),
  quantity integer not null check(quantity between 1 and 50),
  total_cost numeric(14,2) not null check(total_cost > 0),
  config jsonb not null,
  scores jsonb not null,
  status varchar(30) not null default 'PENDING'
);
create index if not exists ix_lotofacil_predictions_target on lotofacil_predictions(target_contest desc, generated_at desc);

create table if not exists lotofacil_prediction_games (
  prediction_id uuid not null references lotofacil_predictions(id) on delete cascade,
  rank integer not null,
  numbers smallint[] not null,
  primary key(prediction_id, rank)
);
