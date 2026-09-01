using Dapper;

namespace LotteryLab.Api.Data;

public sealed class PredictionSchema(Db db)
{
    public async Task Initialize()
    {
        await using var connection = db.Open();
        await connection.ExecuteAsync(Sql);
    }

    public const string Sql = """
        create table if not exists algorithm_versions (
            id bigserial primary key,
            code varchar(80) not null,
            version integer not null,
            name varchar(160) not null,
            weights jsonb not null,
            config jsonb not null,
            is_production boolean not null default false,
            created_at timestamptz not null default now(),
            unique(code, version)
        );

        create table if not exists predictions (
            id uuid primary key,
            bank varchar(120) not null,
            target_date date not null,
            target_time time not null,
            algorithm_code varchar(80) not null,
            algorithm_version integer not null,
            window_days integer not null,
            quantity integer not null,
            random_seed bigint not null,
            sample_extractions integer not null,
            sample_results integer not null,
            robustness varchar(30) not null,
            config jsonb not null,
            status varchar(30) not null default 'PENDING',
            generated_at timestamptz not null default now()
        );

        create table if not exists prediction_candidates (
            id bigserial primary key,
            prediction_id uuid not null references predictions(id) on delete cascade,
            rank integer not null,
            milhar char(4) not null,
            centena char(3) not null,
            dezena char(2) not null,
            group_no integer not null,
            selection_type varchar(30) not null,
            statistical_score numeric(8,4) not null,
            final_score numeric(8,4) not null,
            features jsonb not null,
            reasons jsonb not null,
            unique(prediction_id, rank)
        );

        create table if not exists prediction_evaluations (
            prediction_id uuid primary key references predictions(id) on delete cascade,
            extraction_id bigint not null references extractions(id),
            evaluated_at timestamptz not null default now(),
            hit_milhar boolean not null,
            hit_centena boolean not null,
            hit_dezena boolean not null,
            milhar_hit_count integer not null default 0,
            centena_hit_count integer not null default 0,
            dezena_hit_count integer not null default 0,
            best_milhar_position integer,
            best_centena_position integer,
            best_dezena_position integer,
            details jsonb not null
        );

        create index if not exists ix_predictions_target on predictions(bank, target_date desc, target_time);
        create index if not exists ix_predictions_status on predictions(status, bank, target_date, target_time);
        create index if not exists ix_prediction_candidates_suffixes on prediction_candidates(milhar, centena, dezena);

        alter table prediction_evaluations add column if not exists milhar_hit_count integer not null default 0;
        alter table prediction_evaluations add column if not exists centena_hit_count integer not null default 0;
        alter table prediction_evaluations add column if not exists dezena_hit_count integer not null default 0;

        create table if not exists data_migrations (
            code varchar(120) primary key,
            applied_at timestamptz not null default now()
        );

        do $migration$
        begin
            if not exists(select 1 from data_migrations where code = 'remove-pt-rio-20260901') then
                delete from predictions where upper(trim(bank)) = 'PT RIO';
                delete from extractions where upper(trim(bank)) = 'PT RIO';
                insert into data_migrations(code) values('remove-pt-rio-20260901');
            end if;
        end
        $migration$;

        insert into algorithm_versions(code, version, name, weights, config, is_production)
        values(
            'HYBRID_EXPLORATION', 2, 'Motor híbrido com exploração controlada',
            '{"frequency":0.22,"timeFrequency":0.12,"delay":0.08,"continuity":0.12,"transition":0.12,"momentum":0.10,"reversal":0.05,"digits":0.09,"novelty":0.10}'::jsonb,
            '{"exploitation":0.60,"emerging":0.20,"exploration":0.20,"maxPerDezena":1,"maxPerCentena":1,"maxPerGroup":2}'::jsonb,
            true
        ) on conflict(code, version) do nothing;
        """;
}
