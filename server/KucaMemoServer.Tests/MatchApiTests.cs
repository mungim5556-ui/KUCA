using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KucaMemoServer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KucaMemoServer.Tests;

/// <summary>실시간 산책 매칭 API (docs/api.md 의 "매칭") 검증. 시각은 테스트가 직접 넘긴다. AI 는 부르지 않는다.</summary>
public class MatchApiTests : IDisposable
{
    const string StudentHall = "way-474085535", Library = "way-474085534", Engineering = "way-455725718";

    /// <summary>테스트가 앞으로 돌릴 수 있는 시계</summary>
    sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    readonly TestServer root = new();
    readonly WebApplicationFactory<Program> server;
    readonly HttpClient client;
    readonly TestClock clock = new();

    internal MatchApiTests(bool demo, IRouteAi? ai = null)
    {
        server = root.WithWebHostBuilder(b =>
        {
            if (demo)
                b.UseSetting("Match:DemoPartner", "true");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                if (ai != null)
                    s.AddSingleton(ai);
            });
        });
        client = server.CreateClient();
    }

    public MatchApiTests() : this(false) { }

    public void Dispose()
    {
        server.Dispose();
        root.Dispose();
    }

    /// <summary>체험 계정을 만들고 닉네임 · 성별 · 취향까지 넣어 매칭할 수 있게 한다</summary>
    async Task<string> User(string nickname, string gender = "female", object? prefs = null)
    {
        JsonElement login = await Json(await client.PostAsync("/api/auth/guest", null));
        string token = login.GetProperty("token").GetString()!;
        await Send(HttpMethod.Put, "/api/me/nickname", token, new { nickname }, HttpStatusCode.OK);
        await Send(HttpMethod.Put, "/api/me/gender", token, new { gender }, HttpStatusCode.OK);
        if (prefs != null)
            await Send(HttpMethod.Put, "/api/me/preferences", token, prefs, HttpStatusCode.OK);
        return token;
    }

    async Task<HttpResponseMessage> Call(HttpMethod method, string url, string? token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token != null)
            req.Headers.Authorization = new("Bearer", token);
        if (body != null)
            req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    async Task<JsonElement> Send(HttpMethod method, string url, string? token, object? body, HttpStatusCode expected)
    {
        HttpResponseMessage res = await Call(method, url, token, body);
        string text = await res.Content.ReadAsStringAsync();
        Assert.True(expected == res.StatusCode, $"{method} {url}: {(int)res.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    Task<JsonElement> Join(string token, int minutes = 30, string building = StudentHall, string? matchGender = null) =>
        Send(HttpMethod.Post, "/api/match/queue", token, new { minutes, matchGender, buildingId = building }, HttpStatusCode.OK);

    Task<JsonElement> State(string token) => Send(HttpMethod.Get, "/api/match", token, null, HttpStatusCode.OK);

    static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    /// <summary>두 사람을 짝지어 둘 다 수락한 상태(meeting)까지 보낸다</summary>
    async Task<(string A, string B, string MatchId, JsonElement Meeting)> Meet()
    {
        string a = await User("민지", prefs: new { tastes = new[] { "photo", "quiet" }, talkStyle = "some" });
        string b = await User("서연", prefs: new { tastes = new[] { "photo", "food" }, talkStyle = "some" });
        await Join(a);
        JsonElement proposed = await Join(b, building: Library);
        string id = S(proposed, "matchId");
        await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        JsonElement meeting = await Send(HttpMethod.Post, $"/api/match/{id}/accept", b, null, HttpStatusCode.OK);
        return (a, b, id, meeting);
    }

    // ───── 흐름 ─────

    [Fact]
    public async Task 두_사람이_짝이_되어_만나고_산책을_시작한다()
    {
        string a = await User("민지", prefs: new { tastes = new[] { "photo", "quiet" } });
        string b = await User("서연", prefs: new { tastes = new[] { "photo", "food" } });

        JsonElement w = await Join(a);
        Assert.Equal("waiting", S(w, "state"));
        Assert.Equal("2026-10-20T12:10:00Z", S(w, "waitingUntil"));

        JsonElement p = await Join(b, building: Library);
        Assert.Equal("proposed", S(p, "state"));
        Assert.Equal("민지", S(p.GetProperty("partner"), "nickname"));
        Assert.Equal(["photo"], p.GetProperty("partner").GetProperty("commonTastes").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(JsonValueKind.Null, p.GetProperty("partner").GetProperty("appearance").ValueKind);
        Assert.False(p.GetProperty("partner").TryGetProperty("gender", out _));   // 성별은 상대에게 안 보임
        string id = S(p, "matchId");
        Assert.Equal(id, S(await State(a), "matchId"));

        JsonElement half = await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        Assert.Equal("proposed", S(half, "state"));
        Assert.Equal("accepted", S(half, "myAnswer"));

        JsonElement m = await Send(HttpMethod.Post, $"/api/match/{id}/accept", b, null, HttpStatusCode.OK);
        Assert.Equal("meeting", S(m, "state"));
        JsonElement spot = m.GetProperty("meetSpot");
        Assert.False(string.IsNullOrEmpty(S(spot, "meetSpecies")));      // 만남 스팟 = meetSpecies 가 있는 곳
        Assert.Equal(3, m.GetProperty("smalltalk").GetArrayLength());
        Assert.Equal("fallback", S(m, "smalltalkSource"));               // AI 키가 없으면 A 의 예시
        Assert.Equal("2026-10-20T12:10:00Z", S(m, "meetBy"));

        await Send(HttpMethod.Put, $"/api/match/{id}/appearance", a, new { text = "회색 맨투맨, 파란 백팩" }, HttpStatusCode.OK);
        JsonElement seenByB = await State(b);
        Assert.Equal("회색 맨투맨, 파란 백팩", S(seenByB.GetProperty("partner"), "appearance"));
        Assert.Equal(JsonValueKind.Null, seenByB.GetProperty("myAppearance").ValueKind);

        string spotId = S(spot, "buildingId");
        JsonElement one = await Send(HttpMethod.Post, $"/api/match/{id}/arrived", a, new { buildingId = spotId }, HttpStatusCode.OK);
        Assert.Equal("meeting", S(one, "state"));
        Assert.True(one.GetProperty("arrived").GetProperty("me").GetBoolean());
        JsonElement walking = await Send(HttpMethod.Post, $"/api/match/{id}/arrived", b, new { buildingId = spotId }, HttpStatusCode.OK);
        Assert.Equal("walking", S(walking, "state"));
        Assert.StartsWith("w_", S(walking, "walkId"));

        // 산책 중 취소하면 두 사람 모두 끝, 상대에게는 "partner" 가 취소한 것으로
        await Send(HttpMethod.Post, $"/api/match/{id}/cancel", a, new { reason = "other" }, HttpStatusCode.OK);
        JsonElement endedB = await State(b);
        Assert.Equal("ended", S(endedB, "state"));
        Assert.Equal("cancelled", S(endedB, "endReason"));
        Assert.Equal("partner", S(endedB, "cancelledBy"));
        Assert.Equal(JsonValueKind.Null, endedB.GetProperty("partner").ValueKind);  // 인상착의도 사라짐
    }

    [Fact]
    public async Task 만남_스팟은_두_사람_사이의_가까운_곳이다()
    {
        string a = await User("가"), b = await User("나");
        await Join(a, building: StudentHall);
        string id = S(await Join(b, building: StudentHall), "matchId");
        await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        JsonElement m = await Send(HttpMethod.Post, $"/api/match/{id}/accept", b, null, HttpStatusCode.OK);
        Assert.Equal(StudentHall, S(m.GetProperty("meetSpot"), "buildingId"));  // 둘 다 학생회관이면 학생회관
        Assert.Equal("pigeon", S(m.GetProperty("meetSpot"), "meetSpecies"));
    }

    // ───── 짝 찾기 조건 ─────

    [Fact]
    public async Task 동성만을_고르면_다른_성별과_짝이_되지_않는다()
    {
        string a = await User("가", "female"), b = await User("나", "male"), c = await User("다", "female");
        await Join(a, matchGender: "same");
        Assert.Equal("waiting", S(await Join(b), "state"));
        Assert.Equal("proposed", S(await Join(c), "state"));
        Assert.Equal("proposed", S(await State(a), "state"));
        Assert.Equal("waiting", S(await State(b), "state"));
    }

    [Fact]
    public async Task 시간이_다르면_짝이_되지_않는다()
    {
        string a = await User("가"), b = await User("나"), c = await User("다");
        await Join(a, minutes: 30);
        Assert.Equal("waiting", S(await Join(b, minutes: 60), "state"));
        Assert.Equal("proposed", S(await Join(c, minutes: 30), "state"));
    }

    [Fact]
    public async Task 함께_걷기는_최소_30분이다()
    {
        string a = await User("가");
        JsonElement r = await Send(HttpMethod.Post, "/api/match/queue", a, new { minutes = 15, buildingId = StudentHall }, HttpStatusCode.BadRequest);
        Assert.Contains("30분", r.GetProperty("error").GetString());
        Assert.Equal("idle", S(await State(a), "state"));
    }

    /// <summary>B 와 C 가 서로 거절해서 둘 다 기다리는 상태를 만든다 (거절한 둘은 1시간 동안 다시 안 묶임)</summary>
    async Task TwoWaiting(string b, string bBuilding, string c, string cBuilding)
    {
        await Join(b, building: bBuilding);
        string id = S(await Join(c, building: cBuilding), "matchId");
        await Send(HttpMethod.Post, $"/api/match/{id}/decline", c, null, HttpStatusCode.OK);
        Assert.Equal("waiting", S(await Join(c, building: cBuilding), "state"));
        Assert.Equal("waiting", S(await State(b), "state"));
    }

    [Fact]
    public async Task 건물이_가까운_사람과_먼저_짝이_된다()
    {
        string far = await User("멀리"), near = await User("가까이"), me = await User("나");
        await TwoWaiting(far, "way-474521125", near, Library);   // 천문대 · 중앙도서관
        JsonElement p = await Join(me, building: StudentHall);
        Assert.Equal("가까이", S(p.GetProperty("partner"), "nickname"));
    }

    [Fact]
    public async Task 거리가_같으면_취향이_겹치는_사람과_먼저_짝이_된다()
    {
        string other = await User("다름", prefs: new { tastes = new[] { "food" } });
        string same = await User("비슷", prefs: new { tastes = new[] { "photo", "quiet" }, walkTimes = new[] { "evening" } });
        string me = await User("나", prefs: new { tastes = new[] { "photo", "quiet" }, walkTimes = new[] { "evening" } });
        await TwoWaiting(other, Library, same, Library);
        JsonElement p = await Join(me, building: Library);
        Assert.Equal("비슷", S(p.GetProperty("partner"), "nickname"));
        Assert.Equal(["photo", "quiet"], p.GetProperty("partner").GetProperty("commonTastes").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task 차단한_사람과는_다시_짝이_되지_않는다()
    {
        (string a, string b, string id, _) = await Meet();
        await Send(HttpMethod.Post, "/api/blocks", a, new { matchId = id }, HttpStatusCode.Created);
        Assert.Equal("blocked", S(await State(b), "endReason"));

        await Join(a);
        Assert.Equal("waiting", S(await Join(b), "state"));
    }

    [Fact]
    public async Task 거절하면_나는_끝나고_상대는_다시_기다린다()
    {
        string a = await User("가"), b = await User("나");
        await Join(a);
        string id = S(await Join(b), "matchId");
        await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        JsonElement mine = await Send(HttpMethod.Post, $"/api/match/{id}/decline", b, null, HttpStatusCode.OK);
        Assert.Equal("declined", S(mine, "endReason"));
        Assert.Equal("me", S(mine, "cancelledBy"));
        Assert.Equal("waiting", S(await State(a), "state"));

        // 방금 거절한 사람과는 바로 다시 짝짓지 않는다
        Assert.Equal("waiting", S(await Join(b), "state"));
    }

    // ───── 시간 ─────

    [Fact]
    public async Task 대기_10분이_지나면_못_찾음으로_끝난다()
    {
        string a = await User("가");
        await Join(a);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        JsonElement s = await State(a);
        Assert.Equal("ended", S(s, "state"));
        Assert.Equal("no_partner", S(s, "endReason"));

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal("idle", S(await State(a), "state"));
    }

    [Fact]
    public async Task 응답이_없으면_수락한_쪽은_다시_기다리고_안_한_쪽은_끝난다()
    {
        string a = await User("가"), b = await User("나");
        await Join(a);
        string id = S(await Join(b), "matchId");
        await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("waiting", S(await State(a), "state"));
        Assert.Equal("timeout", S(await State(b), "endReason"));
        await Send(HttpMethod.Post, $"/api/match/{id}/accept", b, null, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task 만남_10분_안에_둘_다_안_오면_노쇼로_끝난다()
    {
        (string a, string b, string id, JsonElement m) = await Meet();
        await Send(HttpMethod.Post, $"/api/match/{id}/arrived", a, new { buildingId = S(m.GetProperty("meetSpot"), "buildingId") }, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Equal("noshow", S(await State(a), "endReason"));
        Assert.Equal("noshow", S(await State(b), "endReason"));
    }

    // ───── 인상착의 · 스몰토크 ─────

    [Fact]
    public async Task 인상착의는_내용_검사를_거친다()
    {
        (string a, _, string id, _) = await Meet();
        JsonElement blocked = await Send(HttpMethod.Put, $"/api/match/{id}/appearance", a, new { text = "010-1234-5678 로 연락 주세요" }, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("personal_info", S(blocked, "reason"));

        root.Moderator.Next = new ModerationResult(Verdict.Review, ModerationReasons.Abusive, null, false);
        await Send(HttpMethod.Put, $"/api/match/{id}/appearance", a, new { text = "애매한 표현" }, HttpStatusCode.UnprocessableEntity);
        root.Moderator.Next = null;

        await Send(HttpMethod.Put, $"/api/match/{id}/appearance", a, new { text = new string('가', 61) }, HttpStatusCode.BadRequest);
        Assert.Equal(JsonValueKind.Null, (await State(a)).GetProperty("myAppearance").ValueKind);
    }

    [Fact]
    public async Task 다음_주제는_새_예시_3개이고_다섯_번까지다()
    {
        (string a, string b, string id, JsonElement m) = await Meet();
        var shown = m.GetProperty("smalltalk").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        string[] examples = JsonDocument.Parse(File.ReadAllText(DataPath("smalltalk.json"))).RootElement
            .EnumerateArray().Select(e => e.GetProperty("text").GetString()!).ToArray();
        Assert.All(shown, t => Assert.Contains(t, examples));

        JsonElement next = await Send(HttpMethod.Post, $"/api/match/{id}/smalltalk", a, null, HttpStatusCode.OK);
        var topics = next.GetProperty("smalltalk").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(3, topics.Count);
        Assert.Empty(topics.Intersect(shown));   // 이미 본 주제는 다시 안 나옴
        Assert.Equal(topics, (await State(b)).GetProperty("smalltalk").EnumerateArray().Select(x => x.GetString()!));  // 상대도 같은 카드

        for (int i = 1; i < MatchRules.MaxSmalltalkRefresh; i++)
            await Send(HttpMethod.Post, $"/api/match/{id}/smalltalk", b, null, HttpStatusCode.OK);
        await Send(HttpMethod.Post, $"/api/match/{id}/smalltalk", a, null, HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("{\"topics\":[\"학식 뭐 좋아해요?\",\"요즘 날씨 어때요?\",\"전공이 뭐예요?\"]}", 3)]
    [InlineData("{\"topics\":[\"하나만\"]}", 0)]
    [InlineData("주제를 못 만들었어요", 0)]
    [InlineData("{\"topics\":[\"링크 http://x.com 보세요\",\"둘\",\"셋\"]}", 0)]
    public void AI_주제_답은_3개가_맞을_때만_쓴다(string answer, int expected)
    {
        Assert.Equal(expected, SmalltalkGenerator.Parse(answer)?.Count ?? 0);
    }

    // ───── 함께 걸을 코스 · 생동감 (10/25 피드백) ─────

    static JsonElement Course(JsonElement state) => state.GetProperty("course");
    static string[] StopIds(JsonElement course) => course.GetProperty("stops").EnumerateArray().Select(x => S(x, "buildingId")).ToArray();

    [Fact]
    public async Task 매칭되면_만남_스팟에서_출발하는_코스가_나오고_둘_다_동의하면_확정된다()
    {
        (string a, string b, string id, JsonElement m) = await Meet();
        JsonElement course = Course(m);
        string spot = S(m.GetProperty("meetSpot"), "buildingId");
        Assert.NotEmpty(StopIds(course));
        Assert.DoesNotContain(spot, StopIds(course));                                       // 만남 스팟은 코스에서 뺀다
        Assert.InRange(course.GetProperty("totalMinutes").GetInt32(), 1, (int)(30 * RecommendRules.WalkTimeRatio));
        Assert.False(course.GetProperty("myAgree").GetBoolean());
        Assert.False(course.GetProperty("confirmed").GetBoolean());
        Assert.Equal(MatchRules.MaxCourseChanges, course.GetProperty("changesLeft").GetInt32());
        Assert.Equal(S(course, "recommendationId"), S(Course(await State(b)), "recommendationId"));   // 두 사람이 같은 코스
        // 대략 칼로리 · 난이도 (몸무게는 매칭에서 받지 않으니 50~70kg 범위)
        JsonElement health = course.GetProperty("health");
        Assert.True(health.GetProperty("kcalMax").GetInt32() > health.GetProperty("kcalMin").GetInt32());
        Assert.Contains(S(course.GetProperty("difficulty"), "level"), new[] { "easy", "normal", "hard" });

        JsonElement mine = Course(await Send(HttpMethod.Post, $"/api/match/{id}/course/agree", a, null, HttpStatusCode.OK));
        Assert.True(mine.GetProperty("myAgree").GetBoolean());
        Assert.False(mine.GetProperty("confirmed").GetBoolean());
        Assert.True(Course(await State(b)).GetProperty("partnerAgree").GetBoolean());

        JsonElement both = Course(await Send(HttpMethod.Post, $"/api/match/{id}/course/agree", b, null, HttpStatusCode.OK));
        Assert.True(both.GetProperty("confirmed").GetBoolean());
        await Send(HttpMethod.Post, $"/api/match/{id}/course/next", a, null, HttpStatusCode.Conflict);   // 확정 뒤엔 못 바꿈
    }

    [Fact]
    public async Task 다른_코스는_세_번까지이고_동의를_다시_받는다()
    {
        (string a, string b, string id, JsonElement m) = await Meet();
        await Send(HttpMethod.Post, $"/api/match/{id}/course/agree", a, null, HttpStatusCode.OK);
        string[] before = StopIds(Course(m));

        JsonElement next = Course(await Send(HttpMethod.Post, $"/api/match/{id}/course/next", b, null, HttpStatusCode.OK));
        Assert.NotEqual(S(Course(m), "recommendationId"), S(next, "recommendationId"));
        Assert.Empty(StopIds(next).Intersect(before));                                       // 실제로 다른 장소
        Assert.False(next.GetProperty("partnerAgree").GetBoolean());                         // a 의 동의가 지워짐
        Assert.Equal(MatchRules.MaxCourseChanges - 1, next.GetProperty("changesLeft").GetInt32());

        for (int i = 1; i < MatchRules.MaxCourseChanges; i++)
            await Send(HttpMethod.Post, $"/api/match/{id}/course/next", a, null, HttpStatusCode.OK);
        await Send(HttpMethod.Post, $"/api/match/{id}/course/next", a, null, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task 출발하면_그_코스로_완료를_판정한다()
    {
        (string a, string b, string id, JsonElement m) = await Meet();
        JsonElement course = Course(m);
        string spot = S(m.GetProperty("meetSpot"), "buildingId");
        await Send(HttpMethod.Post, $"/api/match/{id}/arrived", a, new { buildingId = spot }, HttpStatusCode.OK);
        JsonElement w = await Send(HttpMethod.Post, $"/api/match/{id}/arrived", b, new { buildingId = spot }, HttpStatusCode.OK);
        Assert.True(Course(w).GetProperty("confirmed").GetBoolean());                        // 동의를 다 안 해도 출발하면 확정

        WalkStore walks = server.Services.GetRequiredService<WalkStore>();
        WalkStore.Walk walk = walks.Find(S(w, "walkId"), UserIdOf(a))!;
        Assert.Equal(course.GetProperty("totalDistanceM").GetInt32(), walk.PlannedDistanceM);
        Assert.Equal(StopIds(course), walk.Stops);

        int planned = course.GetProperty("totalDistanceM").GetInt32();
        JsonElement done = await Send(HttpMethod.Post, $"/api/walks/{S(w, "walkId")}/finish", a,
            new { distanceM = (int)(planned * 0.5), durationSec = 1800 }, HttpStatusCode.OK);
        Assert.False(done.GetProperty("completed").GetBoolean());                            // 코스의 80% 미만이면 미완료
    }

    [Fact]
    public async Task 대기_중에는_지금_찾는_사람_수를_보여_준다()
    {
        string a = await User("가"), b = await User("나");
        Assert.Equal(0, (await Join(a, minutes: 30)).GetProperty("nowSearching").GetInt32());
        Assert.Equal(1, (await Join(b, minutes: 60)).GetProperty("nowSearching").GetInt32());
        Assert.Equal(1, (await State(a)).GetProperty("nowSearching").GetInt32());
    }

    [Fact]
    public async Task 스몰토크_카드에는_분류와_이모지가_붙는다()
    {
        (string a, _, string id, JsonElement m) = await Meet();
        JsonElement[] cards = m.GetProperty("smalltalkCards").EnumerateArray().ToArray();
        Assert.Equal(3, cards.Length);
        Assert.Equal(m.GetProperty("smalltalk").EnumerateArray().Select(x => x.GetString()), cards.Select(c => S(c, "text")));
        Assert.All(cards, c =>
        {
            Assert.Contains(S(c, "topic"), SmalltalkCard.Topics);
            Assert.False(string.IsNullOrEmpty(S(c, "emoji")));
        });
        Assert.Contains(cards, c => S(c, "topic") != "대화");                                  // 예시는 원래 분류(공강·학식…)를 그대로

        JsonElement next = await Send(HttpMethod.Post, $"/api/match/{id}/smalltalk", a, null, HttpStatusCode.OK);
        Assert.Equal(3, next.GetProperty("smalltalkCards").GetArrayLength());
    }

    [Fact]
    public void AI_주제_카드는_분류를_읽고_모르는_분류는_대화로()
    {
        var cards = SmalltalkGenerator.Parse("""
            {"topics":[{"topic":"학식","text":"학식 뭐 좋아해요?"},{"topic":"이상한분류","text":"요즘 어때요?"},{"topic":"날씨","text":"흐린 날 뭐 해요?"}]}
            """)!;
        Assert.Equal(["학식", "대화", "날씨"], cards.Select(c => c.Topic));
        Assert.Equal("🍚", cards[0].Emoji);
    }

    // ───── AI 매칭 이유 ─────

    [Fact]
    public async Task 제안_화면에_왜_연결했는지_한_줄이_나온다()
    {
        string a = await User("민지", prefs: new { tastes = new[] { "photo", "quiet" } });
        string b = await User("서연", prefs: new { tastes = new[] { "photo", "food" } });
        await Join(a);
        JsonElement p = (await Join(b)).GetProperty("partner");
        Assert.Equal("둘 다 사진 찍기를 좋아해서 연결했어요", S(p, "whyMatched"));     // AI 키가 없으면 틀 문장
        Assert.Equal("fallback", S(p, "whyMatchedSource"));
        Assert.Equal(S(p, "whyMatched"), S((await State(a)).GetProperty("partner"), "whyMatched"));   // 두 사람이 같은 문장
    }

    [Theory]
    [InlineData(new[] { "quiet" }, new string[0], null, 900, "둘 다 조용한 곳을 좋아해서 연결했어요")]
    [InlineData(new string[0], new[] { "evening" }, null, 900, "둘 다 저녁에 걷는 걸 좋아해요")]
    [InlineData(new string[0], new string[0], "quiet", 900, "둘 다 조용히 함께 걷기를 좋아해요")]
    [InlineData(new string[0], new string[0], null, 120, "지금 가까이 있어서 금방 만날 수 있어요")]
    [InlineData(new string[0], new string[0], null, 900, "같은 30분 산책을 찾고 있어서 연결했어요")]
    public void 틀_문장은_가장_구체적인_공통점으로_만든다(string[] tastes, string[] times, string? talk, int distance, string expected)
    {
        Assert.Equal(expected, MatchExplainer.Fallback(new MatchReasonInput(tastes, times, talk, distance, 30)));
    }

    [Theory]
    [InlineData("{\"reason\":\"둘 다 저녁 산책을 좋아해서 연결했어요\"}", true)]
    [InlineData("{\"reason\":\"\"}", false)]
    [InlineData("링크 http://x.com", false)]
    [InlineData("이유를 못 썼어요", false)]
    public void AI_이유는_한_문장_형식일_때만_쓴다(string answer, bool ok)
    {
        Assert.Equal(ok, MatchExplainer.Parse(answer) != null);
    }

    // ───── 권한 · 안전 ─────

    [Fact]
    public async Task 로그인과_계정_설정이_필요하다()
    {
        await Send(HttpMethod.Get, "/api/match", null, null, HttpStatusCode.Unauthorized);
        JsonElement guest = await Json(await client.PostAsync("/api/auth/guest", null));
        JsonElement denied = await Send(HttpMethod.Post, "/api/match/queue", S(guest, "token"),
            new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.Forbidden);
        Assert.Equal("not_ready", S(denied, "reason"));
    }

    [Fact]
    public async Task 잘못된_요청과_중복_대기()
    {
        string a = await User("가");
        await Send(HttpMethod.Post, "/api/match/queue", a, new { minutes = 20, buildingId = StudentHall }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/match/queue", a, new { minutes = 30, buildingId = "way-0" }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/match/queue", a, new { minutes = 30, buildingId = StudentHall, matchGender = "male" }, HttpStatusCode.BadRequest);
        await Join(a);
        await Send(HttpMethod.Post, "/api/match/queue", a, new { minutes = 30, buildingId = StudentHall }, HttpStatusCode.Conflict);
        await Send(HttpMethod.Delete, "/api/match/queue", a, null, HttpStatusCode.NoContent);
        Assert.Equal("idle", S(await State(a), "state"));
    }

    [Fact]
    public async Task 남의_매칭은_건드릴_수_없다()
    {
        (_, _, string id, JsonElement m) = await Meet();
        string stranger = await User("남");
        JsonElement r = await Send(HttpMethod.Post, $"/api/match/{id}/arrived", stranger, new { buildingId = S(m.GetProperty("meetSpot"), "buildingId") }, HttpStatusCode.Forbidden);
        Assert.Equal("not_yours", S(r, "reason"));
        await Send(HttpMethod.Post, "/api/reports", stranger, new { matchId = id, reason = "rude" }, HttpStatusCode.Forbidden);
        await Send(HttpMethod.Post, "/api/match/m_none/accept", stranger, null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 신고하면_끝나고_자동_차단되며_신고가_쌓이면_짝에서_빠진다()
    {
        (string a, string b, string id, _) = await Meet();
        await Send(HttpMethod.Post, "/api/reports", a, new { matchId = id, reason = "wrong" }, HttpStatusCode.BadRequest);
        await Send(HttpMethod.Post, "/api/reports", a, new { matchId = id, reason = "rude", text = "말을 함부로 했어요" }, HttpStatusCode.Created);
        Assert.Equal("reported", S(await State(b), "endReason"));
        Assert.Equal("me", S(await State(a), "cancelledBy"));

        // 지난 매칭도 신고할 수 있다 (기록이 남아 있는 동안)
        await Send(HttpMethod.Post, "/api/reports", a, new { matchId = id, reason = "unsafe" }, HttpStatusCode.Created);

        MatchStore store = server.Services.GetRequiredService<MatchStore>();
        string bId = store.FindRecord(id)!.UserA == UserIdOf(a) ? store.FindRecord(id)!.UserB : store.FindRecord(id)!.UserA;
        store.AddReport(id, "someone", bId, "rude", null, clock.Now);   // 세 번째 신고
        string c = await User("다");
        await Join(b);
        Assert.Equal("waiting", S(await Join(c), "state"));
    }

    /// <summary>경희대 메일 인증까지 마친 실제 학생 계정 (체험 계정이 아님)</summary>
    async Task<string> RealStudent(string nickname)
    {
        AccountStore accounts = server.Services.GetRequiredService<AccountStore>();
        var account = accounts.FindOrCreate("kakao", "kakao-" + Guid.NewGuid().ToString("N"));
        accounts.SetNickname(account.Id, nickname);
        accounts.SetGender(account.Id, "female");
        accounts.SaveCode(account.Id, $"{Guid.NewGuid():N}"[..12] + "@khu.ac.kr", "123456");
        Assert.Equal(AccountStore.VerifyResult.Ok, accounts.VerifyCode(account.Id, "123456"));
        Assert.True(accounts.Find(account.Id)!.Ready);
        return accounts.CreateSession(account.Id);
    }

    /// <summary>MatchDemoTests 가 부른다: 가상 상대가 켜져 있어도 실제 학생은 실제 학생끼리만</summary>
    public async Task DemoSkipsRealStudents()
    {
        string a = await RealStudent("학생A"), b = await RealStudent("학생B");
        Assert.Equal("waiting", S(await Join(a), "state"));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("waiting", S(await State(a), "state"));   // 체험메이트가 붙지 않음
        JsonElement p = await Join(b);
        Assert.Equal("proposed", S(p, "state"));
        Assert.Equal("학생A", S(p.GetProperty("partner"), "nickname"));
    }

    /// <summary>MatchDemoTests 가 부른다 (Match:DemoPartner=true 서버, 체험 계정)</summary>
    public async Task DemoFlow()
    {
        string a = await User("민지", prefs: new { tastes = new[] { "photo" } });
        Assert.Equal("waiting", S(await Join(a), "state"));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("waiting", S(await State(a), "state"));
        clock.Advance(TimeSpan.FromSeconds(1));
        JsonElement p = await State(a);
        Assert.Equal("proposed", S(p, "state"));
        Assert.Equal(MatchRules.DemoNickname, S(p.GetProperty("partner"), "nickname"));

        string id = S(p, "matchId");
        JsonElement m = await Send(HttpMethod.Post, $"/api/match/{id}/accept", a, null, HttpStatusCode.OK);
        Assert.Equal("meeting", S(m, "state"));   // 가상 상대는 이미 수락
        Assert.Equal(MatchRules.DemoAppearance, S(m.GetProperty("partner"), "appearance"));
        Assert.True(m.GetProperty("arrived").GetProperty("partner").GetBoolean());
        JsonElement w = await Send(HttpMethod.Post, $"/api/match/{id}/arrived", a, new { buildingId = S(m.GetProperty("meetSpot"), "buildingId") }, HttpStatusCode.OK);
        Assert.Equal("walking", S(w, "state"));
    }

    [Fact]
    public async Task 가상_상대는_설정이_없으면_나오지_않는다()
    {
        string a = await User("가");
        await Join(a);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("waiting", S(await State(a), "state"));
    }

    /// <summary>MatchReasonAiTests 가 부른다: 취향이 겹치는 두 사람을 제안 상태로 만들고 partner 를 돌려준다</summary>
    public async Task<JsonElement> ProposeWithPrefs()
    {
        string a = await User("민지", prefs: new { tastes = new[] { "photo" }, walkTimes = new[] { "evening" } });
        string b = await User("서연", prefs: new { tastes = new[] { "photo" }, walkTimes = new[] { "evening" } });
        await Join(a);
        return (await Join(b)).GetProperty("partner");
    }

    /// <summary>MatchReasonAiTests 가 부른다: 멀리(천문대) · 가까이(중앙도서관) 두 사람이 기다릴 때 학생회관의 내가 들어가면 누구와 짝이 되는지</summary>
    public async Task<JsonElement> JoinWithTwoCandidates()
    {
        string far = await User("멀리", prefs: new { tastes = new[] { "photo", "quiet" }, walkTimes = new[] { "evening" } });
        string near = await User("가까이", prefs: new { tastes = new[] { "food" } });
        string me = await User("나", prefs: new { tastes = new[] { "photo", "quiet" }, walkTimes = new[] { "evening" } });
        await TwoWaiting(far, "way-474521125", near, Library);
        return (await Join(me, building: StudentHall)).GetProperty("partner");
    }

    string UserIdOf(string token) => server.Services.GetRequiredService<AccountStore>().FindBySession(token)!.Id;

    [Fact]
    public async Task 탈퇴하면_진행_중인_매칭이_끝나고_기록이_지워진다()
    {
        (string a, string b, string id, _) = await Meet();
        await Send(HttpMethod.Delete, "/api/me", a, null, HttpStatusCode.NoContent);
        Assert.Equal("cancelled", S(await State(b), "endReason"));
        Assert.Null(server.Services.GetRequiredService<MatchStore>().FindRecord(id));
    }

    static string DataPath(string file)
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "KucaMemoServer", "Data", file)))
            dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "KucaMemoServer", "Data", file);
    }
}

/// <summary>시연용 가상 상대 (Match:DemoPartner=true)</summary>
public class MatchDemoTests : IDisposable
{
    readonly MatchApiTests t = new(demo: true);
    public void Dispose() => t.Dispose();

    [Fact]
    public Task 대기_5초_뒤_체험메이트가_수락하고_바로_도착한다() => t.DemoFlow();

    [Fact]
    public Task 실제_학생에게는_가상_상대가_붙지_않는다() => t.DemoSkipsRealStudents();
}

/// <summary>AI 매칭 이유 (가짜 AI 로)</summary>
public class MatchReasonAiTests : IDisposable
{
    sealed class FakeAi : IRouteAi
    {
        public bool IsConfigured => true;
        public List<string> Prompts { get; } = [];
        /// <summary>짝 고르기 요청에 줄 답 (null 이면 엉뚱한 답)</summary>
        public string? PickAnswer;
        public Task<string?> CompleteAsync(string system, string user, CancellationToken ct)
        {
            lock (Prompts) Prompts.Add(system + "\n" + user);
            // 매칭 이유 · 짝 고르기에만 답하고, 나머지(코스 · 스몰토크)는 형식이 틀려 규칙으로 대신한다
            if (system.Contains("왜 둘을 연결했는지"))
                return Task.FromResult<string?>("{\"reason\":\"저녁 산책과 사진을 함께 즐길 두 분이에요\"}");
            if (system.Contains("가장 잘 맞을 한 명"))
                return Task.FromResult(PickAnswer ?? "잘 모르겠어요");
            return Task.FromResult<string?>("모름");
        }
    }

    readonly FakeAi ai = new();
    readonly MatchApiTests t;

    public MatchReasonAiTests() => t = new MatchApiTests(demo: false, ai);
    public void Dispose() => t.Dispose();

    [Fact]
    public async Task AI_가_쓴_이유를_보여_주고_닉네임은_AI_에게_보내지_않는다()
    {
        JsonElement p = await t.ProposeWithPrefs();
        Assert.Equal("저녁 산책과 사진을 함께 즐길 두 분이에요", p.GetProperty("whyMatched").GetString());
        Assert.Equal("ai", p.GetProperty("whyMatchedSource").GetString());
        string prompt = string.Join("\n", ai.Prompts.Where(x => x.Contains("왜 둘을 연결했는지")));
        Assert.Contains("사진 찍기", prompt);
        Assert.DoesNotContain("민지", prompt);
        Assert.DoesNotContain("서연", prompt);
        Assert.DoesNotContain("female", prompt);
    }

    [Fact]
    public async Task 후보가_둘_이상이면_AI_가_고른_사람과_AI_이유로_짝이_된다()
    {
        // 규칙 점수(거리 − 공통 취향 × 100m)로는 취향이 겹치는 "멀리"가 A(1등)지만, AI 가 B("가까이")를 고르면 그대로 따른다
        ai.PickAnswer = "{\"pick\":\"B\",\"reason\":\"바로 가까이 있어 금방 만날 수 있는 메이트예요\"}";
        JsonElement p = await t.JoinWithTwoCandidates();
        Assert.Equal("가까이", p.GetProperty("nickname").GetString());
        Assert.Equal("바로 가까이 있어 금방 만날 수 있는 메이트예요", p.GetProperty("whyMatched").GetString());
        Assert.Equal("ai", p.GetProperty("whyMatchedSource").GetString());

        string prompt = ai.Prompts.Single(x => x.Contains("가장 잘 맞을 한 명"));
        Assert.Contains("A |", prompt);
        Assert.Contains("B |", prompt);
        Assert.DoesNotContain("멀리", prompt);      // 닉네임은 AI 에게 보내지 않는다
        Assert.DoesNotContain("가까이", prompt);
    }

    [Theory]
    [InlineData(null)]                                      // 엉뚱한 답
    [InlineData("{\"pick\":\"Z\",\"reason\":\"없는 후보\"}")]   // 후보에 없는 Label
    public async Task AI_가_못_고르면_규칙_점수대로_짝이_된다(string? answer)
    {
        ai.PickAnswer = answer;
        JsonElement p = await t.JoinWithTwoCandidates();
        Assert.Equal("멀리", p.GetProperty("nickname").GetString());   // 거리 403m − 공통 3개 × 100m = 103 < 가까이 121
    }

    [Fact]
    public void AI_짝_고르기_답은_후보_Label_일_때만_쓴다()
    {
        var labels = new HashSet<string> { "A", "B" };
        Assert.Equal(("B", "좋아요"), MatchExplainer.ParsePick("{\"pick\":\"B\",\"reason\":\"좋아요\"}", labels));
        Assert.Equal(("A", (string?)null), MatchExplainer.ParsePick("{\"pick\":\"A\",\"reason\":\"http://x\"}", labels));
        Assert.Equal(((string?)null, (string?)null), MatchExplainer.ParsePick("{\"pick\":\"C\"}", labels));
        Assert.Equal(((string?)null, (string?)null), MatchExplainer.ParsePick("모르겠어요", labels));
    }
}
