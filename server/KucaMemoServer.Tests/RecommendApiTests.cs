using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KucaMemoServer.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KucaMemoServer.Tests;

/// <summary>산책 추천 API (docs/api.md 의 "산책 추천") 검증. 실제 AI 는 부르지 않는다.</summary>
public class RecommendApiTests : IDisposable
{
    // 앱의 경희스팟 일부 (Data/buildings.json 에 있음)
    static readonly string[] Spots = ["way-473963422", "way-474085534", "way-474085535", "way-455725718", "way-474531805", "way-455726113"];
    const double Lat = 37.2440, Lon = 127.0800;

    /// <summary>정해 둔 답을 돌려주는 가짜 AI</summary>
    sealed class FakeAi(string? answer) : IRouteAi
    {
        public bool IsConfigured => true;
        public string? LastPrompt { get; private set; }
        public Task<string?> CompleteAsync(string system, string user, CancellationToken ct)
        {
            LastPrompt = user;
            return Task.FromResult(answer);
        }
    }

    sealed class FakeWeather(string? condition) : ICampusWeather
    {
        public Task<string?> NowAsync(CancellationToken ct) => Task.FromResult(condition);
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>기본 시각: 가을 밤 (2026-11-02 21:00 한국 시각)</summary>
    static readonly DateTimeOffset AutumnNight = new(2026, 11, 2, 21, 0, 0, TimeSpan.FromHours(9));

    readonly List<IDisposable> owned = [];

    WebApplicationFactory<Program> Server(IRouteAi? ai = null, string? weather = null, DateTimeOffset? now = null)
    {
        var server = new TestServer();
        owned.Add(server);
        var configured = server.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            if (ai != null)
                s.AddSingleton(ai);
            s.AddSingleton<ICampusWeather>(new FakeWeather(weather));
            s.AddSingleton<TimeProvider>(new FixedClock(now ?? AutumnNight));
        }));
        owned.Add(configured);
        return configured;
    }

    HttpClient Client(IRouteAi? ai = null, string? weather = null, DateTimeOffset? now = null) =>
        Server(ai, weather, now).CreateClient();

    public void Dispose()
    {
        foreach (IDisposable d in owned) d.Dispose();
    }

    static object Body(int minutes = 30, string[]? tastes = null, string[]? visited = null, double lat = Lat) => new
    {
        minutes, tastes = tastes ?? ["unvisited"], lat, lon = Lon,
        visitedBuildingIds = visited ?? Array.Empty<string>(), candidateBuildingIds = Spots,
    };

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task 키가_없으면_규칙으로_후보_안에서_추천한다()
    {
        JsonElement r = await Ok(await Client().PostAsJsonAsync("/api/recommendations", Body(30)));
        Assert.Equal("fallback", r.GetProperty("source").GetString());
        JsonElement[] stops = r.GetProperty("stops").EnumerateArray().ToArray();
        Assert.InRange(stops.Length, 1, RecommendRules.MaxStops(30));
        foreach (JsonElement s in stops)
        {
            Assert.Contains(s.GetProperty("buildingId").GetString(), Spots);
            Assert.True(s.GetProperty("walkMinutes").GetInt32() >= 1);
            Assert.NotEqual(0, s.GetProperty("lat").GetDouble());
            Assert.False(string.IsNullOrEmpty(s.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public async Task 안_가_본_곳을_고르면_가_본_곳보다_먼저_나온다()
    {
        string[] visited = Spots[..5];
        // 기본 위치(Lat, Lon)는 체육대학관 바로 앞이라 출발지로 빠진다 → 200 m 남쪽에서
        JsonElement r = await Ok(await Client().PostAsJsonAsync("/api/recommendations", Body(15, visited: visited, lat: Lat - 0.002)));
        Assert.Equal(Spots[5], r.GetProperty("stops")[0].GetProperty("buildingId").GetString());
    }

    [Theory]
    [InlineData(20, "unvisited")]
    [InlineData(30, "shopping")]
    public async Task 잘못된_값은_400(int minutes, string taste)
    {
        var res = await Client().PostAsJsonAsync("/api/recommendations", Body(minutes, [taste]));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AI_답을_순서대로_쓰고_후보에_없는_곳은_버린다()
    {
        var ai = new FakeAi("""
            코스를 짰어요!
            ```json
            {"title":"노을 산책","stops":[
              {"buildingId":"way-474531805","reason":"노을이 예뻐요"},
              {"buildingId":"way-999","reason":"없는 곳"},
              {"buildingId":"way-474085534","reason":"조용해요"}]}
            ```
            """);
        // 60분: 노천극장 → 중앙도서관은 실제 길로 2 km 쯤이라 30분(걷기 24분)엔 다 못 든다 (Data/walk_meters.json)
        JsonElement r = await Ok(await Client(ai).PostAsJsonAsync("/api/recommendations", Body(60, ["photo"])));
        Assert.Equal("ai", r.GetProperty("source").GetString());
        Assert.Equal("노을 산책", r.GetProperty("title").GetString());
        string[] ids = r.GetProperty("stops").EnumerateArray().Select(s => s.GetProperty("buildingId").GetString()!).ToArray();
        Assert.Equal(["way-474531805", "way-474085534"], ids);
        Assert.Equal("노을이 예뻐요", r.GetProperty("stops")[0].GetProperty("reason").GetString());
        Assert.Contains("사진 찍기 좋은 명소", ai.LastPrompt);
        Assert.Contains("중앙도서관", ai.LastPrompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("미안해요, 지금은 답할 수 없어요")]
    [InlineData("{\"stops\":[{\"buildingId\":\"way-999\"}]}")]
    public async Task AI_가_실패하거나_엉뚱하면_규칙_추천(string? answer)
    {
        JsonElement r = await Ok(await Client(new FakeAi(answer)).PostAsJsonAsync("/api/recommendations", Body(30)));
        Assert.Equal("fallback", r.GetProperty("source").GetString());
        Assert.True(r.GetProperty("stops").GetArrayLength() > 0);
    }

    // ───── 추천 v2 ─────

    const string Library = "way-474085534", Amphitheater = "way-474531805", Observatory = "way-474521125", Sports = "way-455726113";

    /// <summary>건물에서 약 110 m 남쪽 (건물 바로 앞은 출발지로 보고 들를 곳에서 뺀다)</summary>
    static async Task<(double Lat, double Lon)> Beside(HttpClient client, string id)
    {
        var at = await Midpoint(client, id, id);
        return (at.Lat - 0.001, at.Lon);
    }

    /// <summary>두 건물의 가운데 (어느 쪽도 가깝지 않게)</summary>
    static async Task<(double Lat, double Lon)> Midpoint(HttpClient client, string a, string b)
    {
        JsonElement x = await client.GetFromJsonAsync<JsonElement>($"/api/buildings/{a}");
        JsonElement y = await client.GetFromJsonAsync<JsonElement>($"/api/buildings/{b}");
        return ((x.GetProperty("lat").GetDouble() + y.GetProperty("lat").GetDouble()) / 2,
                (x.GetProperty("lon").GetDouble() + y.GetProperty("lon").GetDouble()) / 2);
    }

    static object Between((double Lat, double Lon) at, string[] candidates, int minutes = 60, string? pace = null,
                          bool afterMeal = false, string[]? tastes = null) => new
    {
        minutes, tastes = tastes ?? Array.Empty<string>(), lat = at.Lat, lon = at.Lon,
        visitedBuildingIds = Array.Empty<string>(), candidateBuildingIds = candidates, pace, afterMeal,
    };

    [Fact]
    public async Task 응답에_추천ID_상황_거리_합계가_있고_기록된다()
    {
        WebApplicationFactory<Program> server = Server(weather: "cloudy");
        JsonElement r = await Ok(await server.CreateClient().PostAsJsonAsync("/api/recommendations", Body(30)));

        string id = r.GetProperty("id").GetString()!;
        Assert.StartsWith("rec_", id);
        JsonElement context = r.GetProperty("context");
        Assert.Equal("night", context.GetProperty("timeOfDay").GetString());
        Assert.Equal("autumn", context.GetProperty("season").GetString());
        Assert.Equal("cloudy", context.GetProperty("weather").GetString());
        JsonElement[] stops = r.GetProperty("stops").EnumerateArray().ToArray();
        Assert.All(stops, s => Assert.True(s.GetProperty("distanceM").GetInt32() > 0));
        Assert.Equal(stops.Sum(s => s.GetProperty("distanceM").GetInt32()), r.GetProperty("totalDistanceM").GetInt32());
        Assert.Equal(stops.Sum(s => s.GetProperty("walkMinutes").GetInt32()), r.GetProperty("totalMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("healthNote").ValueKind);
        Assert.False(r.TryGetProperty("kcal", out _)); // 칼로리는 내지 않는다

        RecommendationStore.Saved? saved = server.Services.GetRequiredService<RecommendationStore>().Find(id);
        Assert.NotNull(saved);
        Assert.Equal("fallback", saved.Source);
        Assert.Equal(r.GetProperty("totalDistanceM").GetInt32(), saved.PlannedDistanceM);
        Assert.Equal(stops.Select(s => s.GetProperty("buildingId").GetString()!), saved.StopIds);
    }

    [Fact]
    public async Task 후보를_안_보내면_경희스팟_안에서_고른다()
    {
        JsonElement r = await Ok(await Client().PostAsJsonAsync("/api/recommendations",
            new { minutes = 60, lat = Lat, lon = Lon }));
        string[] spotIds = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SpotsJsonPath())).RootElement
            .EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray();
        Assert.NotEmpty(r.GetProperty("stops").EnumerateArray());
        Assert.All(r.GetProperty("stops").EnumerateArray(), s => Assert.Contains(s.GetProperty("buildingId").GetString(), spotIds));
    }

    static string SpotsJsonPath()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "KucaMemoServer", "Data", "spots.json")))
            dir = Path.GetDirectoryName(dir)!;
        return Path.Combine(dir, "KucaMemoServer", "Data", "spots.json");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public async Task 걷는_시간이_고른_시간을_넘지_않는다(int minutes)
    {
        JsonElement r = await Ok(await Client().PostAsJsonAsync("/api/recommendations",
            new { minutes, tastes = new[] { "unvisited" }, lat = Lat, lon = Lon }));
        Assert.InRange(r.GetProperty("totalMinutes").GetInt32(), 1, (int)(minutes * RecommendRules.WalkTimeRatio));
        Assert.InRange(r.GetProperty("stops").GetArrayLength(), 1, RecommendRules.MaxStops(minutes));
    }

    [Theory]
    [InlineData(15, 2)]
    [InlineData(30, 4)]
    [InlineData(60, 6)]
    public void 들를_곳은_15분_2곳_30분_4곳_60분_6곳까지(int minutes, int stops)
    {
        Assert.Equal(stops, RecommendRules.MaxStops(minutes));
    }

    [Fact]
    public async Task 시간이_넉넉하면_30분_코스는_3곳보다_많이_들른다()
    {
        // 캠퍼스 한가운데(중앙도서관 근처)에서 30분: 걷는 예산 24분이면 가까운 스팟을 4곳까지 잇는다
        JsonElement r = await Ok(await Client().PostAsJsonAsync("/api/recommendations",
            new { minutes = 30, lat = 37.2421, lon = 127.0796 }));
        Assert.Equal(RecommendRules.MaxStops(30), r.GetProperty("stops").GetArrayLength());
        Assert.True(r.GetProperty("totalMinutes").GetInt32() <= 30 * RecommendRules.WalkTimeRatio);
    }

    [Fact]
    public async Task 지금_계절과_시간대에_맞는_곳이_먼저다()
    {
        string[] both = [Observatory, Sports];
        // 천문대: 가을·겨울 밤 / 체육대학관: 봄·여름·가을 아침·낮
        HttpClient night = Client(now: AutumnNight);
        var at = await Midpoint(night, Observatory, Sports);
        JsonElement r1 = await Ok(await night.PostAsJsonAsync("/api/recommendations", Between(at, both)));
        Assert.Equal(Observatory, r1.GetProperty("stops")[0].GetProperty("buildingId").GetString());

        var springMorning = new DateTimeOffset(2026, 5, 6, 9, 0, 0, TimeSpan.FromHours(9));
        JsonElement r2 = await Ok(await Client(now: springMorning).PostAsJsonAsync("/api/recommendations", Between(at, both)));
        Assert.Equal(Sports, r2.GetProperty("stops")[0].GetProperty("buildingId").GetString());
        Assert.Equal("spring", r2.GetProperty("context").GetProperty("season").GetString());
        Assert.Equal("morning", r2.GetProperty("context").GetProperty("timeOfDay").GetString());
    }

    [Fact]
    public async Task 비가_오면_실내_스팟이_먼저고_이유도_날씨다()
    {
        // 가을 저녁엔 노천극장(실외)과 중앙도서관(실내) 모두 계절·시간이 맞는다 → 비가 오면 실내가 앞선다
        var autumnEvening = new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.FromHours(9));
        HttpClient client = Client(weather: "rain", now: autumnEvening);
        var at = await Midpoint(client, Library, Amphitheater);
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/recommendations", Between(at, [Amphitheater, Library])));
        JsonElement first = r.GetProperty("stops")[0];
        Assert.Equal(Library, first.GetProperty("buildingId").GetString());
        Assert.Equal("비가 와도 실내라 들르기 좋아요", first.GetProperty("reason").GetString());
        Assert.Equal("rain", r.GetProperty("context").GetProperty("weather").GetString());
    }

    [Fact]
    public async Task 규칙_이유는_소개글에서_가져오고_확인필요_표시는_뺀다()
    {
        HttpClient client = Client();
        var at = await Beside(client, Observatory);
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/recommendations", Between(at, [Observatory])));
        string reason = r.GetProperty("stops")[0].GetProperty("reason").GetString()!;
        Assert.Equal("가는 길에 들르기 좋아요", reason);   // 천문대 소개의 "좋" 문장은 "어두우니" 주의가 섞여 있어 쓰지 않음
        Assert.DoesNotContain("확인 필요", reason);
    }

    [Theory]
    [InlineData("way-474085534", "공강에 잠깐 들러 쉬거나 시험 기간 밤 산책 끝에 들르기 좋아요")]  // 중앙도서관
    public async Task 규칙_이유는_추천_문장을_고르고_주의_문장은_쓰지_않는다(string spot, string expected)
    {
        HttpClient client = Client();
        var at = await Beside(client, spot);
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/recommendations", Between(at, [spot])));
        Assert.Equal(expected, r.GetProperty("stops")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task 출입_제한_스팟은_후보로_보내도_추천하지_않는다()
    {
        const string Dorm = "relation-8269760";   // 우정원 (spots.json restricted)
        HttpClient client = Client();
        var at = await Midpoint(client, Dorm, Library);
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/recommendations", Between(at, [Dorm, Library])));
        string[] ids = r.GetProperty("stops").EnumerateArray().Select(s => s.GetProperty("buildingId").GetString()!).ToArray();
        Assert.Contains(Library, ids);
        Assert.DoesNotContain(Dorm, ids);
    }

    [Fact]
    public async Task AI_이유가_소개글과_상관없으면_규칙_이유로_바꾼다()
    {
        var ai = new FakeAi("""
            {"title":"가을 산책","stops":[
              {"buildingId":"way-474531805","reason":"저녁 노을에 계단에 앉아 쉬어 보세요"},
              {"buildingId":"way-474085534","reason":"맛있는 빵집이 숨어 있어요"}]}
            """);
        HttpClient client = Client(ai);
        var at = await Midpoint(client, Library, Amphitheater);
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/recommendations", Between(at, [Amphitheater, Library])));
        Assert.Equal("ai", r.GetProperty("source").GetString());
        JsonElement[] stops = r.GetProperty("stops").EnumerateArray().ToArray();
        Assert.Equal("저녁 노을에 계단에 앉아 쉬어 보세요", stops[0].GetProperty("reason").GetString());
        Assert.DoesNotContain("빵집", stops[1].GetProperty("reason").GetString());
        Assert.Contains("가을", ai.LastPrompt);   // AI 에게 지금 상황과 소개글을 준다
        Assert.Contains("열람실", ai.LastPrompt);
        Assert.DoesNotContain("확인 필요", ai.LastPrompt);
    }

    const string ArtDesign = "way-474085536", Ceramics = "way-474521123";   // 예술디자인대학 · 도예관 (누구나 들어가는 곳 아님)

    [Fact]
    public async Task AI_이유가_건물_안에_들어가라고_하면_규칙_이유로_바꾸고_말끝은_요로()
    {
        var ai = new FakeAi("""
            {"title":"가을 산책","stops":[
              {"buildingId":"way-474085536","reason":"2층 전시실에서 학생 작품 사진을 찍기 좋습니다."},
              {"buildingId":"way-474085534","reason":"공강에 열람실에서 잠깐 쉬기 좋습니다."}]}
            """);
        HttpClient client = Client(ai);
        var at = await Midpoint(client, ArtDesign, Library);
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/recommendations", Between(at, [ArtDesign, Library])));
        JsonElement[] stops = r.GetProperty("stops").EnumerateArray().ToArray();
        string art = stops.First(x => x.GetProperty("buildingId").GetString() == ArtDesign).GetProperty("reason").GetString()!;
        string lib = stops.First(x => x.GetProperty("buildingId").GetString() == Library).GetProperty("reason").GetString()!;
        Assert.DoesNotContain("전시실", art);   // 예디대는 앞만 지나가는 곳
        Assert.DoesNotContain("층", art);
        Assert.Equal("공강에 열람실에서 잠깐 쉬기 좋아요", lib);   // 도서관은 누구나 들어가는 곳, 말끝만 ~요
        Assert.Contains("앞만 지나감", ai.LastPrompt);
        Assert.Contains("누구나 들어감", ai.LastPrompt);
    }

    [Theory]
    [InlineData(ArtDesign)]
    [InlineData(Ceramics)]
    public async Task 규칙_이유도_건물_안_이야기는_쓰지_않는다(string spot)
    {
        HttpClient client = Client();
        var at = await Beside(client, spot);
        JsonElement r = await Ok(await client.PostAsJsonAsync("/api/recommendations", Between(at, [spot])));
        string reason = r.GetProperty("stops")[0].GetProperty("reason").GetString()!;
        foreach (string word in new[] { "전시실", "작업", "공방", "층", "안에서" })
            Assert.DoesNotContain(word, reason);
        Assert.EndsWith("요", reason);
    }

    [Theory]
    [InlineData("조용히 걷기 좋습니다.", "조용히 걷기 좋아요")]
    [InlineData("벤치가 있습니다", "벤치가 있어요")]
    [InlineData("쉬어 가기 좋다", "쉬어 가기 좋아요")]
    [InlineData("노을이 예뻐요", "노을이 예뻐요")]
    public void 말끝을_요로_바꾼다(string from, string expected) =>
        Assert.Equal(expected, RouteRecommender.Polite(from));

    [Fact]
    public async Task AI_가_먼_곳을_이어도_시간_안에서_자른다()
    {
        var ai = new FakeAi("""
            {"title":"먼 길","stops":[
              {"buildingId":"way-474521125","reason":"별"},{"buildingId":"way-473963422","reason":"정문"},
              {"buildingId":"relation-8269760","reason":"기숙사"},{"buildingId":"way-455726113","reason":"체육"}]}
            """);
        JsonElement r = await Ok(await Client(ai).PostAsJsonAsync("/api/recommendations",
            new { minutes = 15, lat = Lat, lon = Lon, candidateBuildingIds = new[] { Observatory, "way-473963422", "relation-8269760", Sports } }));
        Assert.InRange(r.GetProperty("stops").GetArrayLength(), 1, 2);
        if (r.GetProperty("stops").GetArrayLength() > 1)
            Assert.True(r.GetProperty("totalMinutes").GetInt32() <= 15 * RecommendRules.WalkTimeRatio);
    }

    [Fact]
    public async Task 식후_걷기는_안내_문구와_제목이_붙는다()
    {
        JsonElement r = await Ok(await Client().PostAsJsonAsync("/api/recommendations",
            new { minutes = 15, lat = Lat, lon = Lon, afterMeal = true }));
        Assert.StartsWith("식후", r.GetProperty("title").GetString());
        Assert.EndsWith("일반 건강 정보이며 의료 조언이 아닙니다.", r.GetProperty("healthNote").GetString());
    }

    [Fact]
    public async Task 느리게_걸으면_같은_거리도_더_오래_걸린다_계정_취향도_쓴다()
    {
        HttpClient client = Client();
        var gate = await Midpoint(client, "way-473963422", "way-473963422");
        async Task<int> MinutesTo(string? pace, string? token = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/recommendations")
            {
                Content = JsonContent.Create(Between(gate, [Observatory], pace: pace)),
            };
            if (token != null)
                req.Headers.Authorization = new("Bearer", token);
            JsonElement r = await Ok(await client.SendAsync(req));
            return r.GetProperty("stops")[0].GetProperty("walkMinutes").GetInt32();
        }

        int slow = await MinutesTo("slow"), normal = await MinutesTo(null), fast = await MinutesTo("fast");
        Assert.True(slow > normal && normal > fast, $"slow {slow} · normal {normal} · fast {fast}");

        // 로그인한 계정 취향이 느긋하면 pace 를 안 보내도 느리게 계산
        JsonElement login = await Ok(await client.PostAsync("/api/auth/guest", null));
        string token = login.GetProperty("token").GetString()!;
        var prefs = new HttpRequestMessage(HttpMethod.Put, "/api/me/preferences") { Content = JsonContent.Create(new { pace = "slow" }) };
        prefs.Headers.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(prefs)).StatusCode);
        Assert.Equal(slow, await MinutesTo(null, token));
    }

    [Fact]
    public async Task v2_취향_코드도_받고_잘못된_속도는_400()
    {
        HttpClient client = Client();
        await Ok(await client.PostAsJsonAsync("/api/recommendations", Body(30, ["partner", "exercise"])));
        var res = await client.PostAsJsonAsync("/api/recommendations",
            new { minutes = 30, lat = Lat, lon = Lon, pace = "run" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
