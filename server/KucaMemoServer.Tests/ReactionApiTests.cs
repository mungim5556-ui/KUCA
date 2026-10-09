using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace KucaMemoServer.Tests;

/// <summary>좋아요·댓글 API (docs/api.md 의 like, comments) 검증</summary>
public class ReactionApiTests : IDisposable
{
    const string Engineering = "way-455725718";

    readonly TestServer server = new();
    readonly HttpClient client;

    public ReactionApiTests() => client = server.CreateClient();

    public void Dispose() => server.Dispose();

    async Task<string> CreateMemoAsync(string deviceId = "writer")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("원준"), "author" },
            { new StringContent("공학관 자판기 고장"), "text" },
            { new StringContent(deviceId), "deviceId" },
        };
        var res = await client.PostAsync($"/api/buildings/{Engineering}/memos", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? deviceId, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        if (deviceId != null)
            req.Headers.Add("X-Device-Id", deviceId);
        return client.SendAsync(req);
    }

    async Task<JsonElement> LikeAsync(string memoId, string deviceId, bool like = true)
    {
        var res = await SendAsync(like ? HttpMethod.Put : HttpMethod.Delete, $"/api/memos/{memoId}/like", deviceId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    async Task<JsonElement> GetMemoAsync(string memoId, string? deviceId = null)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/memos/{memoId}", deviceId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    async Task<JsonElement> CommentAsync(string memoId, string text, string deviceId = "commenter", string author = "민지")
    {
        var res = await client.PostAsync($"/api/memos/{memoId}/comments", Json(new { author, text, deviceId }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    static async Task<string> ErrorOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    // ---------- 좋아요 ----------

    [Fact]
    public async Task 좋아요는_기기마다_한_번만_세고_여러_번_보내도_같다()
    {
        string memo = await CreateMemoAsync();

        var first = await LikeAsync(memo, "a");
        Assert.Equal(1, first.GetProperty("likeCount").GetInt32());
        Assert.True(first.GetProperty("likedByMe").GetBoolean());

        var again = await LikeAsync(memo, "a");
        Assert.Equal(1, again.GetProperty("likeCount").GetInt32());

        var other = await LikeAsync(memo, "b");
        Assert.Equal(2, other.GetProperty("likeCount").GetInt32());
    }

    [Fact]
    public async Task 좋아요_취소는_누른_적이_없어도_성공()
    {
        string memo = await CreateMemoAsync();
        await LikeAsync(memo, "a");
        await LikeAsync(memo, "b");

        var undo = await LikeAsync(memo, "a", like: false);
        Assert.Equal(1, undo.GetProperty("likeCount").GetInt32());
        Assert.False(undo.GetProperty("likedByMe").GetBoolean());

        var never = await LikeAsync(memo, "c", like: false);
        Assert.Equal(1, never.GetProperty("likeCount").GetInt32());
        Assert.False(never.GetProperty("likedByMe").GetBoolean());
    }

    [Fact]
    public async Task likedByMe_는_요청한_기기_기준이고_헤더가_없으면_false()
    {
        string memo = await CreateMemoAsync();
        await LikeAsync(memo, "a");

        Assert.True((await GetMemoAsync(memo, "a")).GetProperty("likedByMe").GetBoolean());
        Assert.False((await GetMemoAsync(memo, "b")).GetProperty("likedByMe").GetBoolean());
        var anonymous = await GetMemoAsync(memo);
        Assert.False(anonymous.GetProperty("likedByMe").GetBoolean());
        Assert.Equal(1, anonymous.GetProperty("likeCount").GetInt32());

        // 목록에도 같은 값
        var res = await SendAsync(HttpMethod.Get, $"/api/buildings/{Engineering}/memos", "a");
        var list = await res.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.True(list!.Single().GetProperty("likedByMe").GetBoolean());
        Assert.Equal(1, list!.Single().GetProperty("likeCount").GetInt32());
    }

    [Fact]
    public async Task 좋아요에_기기_ID가_없거나_너무_길면_400()
    {
        string memo = await CreateMemoAsync();

        var none = await SendAsync(HttpMethod.Put, $"/api/memos/{memo}/like", null);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("X-Device-Id", await ErrorOf(none));

        var tooLong = await SendAsync(HttpMethod.Put, $"/api/memos/{memo}/like", new string('d', 65));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var undo = await SendAsync(HttpMethod.Delete, $"/api/memos/{memo}/like", null);
        Assert.Equal(HttpStatusCode.BadRequest, undo.StatusCode);
    }

    [Fact]
    public async Task 없는_메모에_좋아요는_404()
    {
        var res = await SendAsync(HttpMethod.Put, $"/api/memos/{Guid.NewGuid()}/like", "a");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---------- 댓글 ----------

    [Fact]
    public async Task 댓글을_쓰면_201과_명세의_Comment_모양을_돌려준다()
    {
        string memo = await CreateMemoAsync();

        var res = await client.PostAsync($"/api/memos/{memo}/comments",
            Json(new { author = "  민지 ", text = " 저도 봤어요 ", deviceId = "c1" }));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var comment = await res.Content.ReadFromJsonAsync<JsonElement>();
        string id = comment.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal($"/api/comments/{id}", res.Headers.Location?.ToString());
        Assert.Equal(memo, comment.GetProperty("memoId").GetString());
        Assert.Equal("민지", comment.GetProperty("author").GetString());
        Assert.Equal("저도 봤어요", comment.GetProperty("text").GetString());
        Assert.EndsWith("Z", comment.GetProperty("createdAt").GetString());
        Assert.False(comment.TryGetProperty("deviceId", out _));
        Assert.Equal(5, comment.EnumerateObject().Count());
    }

    [Fact]
    public async Task 댓글_목록은_오래된_순이고_메모의_commentCount_가_늘어난다()
    {
        string memo = await CreateMemoAsync();
        string other = await CreateMemoAsync();
        await CommentAsync(memo, "첫째");
        await CommentAsync(memo, "둘째");
        await CommentAsync(other, "다른 메모");

        var list = await client.GetFromJsonAsync<JsonElement[]>($"/api/memos/{memo}/comments");
        Assert.Equal(new[] { "첫째", "둘째" }, list!.Select(c => c.GetProperty("text").GetString()));
        Assert.All(list!, c => Assert.False(c.TryGetProperty("deviceId", out _)));

        Assert.Equal(2, (await GetMemoAsync(memo)).GetProperty("commentCount").GetInt32());
        Assert.Equal(1, (await GetMemoAsync(other)).GetProperty("commentCount").GetInt32());
    }

    [Fact]
    public async Task 댓글이_없으면_빈_배열()
    {
        string memo = await CreateMemoAsync();
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>($"/api/memos/{memo}/comments"))!);
    }

    [Fact]
    public async Task limit_와_after_로_다음_댓글을_불러온다()
    {
        string memo = await CreateMemoAsync();
        for (int i = 1; i <= 4; i++)
        {
            await CommentAsync(memo, $"댓글{i}");
            await Task.Delay(5); // 작성 시각이 겹치지 않게
        }

        var page1 = await client.GetFromJsonAsync<JsonElement[]>($"/api/memos/{memo}/comments?limit=2");
        Assert.Equal(new[] { "댓글1", "댓글2" }, page1!.Select(c => c.GetProperty("text").GetString()));

        string last = page1![^1].GetProperty("createdAt").GetString()!;
        var page2 = await client.GetFromJsonAsync<JsonElement[]>(
            $"/api/memos/{memo}/comments?limit=2&after={Uri.EscapeDataString(last)}");
        Assert.Equal(new[] { "댓글3", "댓글4" }, page2!.Select(c => c.GetProperty("text").GetString()));
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("500", 3)] // 100 으로 맞춰지지만 댓글이 3개뿐
    public async Task 댓글_limit_는_1에서_100_사이로_맞춘다(string limit, int expected)
    {
        string memo = await CreateMemoAsync();
        for (int i = 0; i < 3; i++) await CommentAsync(memo, $"댓글{i}");
        var list = await client.GetFromJsonAsync<JsonElement[]>($"/api/memos/{memo}/comments?limit={limit}");
        Assert.Equal(expected, list!.Length);
    }

    [Fact]
    public async Task 잘못된_after_는_400()
    {
        string memo = await CreateMemoAsync();
        var res = await client.GetAsync($"/api/memos/{memo}/comments?after=어제");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("after", await ErrorOf(res));
    }

    [Theory]
    [InlineData(null, "내용", "d", "author")]
    [InlineData(" ", "내용", "d", "author")]
    [InlineData("가나다라마바사아자차카타파하가나다라마바사", "내용", "d", "author")] // 21자
    [InlineData("민지", null, "d", "text")]
    [InlineData("민지", "  ", "d", "text")]
    [InlineData("민지", "내용", null, "deviceId")]
    public async Task 규칙에_맞지_않는_댓글은_400과_필드_이름(string? author, string? text, string? deviceId, string field)
    {
        string memo = await CreateMemoAsync();
        var res = await client.PostAsync($"/api/memos/{memo}/comments", Json(new { author, text, deviceId }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(field, await ErrorOf(res));
    }

    [Fact]
    public async Task 댓글_글자_수_경계값()
    {
        string memo = await CreateMemoAsync();
        await CommentAsync(memo, new string('가', 200), deviceId: new string('d', 64), author: new string('나', 20));

        var tooLong = await client.PostAsync($"/api/memos/{memo}/comments",
            Json(new { author = "민지", text = new string('가', 201), deviceId = "d" }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task JSON_이_아니거나_깨진_JSON_은_400()
    {
        string memo = await CreateMemoAsync();

        var form = new MultipartFormDataContent { { new StringContent("민지"), "author" } };
        var notJson = await client.PostAsync($"/api/memos/{memo}/comments", form);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Contains("JSON", await ErrorOf(notJson));

        var broken = await client.PostAsync($"/api/memos/{memo}/comments",
            new StringContent("{\"author\": ", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("JSON", await ErrorOf(broken));
    }

    [Fact]
    public async Task 없는_메모의_댓글은_404()
    {
        string missing = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/memos/{missing}/comments")).StatusCode);
        var post = await client.PostAsync($"/api/memos/{missing}/comments", Json(new { author = "a", text = "b", deviceId = "c" }));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task 댓글은_쓴_기기만_지울_수_있다()
    {
        string memo = await CreateMemoAsync(deviceId: "memo-writer");
        string id = (await CommentAsync(memo, "지울 댓글", deviceId: "c1")).GetProperty("id").GetString()!;

        // 헤더 없음, 다른 기기, 메모 작성자 → 403
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/api/comments/{id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/api/comments/{id}", "c2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, $"/api/comments/{id}", "memo-writer")).StatusCode);

        var deleted = await SendAsync(HttpMethod.Delete, $"/api/comments/{id}", "c1");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, (await GetMemoAsync(memo)).GetProperty("commentCount").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/comments/{id}", "c1")).StatusCode);
    }

    [Fact]
    public async Task 메모를_지우면_좋아요와_댓글도_지워진다()
    {
        string memo = await CreateMemoAsync(deviceId: "writer");
        await LikeAsync(memo, "a");
        string commentId = (await CommentAsync(memo, "댓글")).GetProperty("id").GetString()!;

        var deleted = await SendAsync(HttpMethod.Delete, $"/api/memos/{memo}", "writer");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/memos/{memo}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/comments/{commentId}", "commenter")).StatusCode);
    }

    [Fact]
    public async Task 좋아요와_댓글은_서버를_다시_켜도_남는다()
    {
        string memo = await CreateMemoAsync();
        await LikeAsync(memo, "a");
        await CommentAsync(memo, "남아 있어야 함");

        using var second = server.WithWebHostBuilder(_ => { });
        var again = second.CreateClient();
        var m = await again.GetFromJsonAsync<JsonElement>($"/api/memos/{memo}");
        Assert.Equal(1, m.GetProperty("likeCount").GetInt32());
        Assert.Equal(1, m.GetProperty("commentCount").GetInt32());
        var comments = await again.GetFromJsonAsync<JsonElement[]>($"/api/memos/{memo}/comments");
        Assert.Equal("남아 있어야 함", comments!.Single().GetProperty("text").GetString());
    }
}
