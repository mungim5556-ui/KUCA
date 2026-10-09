using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace KucaMemoServer.Tests;

/// <summary>
/// 테스트마다 임시 폴더(DB 파일, wwwroot)를 쓰는 서버를 띄운다.
/// </summary>
public sealed class TestServer : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "kuca-test-" + Guid.NewGuid().ToString("N"));
    public string PhotosDir => Path.Combine(Root, "wwwroot", "photos");

    public TestServer() => Directory.CreateDirectory(Path.Combine(Root, "wwwroot"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Memos:DatabasePath", Path.Combine(Root, "memos.db"));
        builder.UseSetting(WebHostDefaults.WebRootKey, Path.Combine(Root, "wwwroot"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class MemoApiTests : IDisposable
{
    // docs/api.md 의 예시 건물 (Data/buildings.json 에 있음)
    const string Engineering = "way-455725718";
    const string Dorm = "relation-8269760";

    static readonly byte[] TinyJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9 };
    static readonly byte[] TinyPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

    readonly TestServer server = new();
    readonly HttpClient client;

    public MemoApiTests() => client = server.CreateClient();

    public void Dispose() => server.Dispose();

    static MultipartFormDataContent Form(string? author = "원준", string? text = "공학관 1층 자판기 고장났어요",
                                         string? deviceId = "device-1", byte[]? photo = null, string photoName = "photo.jpg")
    {
        var form = new MultipartFormDataContent();
        if (author != null) form.Add(new StringContent(author), "author");
        if (text != null) form.Add(new StringContent(text), "text");
        if (deviceId != null) form.Add(new StringContent(deviceId), "deviceId");
        if (photo != null)
        {
            var file = new ByteArrayContent(photo);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "photo", photoName);
        }
        return form;
    }

    async Task<JsonElement> CreateAsync(string buildingId = Engineering, MultipartFormDataContent? form = null)
    {
        var res = await client.PostAsync($"/api/buildings/{buildingId}/memos", form ?? Form());
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    static async Task<string> ErrorOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    [Fact]
    public async Task 메모를_쓰면_201과_명세의_Memo_모양을_돌려준다()
    {
        var res = await client.PostAsync($"/api/buildings/{Engineering}/memos", Form(author: "  원준  ", text: " 자판기 고장 "));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var memo = await res.Content.ReadFromJsonAsync<JsonElement>();
        string id = memo.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal($"/api/memos/{id}", res.Headers.Location?.ToString());
        Assert.Equal(Engineering, memo.GetProperty("buildingId").GetString());
        Assert.Equal("원준", memo.GetProperty("author").GetString());
        Assert.Equal("자판기 고장", memo.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, memo.GetProperty("photoUrl").ValueKind);
        Assert.EndsWith("Z", memo.GetProperty("createdAt").GetString());
        // deviceId 는 응답에 나가면 안 된다
        Assert.False(memo.TryGetProperty("deviceId", out _));
        // 새 메모는 좋아요·댓글 0
        Assert.Equal(0, memo.GetProperty("likeCount").GetInt32());
        Assert.Equal(0, memo.GetProperty("commentCount").GetInt32());
        Assert.False(memo.GetProperty("likedByMe").GetBoolean());
        Assert.Equal(9, memo.EnumerateObject().Count());
    }

    [Fact]
    public async Task 목록은_최신순이고_그_건물_메모만_온다()
    {
        await CreateAsync(form: Form(text: "첫째"));
        await CreateAsync(form: Form(text: "둘째"));
        await CreateAsync(Dorm, Form(text: "다른 건물"));

        var list = await client.GetFromJsonAsync<JsonElement[]>($"/api/buildings/{Engineering}/memos");

        Assert.Equal(new[] { "둘째", "첫째" }, list!.Select(m => m.GetProperty("text").GetString()));
        Assert.All(list!, m => Assert.False(m.TryGetProperty("deviceId", out _)));
    }

    [Fact]
    public async Task 메모가_없으면_빈_배열()
    {
        var list = await client.GetFromJsonAsync<JsonElement[]>($"/api/buildings/{Engineering}/memos");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task limit_와_before_로_다음_페이지를_불러온다()
    {
        for (int i = 1; i <= 5; i++)
        {
            await CreateAsync(form: Form(text: $"메모{i}"));
            await Task.Delay(5); // 작성 시각이 겹치지 않게
        }

        var page1 = await client.GetFromJsonAsync<JsonElement[]>($"/api/buildings/{Engineering}/memos?limit=2");
        Assert.Equal(new[] { "메모5", "메모4" }, page1!.Select(m => m.GetProperty("text").GetString()));

        string last = page1![^1].GetProperty("createdAt").GetString()!;
        var page2 = await client.GetFromJsonAsync<JsonElement[]>(
            $"/api/buildings/{Engineering}/memos?limit=2&before={Uri.EscapeDataString(last)}");
        Assert.Equal(new[] { "메모3", "메모2" }, page2!.Select(m => m.GetProperty("text").GetString()));
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("100", 3)] // 50 으로 맞춰지지만 메모가 3개뿐
    [InlineData("-5", 1)]
    public async Task limit_는_1에서_50_사이로_맞춘다(string limit, int expected)
    {
        for (int i = 0; i < 3; i++) await CreateAsync();
        var list = await client.GetFromJsonAsync<JsonElement[]>($"/api/buildings/{Engineering}/memos?limit={limit}");
        Assert.Equal(expected, list!.Length);
    }

    [Fact]
    public async Task 잘못된_before_는_400()
    {
        var res = await client.GetAsync($"/api/buildings/{Engineering}/memos?before=어제");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("before", await ErrorOf(res));
    }

    [Fact]
    public async Task 없는_건물은_404()
    {
        var list = await client.GetAsync("/api/buildings/way-1/memos");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Contains("건물", await ErrorOf(list));

        var create = await client.PostAsync("/api/buildings/way-1/memos", Form());
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    [Theory]
    [InlineData(null, "내용", "d", "author")]
    [InlineData("   ", "내용", "d", "author")]
    [InlineData("가나다라마바사아자차카타파하가나다라마바사", "내용", "d", "author")] // 21자
    [InlineData("원준", null, "d", "text")]
    [InlineData("원준", " ", "d", "text")]
    [InlineData("원준", "내용", null, "deviceId")]
    public async Task 규칙에_맞지_않는_값은_400과_필드_이름(string? author, string? text, string? deviceId, string field)
    {
        var res = await client.PostAsync($"/api/buildings/{Engineering}/memos", Form(author, text, deviceId));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(field, await ErrorOf(res));
    }

    [Fact]
    public async Task 글자_수_경계값()
    {
        await CreateAsync(form: Form(author: new string('가', 20), text: new string('나', 500), deviceId: new string('d', 64)));

        var tooLong = await client.PostAsync($"/api/buildings/{Engineering}/memos", Form(text: new string('나', 501)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var longDevice = await client.PostAsync($"/api/buildings/{Engineering}/memos", Form(deviceId: new string('d', 65)));
        Assert.Equal(HttpStatusCode.BadRequest, longDevice.StatusCode);
    }

    [Fact]
    public async Task multipart_가_아니면_400()
    {
        var res = await client.PostAsJsonAsync($"/api/buildings/{Engineering}/memos", new { author = "a", text = "b", deviceId = "c" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    public async Task 사진을_저장하고_photoUrl_로_받을_수_있다(string kind)
    {
        byte[] bytes = kind == "jpg" ? TinyJpeg : TinyPng;
        // 파일 이름이 아니라 내용으로 형식을 판단한다
        var memo = await CreateAsync(form: Form(photo: bytes, photoName: "아무이름.bin"));

        string id = memo.GetProperty("id").GetString()!;
        string photoUrl = memo.GetProperty("photoUrl").GetString()!;
        Assert.Equal($"/photos/{id}.{kind}", photoUrl);

        var photo = await client.GetAsync(photoUrl);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal(bytes, await photo.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task JPEG_PNG_가_아닌_사진은_400이고_메모도_안_생긴다()
    {
        var res = await client.PostAsync($"/api/buildings/{Engineering}/memos",
            Form(photo: "GIF89a-not-allowed"u8.ToArray(), photoName: "a.jpg"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("photo", await ErrorOf(res));

        var list = await client.GetFromJsonAsync<JsonElement[]>($"/api/buildings/{Engineering}/memos");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task 사진이_10MB_를_넘으면_400()
    {
        byte[] big = new byte[10 * 1024 * 1024 + 1];
        TinyJpeg.CopyTo(big, 0);
        var res = await client.PostAsync($"/api/buildings/{Engineering}/memos", Form(photo: big));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("10MB", await ErrorOf(res));
    }

    [Fact]
    public async Task 메모_하나_조회()
    {
        var created = await CreateAsync();
        string id = created.GetProperty("id").GetString()!;

        var memo = await client.GetFromJsonAsync<JsonElement>($"/api/memos/{id}");
        Assert.Equal(created.GetProperty("text").GetString(), memo.GetProperty("text").GetString());
        Assert.Equal(created.GetProperty("createdAt").GetString(), memo.GetProperty("createdAt").GetString());

        var missing = await client.GetAsync($"/api/memos/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 같은_기기만_삭제할_수_있고_사진도_지운다()
    {
        var memo = await CreateAsync(form: Form(deviceId: "device-1", photo: TinyJpeg));
        string id = memo.GetProperty("id").GetString()!;
        string photoFile = Path.Combine(server.PhotosDir, $"{id}.jpg");
        Assert.True(File.Exists(photoFile));

        // 헤더 없음 / 다른 기기 → 403
        var noHeader = await client.DeleteAsync($"/api/memos/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        var other = new HttpRequestMessage(HttpMethod.Delete, $"/api/memos/{id}");
        other.Headers.Add("X-Device-Id", "device-2");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(other)).StatusCode);

        // 같은 기기 → 204, 사진 파일도 사라짐
        var mine = new HttpRequestMessage(HttpMethod.Delete, $"/api/memos/{id}");
        mine.Headers.Add("X-Device-Id", "device-1");
        var deleted = await client.SendAsync(mine);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(photoFile));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/memos/{id}")).StatusCode);

        // 이미 지운 메모 → 404
        var again = new HttpRequestMessage(HttpMethod.Delete, $"/api/memos/{id}");
        again.Headers.Add("X-Device-Id", "device-1");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task 서버를_다시_켜도_메모가_남는다()
    {
        await CreateAsync(form: Form(text: "남아 있어야 함"));

        // 같은 DB 파일로 두 번째 서버를 띄운다
        using var second = server.WithWebHostBuilder(_ => { });
        var list = await second.CreateClient().GetFromJsonAsync<JsonElement[]>($"/api/buildings/{Engineering}/memos");
        Assert.Equal("남아 있어야 함", list!.Single().GetProperty("text").GetString());
    }

    [Fact]
    public async Task 웹_페이지에_건물별_메모_수와_메모가_보이고_HTML_은_이스케이프된다()
    {
        await CreateAsync(form: Form(text: "<script>alert(1)</script>"));
        await CreateAsync(form: Form(text: "둘째"));

        string home = await client.GetStringAsync("/");
        Assert.Contains("공학관", home);
        Assert.Contains("메모 2", home);
        Assert.Contains($"/buildings/{Engineering}", home);

        string page = await client.GetStringAsync($"/buildings/{Engineering}");
        Assert.Contains("둘째", page);
        Assert.Contains("&lt;script&gt;", page);
        Assert.DoesNotContain("<script>alert", page);

        var missing = await client.GetAsync("/buildings/way-1");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
