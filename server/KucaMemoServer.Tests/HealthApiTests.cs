using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KucaMemoServer.Models;
using KucaMemoServer.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KucaMemoServer.Tests;

/// <summary>건강 (걸음 수 · 칼로리 범위 · 주간 150분 목표 · 목표 맞춤 코스 · 주간 AI 코치) 검증</summary>
public class HealthApiTests : IDisposable
{
    sealed class TestClock : TimeProvider
    {
        // 2026-10-28 수요일 정오 (한국 시각)
        public DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(9));
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class FakeAi : IRouteAi
    {
        public bool IsConfigured => true;
        public string? CoachAnswer;
        public string LastCoachPrompt = "";
        public Task<string?> CompleteAsync(string system, string user, CancellationToken ct)
        {
            if (system.Contains("걷기 코치"))
            {
                LastCoachPrompt = user;
                return Task.FromResult(CoachAnswer);
            }
            return Task.FromResult<string?>("모름");   // 추천 등은 규칙으로
        }
    }

    readonly TestServer root = new();
    readonly TestClock clock = new();
    readonly List<IDisposable> owned = [];

    public HealthApiTests() => CoachService.ClearCache();

    HttpClient Client(IRouteAi? ai = null)
    {
        var server = root.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            if (ai != null) s.AddSingleton(ai);
        }));
        owned.Add(server);
        return server.CreateClient();
    }

    public void Dispose()
    {
        foreach (IDisposable d in owned) d.Dispose();
        root.Dispose();
        CoachService.ClearCache();
    }

    static async Task<JsonElement> Send(HttpClient client, HttpMethod method, string url, string? token, object? body, HttpStatusCode expected)
    {
        var req = new HttpRequestMessage(method, url);
        if (token != null) req.Headers.Authorization = new("Bearer", token);
        if (body != null) req.Content = JsonContent.Create(body);
        HttpResponseMessage res = await client.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        Assert.True(expected == res.StatusCode, $"{method} {url}: {(int)res.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    static async Task<string> Guest(HttpClient client) =>
        (await Send(client, HttpMethod.Post, "/api/auth/guest", null, null, HttpStatusCode.OK)).GetProperty("token").GetString()!;

    static async Task Walk(HttpClient client, string token, int seconds, int meters)
    {
        string id = (await Send(client, HttpMethod.Post, "/api/walks", token, new { }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        await Send(client, HttpMethod.Post, $"/api/walks/{id}/finish", token, new { distanceM = meters, durationSec = seconds }, HttpStatusCode.OK);
    }

    static readonly object Rec = new { minutes = 30, lat = 37.2440, lon = 127.0800 };

    // ───── 계산 ─────

    [Fact]
    public void 걸음_수와_칼로리_범위는_공식으로()
    {
        HealthEstimate unknown = HealthRules.Estimate(1400, 20, "normal", null);
        Assert.Equal(2000, unknown.Steps);                       // 1400m ÷ 0.7m
        Assert.Equal(58, unknown.KcalMin);                       // 3.5 × 50kg × 1/3h
        Assert.Equal(82, unknown.KcalMax);                       // 3.5 × 70kg × 1/3h
        Assert.Equal("moderate", unknown.Intensity);

        HealthEstimate known = HealthRules.Estimate(1400, 20, "normal", 60);
        Assert.Equal(63, known.KcalMin);                         // 60kg ± 10%
        Assert.Equal(77, known.KcalMax);
        Assert.Equal(unknown, HealthRules.Estimate(1400, 20, "normal", 0));   // 앱이 0 을 보내면 모름
        Assert.True(HealthRules.ValidWeight(0));
        Assert.False(HealthRules.ValidWeight(10));
        Assert.Equal("light", HealthRules.Estimate(1000, 20, "slow", null).Intensity);
        Assert.Equal("brisk", HealthRules.Estimate(1000, 20, "fast", null).Intensity);
    }

    [Fact]
    public void 주는_한국_시각_월요일_0시부터()
    {
        DateTimeOffset start = HealthRules.WeekStart(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(9)));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(9)), start);
        // 일요일 밤도 그 주 월요일
        Assert.Equal(start, HealthRules.WeekStart(new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.FromHours(9))));
    }

    // ───── 추천 · 산책 ─────

    [Fact]
    public async Task 추천_코스에_걸음_수와_칼로리_범위가_붙는다_로그인_없으면_목표는_없다()
    {
        HttpClient client = Client();
        JsonElement r = await Send(client, HttpMethod.Post, "/api/recommendations", null, Rec, HttpStatusCode.OK);
        JsonElement h = r.GetProperty("health");
        Assert.True(h.GetProperty("steps").GetInt32() > 0);
        Assert.True(h.GetProperty("kcalMin").GetInt32() < h.GetProperty("kcalMax").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("goal").ValueKind);
        JsonElement d = r.GetProperty("difficulty");
        Assert.Equal(HealthRules.Difficulty(r.GetProperty("totalDistanceM").GetInt32(), d.GetProperty("hills").GetInt32()).Label,
                     d.GetProperty("label").GetString());

        await Send(client, HttpMethod.Post, "/api/recommendations", null, new { minutes = 30, lat = 37.244, lon = 127.08, weightKg = 10 }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 몸무게를_보내면_칼로리_범위가_좁아진다()
    {
        HttpClient client = Client();
        JsonElement a = (await Send(client, HttpMethod.Post, "/api/recommendations", null, Rec, HttpStatusCode.OK)).GetProperty("health");
        JsonElement b = (await Send(client, HttpMethod.Post, "/api/recommendations", null,
            new { minutes = 30, lat = 37.2440, lon = 127.0800, weightKg = 60 }, HttpStatusCode.OK)).GetProperty("health");
        int wide = a.GetProperty("kcalMax").GetInt32() - a.GetProperty("kcalMin").GetInt32();
        int narrow = b.GetProperty("kcalMax").GetInt32() - b.GetProperty("kcalMin").GetInt32();
        Assert.True(narrow < wide, $"{narrow} < {wide}");
    }

    [Fact]
    public async Task 로그인하면_이번_주_목표와_이_코스의_관계를_알려_준다()
    {
        HttpClient client = Client();
        string me = await Guest(client);
        await Walk(client, me, 130 * 60, 9000);   // 이번 주 130분 → 20분 남음
        JsonElement r = await Send(client, HttpMethod.Post, "/api/recommendations", me, Rec, HttpStatusCode.OK);
        JsonElement goal = r.GetProperty("goal");
        Assert.Equal(130, goal.GetProperty("weekMinutes").GetInt32());
        Assert.Equal(20, goal.GetProperty("minutesLeft").GetInt32());
        bool completes = r.GetProperty("totalMinutes").GetInt32() >= 20;
        Assert.Equal(completes, goal.GetProperty("completesGoal").GetBoolean());
        Assert.Contains("이번 주 목표", goal.GetProperty("note").GetString());
    }

    [Fact]
    public async Task 산책을_끝내면_실제_걸음_수_칼로리와_이번_주_분이_나온다()
    {
        HttpClient client = Client();
        string me = await Guest(client);
        string id = (await Send(client, HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        JsonElement done = await Send(client, HttpMethod.Post, $"/api/walks/{id}/finish", me,
            new { distanceM = 2100, durationSec = 1800, weightKg = 55 }, HttpStatusCode.OK);
        Assert.Equal(3000, done.GetProperty("health").GetProperty("steps").GetInt32());
        Assert.Equal("normal", HealthRules.PaceFromSpeed(2100, 1800));   // 분당 70m
        JsonElement week = done.GetProperty("progress").GetProperty("week");
        Assert.Equal(30, week.GetProperty("weekMinutes").GetInt32());
        Assert.Equal(150, week.GetProperty("goalMinutes").GetInt32());
        Assert.Equal(120, week.GetProperty("minutesLeft").GetInt32());

        // 지난주 산책은 이번 주에 세지 않는다
        clock.Now = clock.Now.AddDays(5);   // 다음 주 월요일 (체험 계정 7일 안)
        JsonElement next = await Send(client, HttpMethod.Get, "/api/me/progress", me, null, HttpStatusCode.OK);
        Assert.Equal(0, next.GetProperty("week").GetProperty("weekMinutes").GetInt32());
    }

    [Fact]
    public void 코스_난이도는_거리와_오르막_장소_수로()
    {
        Assert.Equal("easy", HealthRules.Difficulty(1500, 0).Level);
        Assert.Equal("normal", HealthRules.Difficulty(1501, 0).Level);
        Assert.Equal("normal", HealthRules.Difficulty(1200, 1).Level);    // 짧아도 오르막이 있으면 하는 아님
        Assert.Equal("hard", HealthRules.Difficulty(2100, 1).Level);      // 오르막 + 2km 넘음
        Assert.Equal("hard", HealthRules.Difficulty(1000, 2).Level);      // 오르막 두 곳
        Assert.Equal("hard", HealthRules.Difficulty(3100, 0).Level);
        Assert.Equal("중", HealthRules.Difficulty(2500, 0).Label);
        Assert.Equal(("하", "상"), (HealthRules.Difficulty(800, 0).Label, HealthRules.Difficulty(3500, 0).Label));
    }

    [Fact]
    public void 센서_걸음_수는_거리와_맞을_때만_믿는다()
    {
        Assert.Equal(2400, HealthRules.MeasuredSteps(2400, 1400));   // 보폭 0.58m
        Assert.Null(HealthRules.MeasuredSteps(0, 1400));             // 앱이 0 을 보내면 모름
        Assert.Null(HealthRules.MeasuredSteps(null, 1400));
        Assert.Null(HealthRules.MeasuredSteps(20000, 1400));         // 보폭 0.07m: 흔들기 · 다른 걸음까지
        Assert.Null(HealthRules.MeasuredSteps(500, 1400));           // 보폭 2.8m: 센서가 덜 셈
        Assert.Null(HealthRules.MeasuredSteps(150, 80));             // 너무 짧아 맞춰 볼 수 없음
        Assert.False(HealthRules.ValidSteps(HealthRules.MaxSteps + 1));
    }

    [Fact]
    public async Task 산책_끝에_센서_걸음_수를_보내면_그_값을_쓰고_안_맞으면_어림값()
    {
        HttpClient client = Client();
        string me = await Guest(client);
        string id = (await Send(client, HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        JsonElement done = await Send(client, HttpMethod.Post, $"/api/walks/{id}/finish", me,
            new { distanceM = 2100, durationSec = 1800, weightKg = 0, steps = 2875 }, HttpStatusCode.OK);
        JsonElement health = done.GetProperty("health");
        Assert.Equal(2875, health.GetProperty("steps").GetInt32());
        Assert.True(health.GetProperty("stepsMeasured").GetBoolean());

        string id2 = (await Send(client, HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        JsonElement odd = await Send(client, HttpMethod.Post, $"/api/walks/{id2}/finish", me,
            new { distanceM = 2100, durationSec = 1800, steps = 40000 }, HttpStatusCode.OK);
        Assert.Equal(3000, odd.GetProperty("health").GetProperty("steps").GetInt32());   // 2100m ÷ 0.7m
        Assert.False(odd.GetProperty("health").GetProperty("stepsMeasured").GetBoolean());

        string id3 = (await Send(client, HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created)).GetProperty("id").GetString()!;
        await Send(client, HttpMethod.Post, $"/api/walks/{id3}/finish", me,
            new { distanceM = 2100, durationSec = 1800, steps = HealthRules.MaxSteps + 1 }, HttpStatusCode.BadRequest);
    }

    // ───── 주간 AI 코치 ─────

    [Fact]
    public async Task 코치는_AI_키가_없으면_틀_문장()
    {
        HttpClient client = Client();
        string me = await Guest(client);
        JsonElement first = await Send(client, HttpMethod.Get, "/api/me/coach", me, null, HttpStatusCode.OK);
        Assert.Equal("fallback", first.GetProperty("source").GetString());
        Assert.Contains("첫 산책", first.GetProperty("text").GetString());
        Assert.Equal(HealthRules.Disclaimer, first.GetProperty("note").GetString());
        await Send(client, HttpMethod.Get, "/api/me/coach", null, null, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 코치는_AI_문장을_쓰고_숫자_요약만_보낸다()
    {
        var ai = new FakeAi { CoachAnswer = "{\"text\":\"이번 주 40분 걸었어요! 목표까지 110분, 오늘 공강에 30분 살짝 걸어 볼까요?\"}" };
        HttpClient client = Client(ai);
        string me = await Guest(client);
        await Walk(client, me, 40 * 60, 2800);
        JsonElement c = await Send(client, HttpMethod.Get, "/api/me/coach", me, null, HttpStatusCode.OK);
        Assert.Equal("ai", c.GetProperty("source").GetString());
        Assert.StartsWith("이번 주 40분", c.GetProperty("text").GetString());
        Assert.Contains("40분", ai.LastCoachPrompt);
        Assert.Contains("110분", ai.LastCoachPrompt);
        Assert.DoesNotContain("kg", ai.LastCoachPrompt);     // 몸무게는 보내지 않는다
    }

    [Theory]
    [InlineData("{\"text\":\"이번 주 200kcal 태웠어요!\"}")]          // 지어낸 숫자
    [InlineData("{\"text\":\"꾸준히 걸으면 다이어트에 좋아요\"}")]     // 효과 단정
    [InlineData("{\"text\":\"혈당 관리에 도움이 돼요\"}")]            // 의료 이야기
    [InlineData("코치 말을 못 만들었어요")]                          // 형식 틀림
    public async Task 코치_AI_가_숫자를_지어내거나_효과를_단정하면_틀_문장으로(string answer)
    {
        HttpClient client = Client(new FakeAi { CoachAnswer = answer });
        string me = await Guest(client);
        await Walk(client, me, 40 * 60, 2800);
        JsonElement c = await Send(client, HttpMethod.Get, "/api/me/coach", me, null, HttpStatusCode.OK);
        Assert.Equal("fallback", c.GetProperty("source").GetString());
        Assert.Contains("목표까지 110분", c.GetProperty("text").GetString());
    }

    [Fact]
    public void 코치_틀_문장은_상황에_맞게()
    {
        Assert.Contains("채웠어요", CoachService.Fallback(new WeekGoal(160, 150, 0, 5), null, null));
        Assert.Contains("첫 산책", CoachService.Fallback(new WeekGoal(0, 150, 150, 0), null, null));
        Assert.Contains("수요일 저녁", CoachService.Fallback(new WeekGoal(40, 150, 110, 2), "수요일", "저녁"));
    }
}
