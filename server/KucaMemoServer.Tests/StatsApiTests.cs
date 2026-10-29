using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KucaMemoServer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KucaMemoServer.Tests;

/// <summary>지표 · 운영 점검 관리자 API (C-V6, docs/api.md 의 "관리자 API › 지표") 검증</summary>
public class StatsApiTests : IDisposable
{
    const string StudentHall = "way-474085535";

    sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class FakeAi(string? answer) : IRouteAi
    {
        public bool IsConfigured => true;
        public Task<string?> CompleteAsync(string system, string user, CancellationToken ct) => Task.FromResult(answer);
    }

    readonly TestServer root = new();
    readonly TestClock clock = new();
    readonly List<IDisposable> owned = [];
    HttpClient client;

    public StatsApiTests() => client = Start();

    HttpClient Start(IRouteAi? ai = null, Dictionary<string, string>? settings = null)
    {
        var server = root.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings ?? [])
                b.UseSetting(key, value);
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                if (ai != null)
                    s.AddSingleton(ai);
            });
        });
        owned.Add(server);
        return server.CreateClient();
    }

    public void Dispose()
    {
        foreach (IDisposable d in owned) d.Dispose();
        root.Dispose();
    }

    async Task<JsonElement> Send(HttpMethod method, string url, string? token, object? body, HttpStatusCode expected, bool admin = false)
    {
        var req = new HttpRequestMessage(method, url);
        if (token != null)
            req.Headers.Authorization = new("Bearer", token);
        if (admin)
            req.Headers.Add("X-Admin-Key", TestServer.AdminKey);
        if (body != null)
            req.Content = JsonContent.Create(body);
        HttpResponseMessage res = await client.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        Assert.True(expected == res.StatusCode, $"{method} {url}: {(int)res.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    Task<JsonElement> Stats(string query = "") => Send(HttpMethod.Get, "/api/admin/stats" + query, null, null, HttpStatusCode.OK, admin: true);

    async Task<string> User(string nickname)
    {
        string token = (await Send(HttpMethod.Post, "/api/auth/guest", null, null, HttpStatusCode.OK)).GetProperty("token").GetString()!;
        await Send(HttpMethod.Put, "/api/me/nickname", token, new { nickname }, HttpStatusCode.OK);
        await Send(HttpMethod.Put, "/api/me/gender", token, new { gender = "male" }, HttpStatusCode.OK);
        return token;
    }

    Task<JsonElement> Recommend() =>
        Send(HttpMethod.Post, "/api/recommendations", null, new { minutes = 60, lat = 37.2440, lon = 127.0800 }, HttpStatusCode.OK);

    static int I(JsonElement e, string path)
    {
        foreach (string part in path.Split('.')) e = e.GetProperty(part);
        return e.GetInt32();
    }

    static double? D(JsonElement e, string path)
    {
        foreach (string part in path.Split('.')) e = e.GetProperty(part);
        return e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();
    }

    [Fact]
    public async Task 관리자_키가_없으면_볼_수_없다()
    {
        await Send(HttpMethod.Get, "/api/admin/stats", null, null, HttpStatusCode.Forbidden);
        await Send(HttpMethod.Get, "/api/admin/health", null, null, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 추천_수락률_완주율_코스_별점을_추천_종류별로_센다()
    {
        string me = await User("민지");
        JsonElement rec = await Recommend();
        await Recommend();   // 받기만 하고 안 걸음
        string walkId = (await Send(HttpMethod.Post, "/api/walks", me, new { recommendationId = rec.GetProperty("id").GetString() }, HttpStatusCode.Created))
            .GetProperty("id").GetString()!;
        await Send(HttpMethod.Post, $"/api/walks/{walkId}/finish", me, new { distanceM = rec.GetProperty("totalDistanceM").GetInt32(), durationSec = 3600 }, HttpStatusCode.OK);
        await Send(HttpMethod.Post, $"/api/walks/{walkId}/review", me, new { courseRating = 4 }, HttpStatusCode.Created);
        string free = (await Send(HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        await Send(HttpMethod.Post, $"/api/walks/{free}/finish", me, new { distanceM = 100, durationSec = 600 }, HttpStatusCode.OK);

        JsonElement s = await Stats();
        Assert.Equal(2, I(s, "recommendations.fallback.shown"));
        Assert.Equal(1, I(s, "recommendations.fallback.started"));
        Assert.Equal(1, I(s, "recommendations.fallback.completed"));
        Assert.Equal(0.5, D(s, "recommendations.fallback.acceptRate"));
        Assert.Equal(1.0, D(s, "recommendations.fallback.completionRate"));
        Assert.Equal(4.0, D(s, "recommendations.fallback.avgCourseRating"));
        Assert.Equal(0, I(s, "recommendations.ai.shown"));
        Assert.Null(D(s, "recommendations.ai.acceptRate"));        // 분모 0 이면 null
        Assert.Equal(1, I(s, "walks.free.started"));
        Assert.Equal(0, I(s, "walks.free.completed"));
        Assert.Equal(2, I(s, "ai.recommend.fallback"));            // 키가 없으니 전부 규칙 추천
        Assert.Equal(1.0, D(s, "ai.recommend.fallbackRate"));
    }

    [Fact]
    public async Task AI_추천은_따로_센다()
    {
        client = Start(new FakeAi("""{"title":"코스","stops":[{"buildingId":"way-474085534","reason":"열람실에서 쉬기 좋아요"}]}"""));
        JsonElement rec = await Send(HttpMethod.Post, "/api/recommendations", null,
            new { minutes = 30, lat = 37.2440, lon = 127.0800, candidateBuildingIds = new[] { "way-474085534" } }, HttpStatusCode.OK);
        Assert.Equal("ai", rec.GetProperty("source").GetString());
        JsonElement s = await Stats();
        Assert.Equal(1, I(s, "recommendations.ai.shown"));
        Assert.Equal(1, I(s, "ai.recommend.ai"));
        Assert.Equal(0.0, D(s, "ai.recommend.fallbackRate"));
    }

    [Fact]
    public async Task 매칭_요청부터_완료까지_성사율을_센다()
    {
        string a = await User("가"), b = await User("나"), lonely = await User("혼자");
        await Send(HttpMethod.Post, "/api/match/queue", a, new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.OK);
        string id = (await Send(HttpMethod.Post, "/api/match/queue", b, new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.OK))
            .GetProperty("matchId").GetString()!;
        await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        JsonElement m = await Send(HttpMethod.Post, $"/api/match/{id}/accept", b, null, HttpStatusCode.OK);
        string spot = m.GetProperty("meetSpot").GetProperty("buildingId").GetString()!;
        await Send(HttpMethod.Post, $"/api/match/{id}/arrived", a, new { buildingId = spot }, HttpStatusCode.OK);
        string walkId = (await Send(HttpMethod.Post, $"/api/match/{id}/arrived", b, new { buildingId = spot }, HttpStatusCode.OK))
            .GetProperty("walkId").GetString()!;
        await Send(HttpMethod.Post, $"/api/walks/{walkId}/finish", a, new { distanceM = 3000, durationSec = 2400 }, HttpStatusCode.OK);
        await Send(HttpMethod.Post, $"/api/walks/{walkId}/finish", b, new { distanceM = 3000, durationSec = 2400 }, HttpStatusCode.OK);

        await Send(HttpMethod.Post, "/api/match/queue", lonely, new { minutes = 60, buildingId = StudentHall }, HttpStatusCode.OK);
        clock.Now += MatchRules.WaitLimit + TimeSpan.FromSeconds(1);
        await Send(HttpMethod.Get, "/api/match", lonely, null, HttpStatusCode.OK);   // 이때 못 찾음 처리

        JsonElement s = await Stats();
        Assert.Equal(3, I(s, "matching.requests"));
        Assert.Equal(1, I(s, "matching.proposed"));
        Assert.Equal(1, I(s, "matching.met"));
        Assert.Equal(1, I(s, "matching.walking"));
        Assert.Equal(1, I(s, "matching.completed"));
        Assert.Equal(0.333, D(s, "matching.successRate"));
        Assert.Equal(1.0, D(s, "matching.completionRate"));
        Assert.Equal(1, I(s, "matching.endReasons.completed"));
        Assert.Equal(1, I(s, "matching.endReasons.no_partner"));
        Assert.Equal(2, I(s, "walks.match.completed"));
        Assert.True(I(s, "ai.smalltalk.fallback") >= 1);   // AI 키가 없으니 예시로 대체
        Assert.Equal(1.0, D(s, "matching.acceptRateByReason.fallback"));   // 틀 문장 이유로 제안 → 둘 다 수락
        Assert.Null(D(s, "matching.acceptRateByReason.ai"));
        Assert.Equal(1, I(s, "ai.match_reason.fallback"));
    }

    [Fact]
    public async Task 기간을_벗어난_이벤트는_빠진다()
    {
        await Recommend();
        clock.Now += TimeSpan.FromDays(8);
        Assert.Equal(0, I(await Stats(), "recommendations.fallback.shown"));
        Assert.Equal(1, I(await Stats("?days=30"), "recommendations.fallback.shown"));
        Assert.Equal(StatsService.MaxDays, I(await Stats("?days=999"), "days"));
    }

    [Fact]
    public async Task 운영_점검은_키가_있는지만_보여_준다()
    {
        JsonElement off = await Send(HttpMethod.Get, "/api/admin/health", null, null, HttpStatusCode.OK, admin: true);
        Assert.False(off.GetProperty("aiKey").GetBoolean());
        Assert.False(off.GetProperty("demoPartner").GetBoolean());
        Assert.True(off.GetProperty("devLoginOff").GetBoolean());

        client = Start(settings: new() { ["Recommend:ApiKey"] = "secret-value-123", ["Match:DemoPartner"] = "true" });
        HttpResponseMessage res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/health") { Headers = { { "X-Admin-Key", TestServer.AdminKey } } });
        string text = await res.Content.ReadAsStringAsync();
        JsonElement on = JsonDocument.Parse(text).RootElement;
        Assert.True(on.GetProperty("aiKey").GetBoolean());
        Assert.True(on.GetProperty("demoPartner").GetBoolean());
        Assert.DoesNotContain("secret-value-123", text);   // 키 값은 절대 내보내지 않는다
    }

    [Fact]
    public async Task 지표에는_누가_했는지_남지_않는다()
    {
        string me = await User("민지");
        await Send(HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created);
        // 요청을 받은 (시계를 고정한) 서버의 저장소로 본다. root 는 실제 시계라 10/27 부터 체험 계정 세션이 만료로 보였음
        IServiceProvider services = ((WebApplicationFactory<Program>)owned[^1]).Services;
        string id = services.GetRequiredService<AccountStore>().FindBySession(me)!.Id;
        var rows = services.GetRequiredService<MetricsStore>().Since(DateTimeOffset.UnixEpoch);
        Assert.NotEmpty(rows);
        Assert.DoesNotContain(rows, r => r.Source == id);
    }
}
