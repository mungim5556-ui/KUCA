using System.Globalization;
using KucaMemoServer.Models;
using KucaMemoServer.Services;

namespace KucaMemoServer.Endpoints;

/// <summary>
/// 메모 API. 주소와 요청/응답 모양은 docs/api.md 를 그대로 따른다.
/// </summary>
public static class MemoEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int MaxAuthorLength = 20;
    public const int MaxTextLength = 500;
    public const int MaxDeviceIdLength = 64;

    public static void MapMemoEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Memos");

        // 건물의 메모 목록 (최신순). ?limit=1~50 (기본 20), ?before=시각 이면 그보다 이전 메모만.
        // X-Device-Id 헤더를 보내면 각 메모의 likedByMe 가 채워진다.
        group.MapGet("/buildings/{buildingId}/memos", (string buildingId, int? limit, string? before, HttpRequest request,
                                                       BuildingStore buildings, MemoStore memos) =>
        {
            if (buildings.Find(buildingId) is null)
                return BuildingNotFound(buildingId);

            DateTime? beforeTime = null;
            if (!string.IsNullOrWhiteSpace(before))
            {
                if (!DateTime.TryParse(before, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
                    return Error(StatusCodes.Status400BadRequest, "before 는 ISO 8601 시각이어야 합니다 (예: 2026-10-06T05:12:30Z)");
                beforeTime = parsed;
            }

            int take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            return Results.Ok(memos.List(buildingId, take, beforeTime, ViewerDeviceId(request)));
        });

        // 메모 작성. multipart/form-data 로 author, text, deviceId, photo(선택)를 받는다.
        group.MapPost("/buildings/{buildingId}/memos", async (string buildingId, HttpRequest request,
                                                              BuildingStore buildings, MemoStore memos, PhotoStore photos) =>
        {
            if (buildings.Find(buildingId) is null)
                return BuildingNotFound(buildingId);

            if (!request.HasFormContentType)
                return Error(StatusCodes.Status400BadRequest, "본문은 multipart/form-data 여야 합니다");

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync();
            }
            catch (InvalidDataException)
            {
                // FormOptions 의 크기 제한을 넘은 경우
                return Error(StatusCodes.Status400BadRequest, "사진은 10MB 이하만 올릴 수 있습니다");
            }

            string author = form["author"].ToString().Trim();
            string text = form["text"].ToString().Trim();
            string deviceId = form["deviceId"].ToString().Trim();

            if (author.Length is 0 or > MaxAuthorLength)
                return Error(StatusCodes.Status400BadRequest, $"author 는 1~{MaxAuthorLength}자여야 합니다");
            if (text.Length is 0 or > MaxTextLength)
                return Error(StatusCodes.Status400BadRequest, $"text 는 1~{MaxTextLength}자여야 합니다");
            if (deviceId.Length is 0 or > MaxDeviceIdLength)
                return Error(StatusCodes.Status400BadRequest, $"deviceId 는 1~{MaxDeviceIdLength}자여야 합니다");

            IFormFile? photo = form.Files.GetFile("photo");
            string? extension = null;
            if (photo is { Length: > 0 })
            {
                if (photo.Length > PhotoStore.MaxBytes)
                    return Error(StatusCodes.Status400BadRequest, "photo 는 10MB 이하만 올릴 수 있습니다");
                extension = await PhotoStore.DetectExtensionAsync(photo);
                if (extension is null)
                    return Error(StatusCodes.Status400BadRequest, "photo 는 JPEG 또는 PNG 여야 합니다");
            }

            var memo = new Memo
            {
                Id = Guid.NewGuid().ToString(),
                BuildingId = buildingId,
                Author = author,
                Text = text,
                DeviceId = deviceId,
                // 밀리초 아래는 버린다. 응답의 createdAt 을 그대로 before 에 넣어도 같은 메모가 다시 오지 않게.
                CreatedAt = TruncateToMilliseconds(DateTime.UtcNow),
            };

            if (photo is not null && extension is not null)
                memo.PhotoUrl = await photos.SaveAsync(memo.Id, extension, photo);

            try
            {
                memos.Add(memo);
            }
            catch
            {
                photos.Delete(memo.PhotoUrl);
                throw;
            }

            return Results.Created($"/api/memos/{memo.Id}", memo);
        })
        .DisableAntiforgery();

        // 메모 하나. X-Device-Id 헤더를 보내면 likedByMe 가 채워진다.
        group.MapGet("/memos/{memoId}", (string memoId, HttpRequest request, MemoStore memos) =>
            memos.Find(memoId, ViewerDeviceId(request)) is { } memo ? Results.Ok(memo) : MemoNotFound(memoId));

        // 메모 삭제. X-Device-Id 헤더가 작성할 때의 deviceId 와 같을 때만. 사진 파일·좋아요·댓글도 함께 지운다.
        group.MapDelete("/memos/{memoId}", (string memoId, HttpRequest request, MemoStore memos, PhotoStore photos) =>
        {
            Memo? memo = memos.Find(memoId);
            if (memo is null)
                return MemoNotFound(memoId);

            string deviceId = request.Headers["X-Device-Id"].ToString().Trim();
            if (deviceId.Length == 0 || deviceId != memo.DeviceId)
                return Error(StatusCodes.Status403Forbidden, "이 기기에서 쓴 메모만 지울 수 있습니다");

            memos.Delete(memoId);
            photos.Delete(memo.PhotoUrl);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// X-Device-Id 헤더 값 (앞뒤 공백 제거). 없거나 64자를 넘으면 null.
    /// 조회에서는 likedByMe 계산에만 쓰므로 틀린 값이어도 오류 대신 "모르는 기기"로 본다.
    /// </summary>
    internal static string? ViewerDeviceId(HttpRequest request)
    {
        string id = request.Headers["X-Device-Id"].ToString().Trim();
        return id.Length is > 0 and <= MaxDeviceIdLength ? id : null;
    }

    internal static DateTime TruncateToMilliseconds(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, t.Kind);

    internal static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    static IResult BuildingNotFound(string id) => Error(StatusCodes.Status404NotFound, $"건물을 찾을 수 없습니다: {id}");

    internal static IResult MemoNotFound(string id) => Error(StatusCodes.Status404NotFound, $"메모를 찾을 수 없습니다: {id}");
}
