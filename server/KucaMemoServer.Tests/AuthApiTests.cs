using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KucaMemoServer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KucaMemoServer.Tests;

/// <summary>로그인 · 계정 API 검증. 카카오·Apple·메일은 가짜로 바꿔서 실제로 부르지 않는다.</summary>
public class AuthApiTests : IDisposable
{
    sealed class FakeKakao : IKakaoVerifier
    {
        public bool IsConfigured => true;
        public Task<string?> VerifyAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult<string?>(accessToken.StartsWith("good-") ? accessToken[5..] : null);
    }

    sealed class FakeApple : IAppleVerifier
    {
        public Task<string?> VerifyAsync(string identityToken, CancellationToken ct) =>
            Task.FromResult<string?>(identityToken == "apple-ok" ? "apple-user-1" : null);
    }

    sealed class FakeMail : IEmailSender
    {
        public bool IsConfigured => true;
        public readonly List<(string to, string body)> Sent = [];
        public Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            Sent.Add((to, body));
            return Task.FromResult(true);
        }
        public string LastCode => System.Text.RegularExpressions.Regex.Match(Sent[^1].body, @"\d{6}").Value;
    }

    readonly TestServer server = new();
    readonly FakeMail mail = new();
    readonly HttpClient client;

    public AuthApiTests()
    {
        var factory = server.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IKakaoVerifier>(new FakeKakao());
            s.AddSingleton<IAppleVerifier>(new FakeApple());
            s.AddSingleton<IEmailSender>(mail);
        }));
        client = factory.CreateClient();
    }

    public void Dispose() => server.Dispose();

    async Task<(string token, JsonElement user)> KakaoLogin(string id = "1234")
    {
        var res = await client.PostAsJsonAsync("/api/auth/kakao", new { accessToken = "good-" + id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        JsonElement r = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (r.GetProperty("token").GetString()!, r.GetProperty("user"));
    }

    HttpRequestMessage Req(HttpMethod method, string url, string? token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return req;
    }

    async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task 카카오_로그인하면_계정과_토큰이_생기고_같은_사람은_같은_계정()
    {
        var (token, user) = await KakaoLogin("777");
        Assert.Equal("kakao", user.GetProperty("provider").GetString());
        Assert.False(user.GetProperty("ready").GetBoolean());
        var (_, again) = await KakaoLogin("777");
        Assert.Equal(user.GetProperty("id").GetString(), again.GetProperty("id").GetString());

        JsonElement me = await Ok(await client.SendAsync(Req(HttpMethod.Get, "/api/me", token)));
        Assert.Equal(user.GetProperty("id").GetString(), me.GetProperty("id").GetString());
    }

    [Fact]
    public async Task 잘못된_카카오_토큰과_토큰_없는_요청은_401()
    {
        var bad = await client.PostAsJsonAsync("/api/auth/kakao", new { accessToken = "fake" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/api/me", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/api/me", "nope"))).StatusCode);
    }

    [Fact]
    public async Task 카카오_앱_ID_설정이_없으면_503()
    {
        using var plain = new TestServer(); // 진짜 KakaoVerifier, Kakao:AppId 없음
        var res = await plain.CreateClient().PostAsJsonAsync("/api/auth/kakao", new { accessToken = "x" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Apple_로그인()
    {
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/auth/apple", new { identityToken = "apple-ok" }));
        Assert.Equal("apple", r.GetProperty("user").GetProperty("provider").GetString());
        var bad = await client.PostAsJsonAsync("/api/auth/apple", new { identityToken = "forged" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task 경희대_메일_인증_흐름()
    {
        var (token, _) = await KakaoLogin();
        var gmail = await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/start", token, new { email = "me@gmail.com" }));
        Assert.Equal(HttpStatusCode.BadRequest, gmail.StatusCode);

        await Ok(await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/start", token, new { email = "Wonjun@KHU.ac.kr" })));
        Assert.Equal("wonjun@khu.ac.kr", mail.Sent[^1].to);

        var again = await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/start", token, new { email = "wonjun@khu.ac.kr" }));
        Assert.Equal((HttpStatusCode)429, again.StatusCode); // 60초 안에 다시 보내기

        string wrong = mail.LastCode == "000000" ? "111111" : "000000";
        var no = await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/verify", token, new { code = wrong }));
        Assert.Equal(HttpStatusCode.BadRequest, no.StatusCode);

        JsonElement user = await Ok(await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/verify", token, new { code = mail.LastCode })));
        Assert.True(user.GetProperty("emailVerified").GetBoolean());
        Assert.Equal("wonjun@khu.ac.kr", user.GetProperty("khuEmail").GetString());
        Assert.False(user.GetProperty("ready").GetBoolean()); // 닉네임이 아직 없다

        user = await Ok(await client.SendAsync(Req(HttpMethod.Put, "/api/me/nickname", token, new { nickname = "원준" })));
        Assert.False(user.GetProperty("ready").GetBoolean()); // 성별이 아직 없다

        user = await Ok(await client.SendAsync(Req(HttpMethod.Put, "/api/me/gender", token, new { gender = "male" })));
        Assert.Equal("male", user.GetProperty("gender").GetString());
        Assert.True(user.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task 성별은_male_female_만()
    {
        var (token, _) = await KakaoLogin("g1");
        var bad = await client.SendAsync(Req(HttpMethod.Put, "/api/me/gender", token, new { gender = "other" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Req(HttpMethod.Put, "/api/me/gender", null, new { gender = "female" }))).StatusCode);
    }

    [Fact]
    public async Task 체험_계정은_메일_인증_없이_성별만_넣으면_준비됨()
    {
        JsonElement r = await Ok(await client.PostAsync("/api/auth/guest", null));
        string token = r.GetProperty("token").GetString()!;
        JsonElement user = r.GetProperty("user");
        Assert.Equal("guest", user.GetProperty("provider").GetString());
        Assert.True(user.GetProperty("isGuest").GetBoolean());
        Assert.StartsWith("체험생-", user.GetProperty("nickname").GetString());
        Assert.False(user.GetProperty("ready").GetBoolean());

        user = await Ok(await client.SendAsync(Req(HttpMethod.Put, "/api/me/gender", token, new { gender = "female" })));
        Assert.True(user.GetProperty("ready").GetBoolean());
        Assert.False(user.GetProperty("emailVerified").GetBoolean());

        // 체험 계정마다 다른 계정
        JsonElement other = await Ok(await client.PostAsync("/api/auth/guest", null));
        Assert.NotEqual(user.GetProperty("id").GetString(), other.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task 회원_탈퇴하면_계정과_토큰이_지워진다()
    {
        var (token, user) = await KakaoLogin("bye");
        var res = await client.SendAsync(Req(HttpMethod.Delete, "/api/me", token));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/api/me", token))).StatusCode);

        // 같은 카카오 계정으로 다시 로그인하면 새 계정
        var (_, again) = await KakaoLogin("bye");
        Assert.NotEqual(user.GetProperty("id").GetString(), again.GetProperty("id").GetString());
    }

    [Fact]
    public async Task 다른_계정이_인증한_메일은_409()
    {
        var (a, _) = await KakaoLogin("a");
        await Ok(await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/start", a, new { email = "same@khu.ac.kr" })));
        await Ok(await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/verify", a, new { code = mail.LastCode })));

        var (b, _) = await KakaoLogin("b");
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/start", b, new { email = "same@khu.ac.kr" }));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task 인증번호를_5번_틀리면_다시_받아야_한다()
    {
        var (token, _) = await KakaoLogin();
        await Ok(await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/start", token, new { email = "x@khu.ac.kr" })));
        string wrong = mail.LastCode == "000000" ? "111111" : "000000";
        for (int i = 0; i < AccountStore.MaxCodeAttempts; i++)
            await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/verify", token, new { code = wrong }));
        var res = await client.SendAsync(Req(HttpMethod.Post, "/api/me/email/verify", token, new { code = mail.LastCode }));
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
    }

    [Fact]
    public async Task 닉네임과_취향_저장()
    {
        var (token, _) = await KakaoLogin();
        var empty = await client.SendAsync(Req(HttpMethod.Put, "/api/me/nickname", token, new { nickname = "  " }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var prefs = new { walkTimes = new[] { "evening" }, walkMinutes = 30, pace = "normal", tastes = new[] { "photo" }, talkStyle = "some", groupSize = "one" };
        JsonElement user = await Ok(await client.SendAsync(Req(HttpMethod.Put, "/api/me/preferences", token, prefs)));
        using JsonDocument saved = JsonDocument.Parse(user.GetProperty("preferencesJson").GetString()!);
        Assert.Equal(30, saved.RootElement.GetProperty("walkMinutes").GetInt32());

        var notJson = new HttpRequestMessage(HttpMethod.Put, "/api/me/preferences") { Content = new StringContent("{oops", Encoding.UTF8, "application/json") };
        notJson.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(notJson)).StatusCode);
    }

    [Fact]
    public async Task 로그아웃하면_토큰을_더_쓸_수_없다()
    {
        var (token, _) = await KakaoLogin();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Req(HttpMethod.Post, "/api/auth/logout", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/api/me", token))).StatusCode);
    }

    [Fact]
    public async Task 개발용_로그인은_설정이_없으면_없다()
    {
        var res = await client.PostAsJsonAsync("/api/auth/dev", new { name = "tester" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
