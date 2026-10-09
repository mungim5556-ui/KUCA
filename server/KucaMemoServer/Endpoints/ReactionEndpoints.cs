using System.Globalization;
using System.Text.Json;
using KucaMemoServer.Models;
using KucaMemoServer.Services;
using static KucaMemoServer.Endpoints.MemoEndpoints;

namespace KucaMemoServer.Endpoints;

/// <summary>
/// 메모에 대한 반응: 좋아요와 댓글. 주소와 요청/응답 모양은 docs/api.md 를 그대로 따른다.
/// 사용자는 기기 ID(X-Device-Id 헤더, 댓글 작성 본문의 deviceId)로 구분한다.
/// </summary>
public static class ReactionEndpoints
{
    public const int DefaultCommentLimit = 50;
    public const int MaxCommentLimit = 100;
    public const int MaxCommentLength = 200;

    /// <summary>댓글 작성 요청 본문 (JSON)</summary>
    public record NewComment(string? Author, string? Text, string? DeviceId);

    public static void MapReactionEndpoints(this WebApplication app)
    {
        var likes = app.MapGroup("/api/memos/{memoId}/like").WithTags("Likes");

        // 좋아요 누르기. 이미 눌렀으면 그대로 (여러 번 보내도 결과가 같다).
        likes.MapPut("/", (string memoId, HttpRequest request, MemoStore memos) =>
            SetLike(memoId, request, memos, liked: true));

        // 좋아요 취소. 누른 적이 없어도 성공.
        likes.MapDelete("/", (string memoId, HttpRequest request, MemoStore memos) =>
            SetLike(memoId, request, memos, liked: false));

        var comments = app.MapGroup("/api").WithTags("Comments");

        // 메모의 댓글 목록 (오래된 순). ?limit=1~100 (기본 50), ?after=시각 이면 그보다 나중 댓글만.
        comments.MapGet("/memos/{memoId}/comments", (string memoId, int? limit, string? after, MemoStore memos) =>
        {
            if (memos.Find(memoId) is null)
                return MemoNotFound(memoId);

            DateTime? afterTime = null;
            if (!string.IsNullOrWhiteSpace(after))
            {
                if (!DateTime.TryParse(after, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
                    return Error(StatusCodes.Status400BadRequest, "after 는 ISO 8601 시각이어야 합니다 (예: 2026-10-06T05:12:30Z)");
                afterTime = parsed;
            }

            int take = Math.Clamp(limit ?? DefaultCommentLimit, 1, MaxCommentLimit);
            return Results.Ok(memos.ListComments(memoId, take, afterTime));
        });

        // 댓글 작성. JSON 으로 author, text, deviceId 를 받는다.
        comments.MapPost("/memos/{memoId}/comments", async (string memoId, HttpRequest request, MemoStore memos) =>
        {
            if (memos.Find(memoId) is null)
                return MemoNotFound(memoId);

            if (!request.HasJsonContentType())
                return Error(StatusCodes.Status400BadRequest, "본문은 JSON 이어야 합니다 (Content-Type: application/json)");

            NewComment? body;
            try
            {
                body = await request.ReadFromJsonAsync<NewComment>();
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, "JSON 형식이 올바르지 않습니다");
            }

            string author = body?.Author?.Trim() ?? "";
            string text = body?.Text?.Trim() ?? "";
            string deviceId = body?.DeviceId?.Trim() ?? "";

            if (author.Length is 0 or > MaxAuthorLength)
                return Error(StatusCodes.Status400BadRequest, $"author 는 1~{MaxAuthorLength}자여야 합니다");
            if (text.Length is 0 or > MaxCommentLength)
                return Error(StatusCodes.Status400BadRequest, $"text 는 1~{MaxCommentLength}자여야 합니다");
            if (deviceId.Length is 0 or > MaxDeviceIdLength)
                return Error(StatusCodes.Status400BadRequest, $"deviceId 는 1~{MaxDeviceIdLength}자여야 합니다");

            var comment = new Comment
            {
                Id = Guid.NewGuid().ToString(),
                MemoId = memoId,
                Author = author,
                Text = text,
                DeviceId = deviceId,
                CreatedAt = TruncateToMilliseconds(DateTime.UtcNow),
            };
            memos.AddComment(comment);
            return Results.Created($"/api/comments/{comment.Id}", comment);
        });

        // 댓글 삭제. 댓글을 쓴 기기만 (X-Device-Id 가 작성 때의 deviceId 와 같아야 함).
        comments.MapDelete("/comments/{commentId}", (string commentId, HttpRequest request, MemoStore memos) =>
        {
            Comment? comment = memos.FindComment(commentId);
            if (comment is null)
                return Error(StatusCodes.Status404NotFound, $"댓글을 찾을 수 없습니다: {commentId}");

            string deviceId = request.Headers["X-Device-Id"].ToString().Trim();
            if (deviceId.Length == 0 || deviceId != comment.DeviceId)
                return Error(StatusCodes.Status403Forbidden, "이 기기에서 쓴 댓글만 지울 수 있습니다");

            memos.DeleteComment(commentId);
            return Results.NoContent();
        });
    }

    static IResult SetLike(string memoId, HttpRequest request, MemoStore memos, bool liked)
    {
        string deviceId = request.Headers["X-Device-Id"].ToString().Trim();
        if (deviceId.Length is 0 or > MaxDeviceIdLength)
            return Error(StatusCodes.Status400BadRequest, $"X-Device-Id 헤더(1~{MaxDeviceIdLength}자)가 필요합니다");
        if (memos.Find(memoId) is null)
            return MemoNotFound(memoId);

        int count = memos.SetLike(memoId, deviceId, liked);
        return Results.Ok(new { likeCount = count, likedByMe = liked });
    }
}
