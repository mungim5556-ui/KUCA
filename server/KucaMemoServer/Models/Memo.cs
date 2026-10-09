using System.Text.Json.Serialization;

namespace KucaMemoServer.Models;

/// <summary>
/// 건물에 남긴 메모 하나. API 응답의 JSON 모양과 같다 (docs/api.md 의 Memo).
/// </summary>
public class Memo
{
    /// <summary>메모 ID (서버가 만든다, Guid 문자열)</summary>
    public string Id { get; set; } = "";

    /// <summary>메모가 달린 건물 ID (예: "way-775364189")</summary>
    public string BuildingId { get; set; } = "";

    /// <summary>작성자 닉네임 (1~20자)</summary>
    public string Author { get; set; } = "";

    /// <summary>메모 내용 (1~500자)</summary>
    public string Text { get; set; } = "";

    /// <summary>사진 주소 (예: "/photos/3f2a….jpg"). 사진이 없으면 null.</summary>
    public string? PhotoUrl { get; set; }

    /// <summary>작성 시각 (UTC)</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>좋아요 수</summary>
    public int LikeCount { get; set; }

    /// <summary>댓글 수</summary>
    public int CommentCount { get; set; }

    /// <summary>요청한 기기(X-Device-Id)가 좋아요를 눌렀는지. 헤더가 없으면 false.</summary>
    public bool LikedByMe { get; set; }

    /// <summary>작성한 기기 ID. 삭제 권한 확인용으로 서버에만 저장하고 API 응답에는 내보내지 않는다.</summary>
    [JsonIgnore]
    public string DeviceId { get; set; } = "";
}
