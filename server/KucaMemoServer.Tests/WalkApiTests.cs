using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KucaMemoServer.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KucaMemoServer.Tests;

/// <summary>산책 · 완료 · 리뷰 · 보상 API (docs/api.md 의 "산책 · 완료 · 리뷰 · 보상") 검증</summary>
public class WalkApiTests : IDisposable
{
    const string StudentHall = "way-474085535";

    sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    readonly TestServer root = new();
    readonly WebApplicationFactory<Program> server;
    readonly HttpClient client;
    readonly TestClock clock = new();

    public WalkApiTests()
    {
        server = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        client = server.CreateClient();
    }

    public void Dispose()
    {
        server.Dispose();
        root.Dispose();
    }

    async Task<string> User(string nickname = "민지")
    {
        JsonElement login = await Send(HttpMethod.Post, "/api/auth/guest", null, null, HttpStatusCode.OK);
        string token = S(login, "token");
        await Send(HttpMethod.Put, "/api/me/nickname", token, new { nickname }, HttpStatusCode.OK);
        await Send(HttpMethod.Put, "/api/me/gender", token, new { gender = "female" }, HttpStatusCode.OK);
        return token;
    }

    async Task<JsonElement> Send(HttpMethod method, string url, string? token, object? body, HttpStatusCode expected)
    {
        var req = new HttpRequestMessage(method, url);
        if (token != null)
            req.Headers.Authorization = new("Bearer", token);
        if (body != null)
            req.Content = JsonContent.Create(body);
        HttpResponseMessage res = await client.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        Assert.True(expected == res.StatusCode, $"{method} {url}: {(int)res.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    static int I(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    /// <summary>60분 추천을 받아 그 코스로 산책을 시작한다</summary>
    async Task<(string WalkId, int Planned, string LastStop)> StartFromRecommendation(string token)
    {
        JsonElement rec = await Send(HttpMethod.Post, "/api/recommendations", null,
            new { minutes = 60, lat = 37.2440, lon = 127.0800 }, HttpStatusCode.OK);
        JsonElement walk = await Send(HttpMethod.Post, "/api/walks", token, new { recommendationId = S(rec, "id") }, HttpStatusCode.Created);
        Assert.Equal(I(rec, "totalDistanceM"), I(walk, "plannedDistanceM"));
        string[] stops = walk.GetProperty("stops").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(rec.GetProperty("stops").EnumerateArray().Select(x => S(x, "buildingId")), stops);
        return (S(walk, "id"), I(walk, "plannedDistanceM"), stops[^1]);
    }

    Task<JsonElement> Finish(string token, string walkId, int distanceM, int durationSec = 3600, string[]? visited = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        Send(HttpMethod.Post, $"/api/walks/{walkId}/finish", token,
             new { distanceM, durationSec, visitedBuildingIds = visited ?? Array.Empty<string>() }, expected);

    // ───── 완료 판정 · 보상 ─────

    [Fact]
    public async Task 추천_코스의_80퍼센트를_걸으면_완료_보상을_받는다()
    {
        string me = await User();
        (string walkId, int planned, _) = await StartFromRecommendation(me);
        int distance = (int)Math.Ceiling(planned * WalkRules.CompleteRatio);
        JsonElement r = await Finish(me, walkId, distance);

        Assert.True(r.GetProperty("completed").GetBoolean());
        JsonElement reward = r.GetProperty("reward");
        Assert.Equal(WalkRules.CompleteTickets, I(reward, "tickets"));
        int expectedXp = distance / WalkRules.MetersPerDistanceXp + WalkRules.WalkXp;
        Assert.Equal(expectedXp, I(reward, "xp"));
        Assert.Equal(["distance", "walk"], reward.GetProperty("breakdown").EnumerateArray().Select(x => S(x, "kind")));

        JsonElement progress = r.GetProperty("progress");
        Assert.Equal(expectedXp, I(progress, "xp"));
        Assert.Equal(1, I(progress, "totalWalks"));
        Assert.Equal(distance, I(progress, "totalDistanceM"));
        Assert.Equal(progress.GetRawText(), (await Send(HttpMethod.Get, "/api/me/progress", me, null, HttpStatusCode.OK)).GetRawText());
    }

    [Fact]
    public async Task 마지막_스팟에_도착하면_거리가_모자라도_완료()
    {
        string me = await User();
        (string walkId, _, string last) = await StartFromRecommendation(me);
        Assert.True((await Finish(me, walkId, 100, visited: [last])).GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task 모자라면_미완료이고_거리_경험치만_받는다()
    {
        string me = await User();
        (string walkId, int planned, _) = await StartFromRecommendation(me);
        int distance = (int)(planned * 0.5);
        JsonElement r = await Finish(me, walkId, distance);
        Assert.False(r.GetProperty("completed").GetBoolean());
        Assert.Equal(0, I(r.GetProperty("reward"), "tickets"));
        Assert.Equal(distance / WalkRules.MetersPerDistanceXp, I(r.GetProperty("reward"), "xp"));
        Assert.Equal(0, I(r.GetProperty("progress"), "totalWalks"));
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    public async Task 추천_없는_자유_산책은_500m_부터_완료(int distance, bool completed)
    {
        string me = await User();
        JsonElement walk = await Send(HttpMethod.Post, "/api/walks", me, new { minutes = 30 }, HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, walk.GetProperty("plannedDistanceM").ValueKind);
        Assert.Equal(completed, (await Finish(me, S(walk, "id"), distance)).GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task 시간에_비해_너무_멀면_거리를_깎는다()
    {
        string me = await User();
        string walkId = S(await Send(HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created), "id");
        JsonElement r = await Finish(me, walkId, 5000, durationSec: 600);   // 10분에 5km → 초속 2.5m 로 1.5km
        Assert.Equal(1500, I(r, "distanceM"));
    }

    [Fact]
    public async Task 권한과_잘못된_요청()
    {
        string me = await User(), other = await User("남");
        string walkId = S(await Send(HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created), "id");
        await Send(HttpMethod.Post, "/api/walks", null, new { }, HttpStatusCode.Unauthorized);
        await Send(HttpMethod.Post, "/api/walks", me, new { recommendationId = "rec_none" }, HttpStatusCode.NotFound);
        await Send(HttpMethod.Post, "/api/walks", me, new { minutes = 20 }, HttpStatusCode.BadRequest);
        JsonElement denied = await Finish(other, walkId, 1000, expected: HttpStatusCode.Forbidden);
        Assert.Equal("not_yours", S(denied, "reason"));
        await Finish(me, "w_none", 1000, expected: HttpStatusCode.NotFound);
        await Finish(me, walkId, -1, expected: HttpStatusCode.BadRequest);
        await Finish(me, walkId, 1000);
        await Finish(me, walkId, 1000, expected: HttpStatusCode.Conflict);   // 두 번 끝낼 수 없음
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(209, 2)]
    [InlineData(210, 3)]   // 100 + 110
    [InlineData(330, 4)]   // + 120
    [InlineData(1349, 10)]
    [InlineData(1450, 11)] // 1→11: 100 + 110 + … + 190
    public void 레벨은_다음까지_100에서_10씩_늘어난다(int xp, int level)
    {
        Assert.Equal(level, WalkRules.LevelOf(xp));
    }

    // ───── 리뷰 ─────

    [Fact]
    public async Task 리뷰는_끝낸_뒤_한_번_24시간_안에_내용_검사를_거쳐_쓴다()
    {
        string me = await User();
        string walkId = S(await Send(HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created), "id");
        await Send(HttpMethod.Post, $"/api/walks/{walkId}/review", me, new { courseRating = 5 }, HttpStatusCode.Conflict);  // 아직 안 끝냄
        await Finish(me, walkId, 800);

        await Send(HttpMethod.Post, $"/api/walks/{walkId}/review", me, new { courseRating = 6 }, HttpStatusCode.BadRequest);
        JsonElement blocked = await Send(HttpMethod.Post, $"/api/walks/{walkId}/review", me,
            new { courseRating = 5, text = "010-1234-5678 연락주세요" }, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("personal_info", S(blocked, "reason"));

        JsonElement ok = await Send(HttpMethod.Post, $"/api/walks/{walkId}/review", me,
            new { courseRating = 5, text = "벤치 쉬는 곳이 좋았어요" }, HttpStatusCode.Created);
        Assert.Equal(WalkRules.ReviewXp, I(ok.GetProperty("reward"), "xp"));
        await Send(HttpMethod.Post, $"/api/walks/{walkId}/review", me, new { courseRating = 4 }, HttpStatusCode.Conflict);

        string late = S(await Send(HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created), "id");
        await Finish(me, late, 800);
        clock.Now += WalkRules.ReviewWindow + TimeSpan.FromMinutes(1);
        await Send(HttpMethod.Post, $"/api/walks/{late}/review", me, new { courseRating = 5 }, HttpStatusCode.Conflict);
    }

    // ───── 매칭 산책 ─────

    /// <summary>두 사람을 매칭해서 산책 시작(walking)까지 보낸다</summary>
    async Task<(string A, string B, string WalkId)> MatchWalk()
    {
        string a = await User("가"), b = await User("나");
        await Send(HttpMethod.Post, "/api/match/queue", a, new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.OK);
        string id = S(await Send(HttpMethod.Post, "/api/match/queue", b, new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.OK), "matchId");
        await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        JsonElement m = await Send(HttpMethod.Post, $"/api/match/{id}/accept", b, null, HttpStatusCode.OK);
        string spot = S(m.GetProperty("meetSpot"), "buildingId");
        await Send(HttpMethod.Post, $"/api/match/{id}/arrived", a, new { buildingId = spot }, HttpStatusCode.OK);
        JsonElement w = await Send(HttpMethod.Post, $"/api/match/{id}/arrived", b, new { buildingId = spot }, HttpStatusCode.OK);
        return (a, b, S(w, "walkId"));
    }

    [Fact]
    public async Task 함께_걸으면_보너스를_받고_둘_다_끝내면_매칭이_완료된다()
    {
        (string a, string b, string walkId) = await MatchWalk();
        JsonElement r = await Finish(a, walkId, 3000);   // 30분 코스를 다 걸은 거리 (코스는 고른 시간을 채운다)
        Assert.Equal(3000 / WalkRules.MetersPerDistanceXp + WalkRules.WalkXp + WalkRules.TogetherXp, I(r.GetProperty("reward"), "xp"));
        Assert.Equal(1, I(r.GetProperty("progress"), "togetherWalks"));
        Assert.Equal("walking", S(await Send(HttpMethod.Get, "/api/match", b, null, HttpStatusCode.OK), "state"));

        await Finish(b, walkId, 2900);
        JsonElement ended = await Send(HttpMethod.Get, "/api/match", a, null, HttpStatusCode.OK);
        Assert.Equal("ended", S(ended, "state"));
        Assert.Equal("completed", S(ended, "endReason"));
    }

    [Fact]
    public async Task 매칭_산책_리뷰도_코스_별점만_받는다()
    {
        (string a, string b, string walkId) = await MatchWalk();
        await Finish(a, walkId, 1000);
        await Send(HttpMethod.Post, $"/api/walks/{walkId}/review", a, new { courseRating = 1, partnerRating = 1 }, HttpStatusCode.Created);

        // 별점을 낮게 줘도 상대는 계속 짝을 찾을 수 있다 (상대 평가 없음)
        await Finish(b, walkId, 1000);
        string c = await User("다");
        await Send(HttpMethod.Post, "/api/match/queue", b, new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.OK);
        JsonElement s = await Send(HttpMethod.Post, "/api/match/queue", c, new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.OK);
        Assert.Equal("proposed", S(s, "state"));
    }

    [Fact]
    public async Task 탈퇴하면_산책과_경험치가_지워진다()
    {
        string me = await User();
        string walkId = S(await Send(HttpMethod.Post, "/api/walks", me, new { }, HttpStatusCode.Created), "id");
        await Finish(me, walkId, 800);
        string id = server.Services.GetRequiredService<AccountStore>().FindBySession(me)!.Id;
        await Send(HttpMethod.Delete, "/api/me", me, null, HttpStatusCode.NoContent);
        WalkStore store = server.Services.GetRequiredService<WalkStore>();
        Assert.Null(store.Find(walkId, id));
        Assert.Equal(0, store.TotalXp(id));
    }
}
