using System.Security.Cryptography;
using System.Text;
using KucaMemoServer.Models;
using Microsoft.Data.Sqlite;

namespace KucaMemoServer.Services;

/// <summary>
/// 계정 · 로그인 토큰 · 메일 인증번호를 메모와 같은 SQLite 파일에 저장한다.
/// 토큰과 인증번호는 원문이 아니라 SHA-256 해시만 저장한다.
/// </summary>
public class AccountStore
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    public const int MaxCodeAttempts = 5;

    /// <summary>체험 계정: 로그인 없이 둘러보기. 만든 지 이만큼 지나면 계정과 로그인 토큰을 지운다.</summary>
    public const string GuestProvider = "guest";
    public static readonly TimeSpan GuestLifetime = TimeSpan.FromDays(7);

    readonly string connectionString;
    readonly TimeProvider clock;

    public AccountStore(IConfiguration config, IWebHostEnvironment env, TimeProvider? clock = null)
    {
        string path = config["Memos:DatabasePath"] ?? Path.Combine(env.ContentRootPath, "memos.db");
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        this.clock = clock ?? TimeProvider.System;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id               TEXT PRIMARY KEY,
                provider         TEXT NOT NULL,
                provider_user_id TEXT NOT NULL,
                nickname         TEXT,
                khu_email        TEXT,
                email_verified   INTEGER NOT NULL DEFAULT 0,
                preferences      TEXT,
                created_at       INTEGER NOT NULL,
                UNIQUE (provider, provider_user_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS users_email ON users (khu_email) WHERE email_verified = 1;

            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY,
                user_id    TEXT NOT NULL,
                expires_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS email_codes (
                user_id    TEXT PRIMARY KEY,
                email      TEXT NOT NULL,
                code_hash  TEXT NOT NULL,
                expires_at INTEGER NOT NULL,
                sent_at    INTEGER NOT NULL,
                attempts   INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
        AddColumnIfMissing(conn, "users", "gender", "TEXT");
        PurgeExpiredGuests();
    }

    /// <summary>예전 데이터베이스에 새 칸을 더한다 (이미 있으면 그대로)</summary>
    static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        check.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt32(check.ExecuteScalar()) > 0)
            return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    SqliteConnection Open()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    long Now => clock.GetUtcNow().UtcTicks;

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // ---------- 계정 ----------

    /// <summary>제공자 계정으로 찾고, 없으면 새로 만든다.</summary>
    public Account FindOrCreate(string provider, string providerUserId)
    {
        using var conn = Open();
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT id FROM users WHERE provider = $p AND provider_user_id = $u";
            find.Parameters.AddWithValue("$p", provider);
            find.Parameters.AddWithValue("$u", providerUserId);
            if (find.ExecuteScalar() is string id)
                return Find(id)!;
        }
        string newId = Guid.NewGuid().ToString();
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO users (id, provider, provider_user_id, created_at) VALUES ($id, $p, $u, $t)";
            insert.Parameters.AddWithValue("$id", newId);
            insert.Parameters.AddWithValue("$p", provider);
            insert.Parameters.AddWithValue("$u", providerUserId);
            insert.Parameters.AddWithValue("$t", Now);
            insert.ExecuteNonQuery();
        }
        return Find(newId)!;
    }

    public Account? Find(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, provider, nickname, khu_email, email_verified, preferences, created_at, gender FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new Account(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) == 1, r.IsDBNull(5) ? null : r.GetString(5),
            new DateTime(r.GetInt64(6), DateTimeKind.Utc), r.IsDBNull(7) ? null : r.GetString(7));
    }

    /// <summary>체험 계정을 새로 만든다 (닉네임 "체험생-1234")</summary>
    public Account CreateGuest()
    {
        PurgeExpiredGuests();
        Account guest = FindOrCreate(GuestProvider, Guid.NewGuid().ToString());
        SetNickname(guest.Id, $"체험생-{RandomNumberGenerator.GetInt32(1000, 10000)}");
        return Find(guest.Id)!;
    }

    /// <summary>계정과 그 계정의 로그인 토큰 · 인증번호를 지운다 (회원 탈퇴)</summary>
    public void DeleteAccount(string userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE user_id = $u; DELETE FROM email_codes WHERE user_id = $u; DELETE FROM users WHERE id = $u";
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>만든 지 GuestLifetime 이 지난 체험 계정을 지운다</summary>
    public void PurgeExpiredGuests()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM sessions WHERE user_id IN (SELECT id FROM users WHERE provider = $g AND created_at < $cut);
            DELETE FROM email_codes WHERE user_id IN (SELECT id FROM users WHERE provider = $g AND created_at < $cut);
            DELETE FROM users WHERE provider = $g AND created_at < $cut;
            """;
        cmd.Parameters.AddWithValue("$g", GuestProvider);
        cmd.Parameters.AddWithValue("$cut", Now - GuestLifetime.Ticks);
        cmd.ExecuteNonQuery();
    }

    public void SetNickname(string userId, string nickname) => Update(userId, "nickname", nickname);
    public void SetGender(string userId, string gender) => Update(userId, "gender", gender);
    public void SetPreferences(string userId, string json) => Update(userId, "preferences", json);

    void Update(string userId, string column, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE users SET {column} = $v WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$id", userId);
        cmd.ExecuteNonQuery();
    }

    // ---------- 로그인 토큰 ----------

    public string CreateSession(string userId, TimeSpan? lifetime = null)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (token_hash, user_id, expires_at) VALUES ($h, $u, $e)";
        cmd.Parameters.AddWithValue("$h", Hash(token));
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$e", Now + (lifetime ?? SessionLifetime).Ticks);
        cmd.ExecuteNonQuery();
        return token;
    }

    /// <summary>토큰의 계정 (만료됐거나 없으면 null)</summary>
    public Account? FindBySession(string token)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM sessions WHERE token_hash = $h AND expires_at > $now";
        cmd.Parameters.AddWithValue("$h", Hash(token));
        cmd.Parameters.AddWithValue("$now", Now);
        return cmd.ExecuteScalar() is string id ? Find(id) : null;
    }

    public void DeleteSession(string token)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE token_hash = $h";
        cmd.Parameters.AddWithValue("$h", Hash(token));
        cmd.ExecuteNonQuery();
    }

    // ---------- 경희대 메일 인증 ----------

    public bool EmailTakenByOther(string userId, string email)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE khu_email = $e AND email_verified = 1 AND id <> $id";
        cmd.Parameters.AddWithValue("$e", email);
        cmd.Parameters.AddWithValue("$id", userId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>다시 보내기까지 남은 시간 (바로 보낼 수 있으면 Zero)</summary>
    public TimeSpan ResendWait(string userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sent_at FROM email_codes WHERE user_id = $u";
        cmd.Parameters.AddWithValue("$u", userId);
        if (cmd.ExecuteScalar() is not long sent)
            return TimeSpan.Zero;
        TimeSpan left = TimeSpan.FromTicks(sent + ResendCooldown.Ticks - Now);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    public void SaveCode(string userId, string email, string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO email_codes (user_id, email, code_hash, expires_at, sent_at, attempts) VALUES ($u, $e, $h, $x, $now, 0)
            ON CONFLICT(user_id) DO UPDATE SET email = $e, code_hash = $h, expires_at = $x, sent_at = $now, attempts = 0
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$e", email);
        cmd.Parameters.AddWithValue("$h", Hash(code));
        cmd.Parameters.AddWithValue("$x", Now + CodeLifetime.Ticks);
        cmd.Parameters.AddWithValue("$now", Now);
        cmd.ExecuteNonQuery();
    }

    public enum VerifyResult { Ok, NoCode, Expired, TooManyTries, Wrong, EmailTaken }

    /// <summary>인증번호를 확인하고, 맞으면 계정에 그 메일을 인증된 메일로 기록한다.</summary>
    public VerifyResult VerifyCode(string userId, string code)
    {
        using var conn = Open();
        string email, hash;
        long expires;
        int attempts;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT email, code_hash, expires_at, attempts FROM email_codes WHERE user_id = $u";
            cmd.Parameters.AddWithValue("$u", userId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return VerifyResult.NoCode;
            (email, hash, expires, attempts) = (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3));
        }
        if (expires <= Now)
            return VerifyResult.Expired;
        if (attempts >= MaxCodeAttempts)
            return VerifyResult.TooManyTries;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(code.Trim()))))
        {
            using var bump = conn.CreateCommand();
            bump.CommandText = "UPDATE email_codes SET attempts = attempts + 1 WHERE user_id = $u";
            bump.Parameters.AddWithValue("$u", userId);
            bump.ExecuteNonQuery();
            return VerifyResult.Wrong;
        }
        if (EmailTakenByOther(userId, email))
            return VerifyResult.EmailTaken;
        using (var ok = conn.CreateCommand())
        {
            ok.CommandText = "UPDATE users SET khu_email = $e, email_verified = 1 WHERE id = $u; DELETE FROM email_codes WHERE user_id = $u";
            ok.Parameters.AddWithValue("$e", email);
            ok.Parameters.AddWithValue("$u", userId);
            ok.ExecuteNonQuery();
        }
        return VerifyResult.Ok;
    }
}
