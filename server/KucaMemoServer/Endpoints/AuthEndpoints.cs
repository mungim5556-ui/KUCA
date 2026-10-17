using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KucaMemoServer.Models;
using KucaMemoServer.Services;

namespace KucaMemoServer.Endpoints;

/// <summary>
/// 로그인 · 계정 API (docs/api.md 의 "로그인 · 계정").
/// 카카오 / Apple 로 로그인하면 KUCA 로그인 토큰을 준다. 이후 요청은 헤더 Authorization: Bearer {토큰}.
/// 앱을 쓰려면 경희대 메일(@khu.ac.kr) 인증 · 닉네임 · 성별이 필요하다 (Account.Ready).
/// 체험 계정(/api/auth/guest)은 메일 인증 없이 성별만 넣으면 되고, 7일 뒤 지워진다.
/// </summary>
public static partial class AuthEndpoints
{
    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]{1,64}@khu\.ac\.kr$")]
    private static partial Regex KhuEmail();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/kakao", async (KakaoLoginRequest? body, IKakaoVerifier kakao, AccountStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.AccessToken))
                return Results.BadRequest(new { error = "accessToken 이 필요합니다." });
            if (!kakao.IsConfigured)
                return Results.Json(new { error = "카카오 로그인이 아직 설정되지 않았어요." }, statusCode: 503);
            string? kakaoId = await kakao.VerifyAsync(body.AccessToken, ct);
            return kakaoId == null
                ? Results.Json(new { error = "카카오 로그인을 확인하지 못했어요. 다시 시도해 주세요." }, statusCode: 401)
                : Results.Ok(Login(store, "kakao", kakaoId));
        });

        auth.MapPost("/apple", async (AppleLoginRequest? body, IAppleVerifier apple, AccountStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.IdentityToken))
                return Results.BadRequest(new { error = "identityToken 이 필요합니다." });
            string? sub = await apple.VerifyAsync(body.IdentityToken, ct);
            return sub == null
                ? Results.Json(new { error = "Apple 로그인을 확인하지 못했어요. 다시 시도해 주세요." }, statusCode: 401)
                : Results.Ok(Login(store, "apple", sub));
        });

        // 개발용 (에디터·로컬 테스트). 설정 Auth:DevLogin=true 일 때만. Azure 에서는 켜지 않는다.
        auth.MapPost("/dev", (DevLoginRequest? body, IConfiguration config, AccountStore store) =>
        {
            if (config["Auth:DevLogin"] != "true")
                return Results.NotFound(new { error = "없는 API 입니다." });
            string name = (body?.Name ?? "").Trim();
            return name.Length is < 1 or > 40
                ? Results.BadRequest(new { error = "name 은 1~40자입니다." })
                : Results.Ok(Login(store, "dev", name));
        });

        // 체험 모드: 로그인 없이 둘러보기. 메일 인증이 면제된 체험 계정과 토큰(7일)을 준다.
        auth.MapPost("/guest", (AccountStore store) =>
        {
            Account guest = store.CreateGuest();
            return Results.Ok(new LoginResult(store.CreateSession(guest.Id, AccountStore.GuestLifetime), guest));
        });

        auth.MapPost("/logout", (HttpRequest req, AccountStore store) =>
        {
            if (TokenOf(req) is { } token)
                store.DeleteSession(token);
            return Results.NoContent();
        });

        var me = app.MapGroup("/api/me").WithTags("Auth");

        me.MapGet("/", (HttpRequest req, AccountStore store) =>
            Current(req, store) is { } user ? Results.Ok(user) : Unauthorized());

        me.MapPut("/nickname", (NicknameRequest? body, HttpRequest req, AccountStore store) =>
        {
            if (Current(req, store) is not { } user)
                return Unauthorized();
            string nick = (body?.Nickname ?? "").Trim();
            if (nick.Length is < 1 or > 20)
                return Results.BadRequest(new { error = "닉네임은 1~20자로 써 주세요." });
            store.SetNickname(user.Id, nick);
            return Results.Ok(store.Find(user.Id));
        });

        // 성별: 매칭 "동성만" 조건에만 쓰고 다른 사람에게 보여 주지 않는다.
        me.MapPut("/gender", (GenderRequest? body, HttpRequest req, AccountStore store) =>
        {
            if (Current(req, store) is not { } user)
                return Unauthorized();
            string gender = (body?.Gender ?? "").Trim().ToLowerInvariant();
            if (gender is not ("male" or "female"))
                return Results.BadRequest(new { error = "gender 는 male 또는 female 이에요." });
            store.SetGender(user.Id, gender);
            return Results.Ok(store.Find(user.Id));
        });

        // 회원 탈퇴: 계정 · 로그인 토큰 · 인증번호를 지운다.
        me.MapDelete("/", (HttpRequest req, AccountStore store) =>
        {
            if (Current(req, store) is not { } user)
                return Unauthorized();
            store.DeleteAccount(user.Id);
            return Results.NoContent();
        });

        // 산책 취향 체크리스트. 앱의 WalkPreferences JSON 을 그대로 받아 저장한다 (AI 산책메이트 매칭용).
        me.MapPut("/preferences", async (HttpRequest req, AccountStore store) =>
        {
            if (Current(req, store) is not { } user)
                return Unauthorized();
            using var reader = new StreamReader(req.Body);
            string json = await reader.ReadToEndAsync();
            if (json.Length is 0 or > 4096)
                return Results.BadRequest(new { error = "취향 정보가 비었거나 너무 큽니다." });
            try { using var _ = JsonDocument.Parse(json); }
            catch (JsonException) { return Results.BadRequest(new { error = "취향 정보가 JSON 이 아닙니다." }); }
            store.SetPreferences(user.Id, json);
            return Results.Ok(store.Find(user.Id));
        });

        // 경희대 메일로 6자리 인증번호 보내기
        me.MapPost("/email/start", async (EmailStartRequest? body, HttpRequest req, AccountStore store, IEmailSender mail, IConfiguration config, CancellationToken ct) =>
        {
            if (Current(req, store) is not { } user)
                return Unauthorized();
            string email = (body?.Email ?? "").Trim().ToLowerInvariant();
            if (!KhuEmail().IsMatch(email))
                return Results.BadRequest(new { error = "경희대 메일(@khu.ac.kr)만 쓸 수 있어요." });
            if (store.EmailTakenByOther(user.Id, email))
                return Results.Conflict(new { error = "이미 다른 계정이 인증한 메일이에요." });
            TimeSpan wait = store.ResendWait(user.Id);
            if (wait > TimeSpan.Zero)
                return Results.Json(new { error = $"{Math.Ceiling(wait.TotalSeconds)}초 뒤에 다시 보낼 수 있어요." }, statusCode: 429);

            string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            bool devEcho = config["Email:DevEcho"] == "true"; // 로컬 테스트: 메일 대신 응답에 번호를 넣는다
            if (!mail.IsConfigured && !devEcho)
                return Results.Json(new { error = "인증 메일을 보낼 수 없어요 (서버 메일 설정 없음)." }, statusCode: 503);
            store.SaveCode(user.Id, email, code);
            if (mail.IsConfigured)
            {
                bool sent = await mail.SendAsync(email, "[KUCA] 경희대 메일 인증번호",
                    $"KUCA 인증번호는 {code} 입니다.\n10분 안에 앱에 입력해 주세요.\n\n직접 요청하지 않았다면 이 메일을 무시하세요.", ct);
                if (!sent)
                    return Results.Json(new { error = "인증 메일을 보내지 못했어요. 잠시 뒤 다시 시도해 주세요." }, statusCode: 502);
            }
            return devEcho && !mail.IsConfigured ? Results.Ok(new { sent = true, devCode = code }) : Results.Ok(new { sent = true });
        });

        me.MapPost("/email/verify", (EmailVerifyRequest? body, HttpRequest req, AccountStore store) =>
        {
            if (Current(req, store) is not { } user)
                return Unauthorized();
            return store.VerifyCode(user.Id, body?.Code ?? "") switch
            {
                AccountStore.VerifyResult.Ok => Results.Ok(store.Find(user.Id)),
                AccountStore.VerifyResult.NoCode => Results.BadRequest(new { error = "먼저 인증번호를 받아 주세요." }),
                AccountStore.VerifyResult.Expired => Results.BadRequest(new { error = "인증번호가 만료됐어요. 다시 받아 주세요." }),
                AccountStore.VerifyResult.TooManyTries => Results.Json(new { error = "너무 많이 틀렸어요. 인증번호를 다시 받아 주세요." }, statusCode: 429),
                AccountStore.VerifyResult.EmailTaken => Results.Conflict(new { error = "이미 다른 계정이 인증한 메일이에요." }),
                _ => Results.BadRequest(new { error = "인증번호가 맞지 않아요." }),
            };
        });
    }

    static LoginResult Login(AccountStore store, string provider, string providerUserId)
    {
        Account user = store.FindOrCreate(provider, providerUserId);
        return new LoginResult(store.CreateSession(user.Id), user);
    }

    static string? TokenOf(HttpRequest req)
    {
        string h = req.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && h.Length > 7 ? h[7..].Trim() : null;
    }

    /// <summary>요청의 로그인 토큰으로 계정을 찾는다 (없거나 만료면 null)</summary>
    public static Account? Current(HttpRequest req, AccountStore store) => TokenOf(req) is { } t ? store.FindBySession(t) : null;

    static IResult Unauthorized() => Results.Json(new { error = "로그인이 필요해요." }, statusCode: 401);
}
