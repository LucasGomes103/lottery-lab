using Dapper;

namespace LotteryLab.Api.Data;

public sealed class AuthSchema(Db db)
{
    public async Task Initialize()
    {
        await using var connection = db.Open();
        await connection.ExecuteAsync(Sql);
    }

    private const string Sql = """
        create table if not exists app_users (
            id uuid primary key,
            username varchar(80) not null,
            display_name varchar(160) not null,
            password_hash varchar(300) not null,
            role varchar(30) not null default 'USER',
            permissions text[] not null default '{}',
            is_active boolean not null default true,
            must_change_password boolean not null default false,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            last_login_at timestamptz
        );
        create unique index if not exists ux_app_users_username on app_users(lower(username));

        create table if not exists auth_sessions (
            id uuid primary key,
            user_id uuid not null references app_users(id) on delete cascade,
            token_hash char(64) not null unique,
            expires_at timestamptz not null,
            created_at timestamptz not null default now(),
            last_used_at timestamptz not null default now()
        );
        create index if not exists ix_auth_sessions_expiry on auth_sessions(expires_at);

        insert into app_users(id,username,display_name,password_hash,role,permissions,is_active,must_change_password)
        select gen_random_uuid(),'admin','Administrador',
               'pbkdf2-sha256$210000$PIVJREf5Ui5e1OMU+TCV1g==$a6v+MAByESCm7XEjA1hPG24aAGMnJYIMYOd6F5pyYBk=',
               'ADMIN','{}',true,true
        where not exists(select 1 from app_users);

        delete from auth_sessions where expires_at <= now();
        """;
}
