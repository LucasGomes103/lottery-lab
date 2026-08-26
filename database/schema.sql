create table if not exists extractions(
 id bigserial primary key, bank varchar(80) not null, extraction_date date not null, extraction_time time not null,
 source_file varchar(255), imported_at timestamptz not null default now(), unique(bank,extraction_date,extraction_time));
create table if not exists results(
 id bigserial primary key, extraction_id bigint not null references extractions(id) on delete cascade,
 position int not null, number varchar(4) not null, centena varchar(3), dezena varchar(2), group_no int, animal varchar(40), unique(extraction_id,position));
create index if not exists ix_results_dezena on results(dezena);
create index if not exists ix_extractions_bank_time_date on extractions(bank,extraction_time,extraction_date);
