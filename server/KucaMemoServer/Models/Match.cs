namespace KucaMemoServer.Models;

// 실시간 산책 매칭 (docs/api.md 의 "매칭"). JSON 은 camelCase, 시각은 ISO 8601 UTC 문자열.

public record MatchJoinRequest(int Minutes, string? MatchGender, string? BuildingId);
public record AppearanceRequest(string? Text);
public record ArrivedRequest(string? BuildingId);
public record MatchCancelRequest(string? Reason);
public record ReportRequest(string? MatchId, string? Reason, string? Text);
public record BlockRequest(string? MatchId);

/// <summary>상대에게서 보이는 것은 닉네임 · 공통 취향 · 인상착의뿐이다 (성별 · 계정 · 건물은 안 보임)</summary>
public record MatchPartner(string Nickname, List<string> CommonTastes, string? Appearance,
                           string? WhyMatched = null,         // 왜 연결했는지 한 줄 (AI, 실패하면 틀 문장)
                           string? WhyMatchedSource = null);  // ai · fallback
public record MeetSpotView(string BuildingId, string Name, double Lat, double Lon, string? MeetSpecies);
public record MatchArrived(bool Me, bool Partner);
public record SmalltalkResult(List<string> Smalltalk, string Source, List<KucaMemoServer.Services.SmalltalkCard> SmalltalkCards);

/// <summary>
/// 두 사람이 함께 걸을 코스 (만남 스팟에서 출발). 둘 다 "이 코스 좋아요"(agree)를 누르면 확정.
/// "다른 코스"는 매칭마다 3번까지, 누르면 두 사람의 동의가 지워진다.
/// </summary>
public record MatchCourse(
    string RecommendationId, string Title, string Source, List<RecommendStop> Stops, int TotalDistanceM, int TotalMinutes,
    bool MyAgree, bool PartnerAgree, bool Confirmed, int ChangesLeft,
    HealthEstimate? Health = null,          // 대략 칼로리 · 걸음 수 (보통 빠르기, 몸무게 50~70kg 범위)
    CourseDifficulty? Difficulty = null);   // 난이도 하 · 중 · 상

/// <summary>
/// GET /api/match 가 돌려주는 지금 상태. State 에 따라 채워지는 필드가 다르다 (나머지는 null).
/// idle · waiting · proposed · meeting · walking · ended
/// </summary>
public record MatchState(
    string State,
    string? MatchId = null,
    int? Minutes = null,
    string? WaitingSince = null,
    string? WaitingUntil = null,
    MatchPartner? Partner = null,
    string? RespondBy = null,
    string? MyAnswer = null,
    MeetSpotView? MeetSpot = null,
    string? MeetBy = null,
    List<string>? Smalltalk = null,
    string? SmalltalkSource = null,
    string? MyAppearance = null,
    MatchArrived? Arrived = null,
    string? WalkId = null,
    string? EndReason = null,
    string? CancelledBy = null,
    int? NowSearching = null,                                         // waiting: 나 말고 지금 짝을 찾는 사람 수
    List<KucaMemoServer.Services.SmalltalkCard>? SmalltalkCards = null, // smalltalk 와 같은 주제 + 분류 · 이모지
    MatchCourse? Course = null);                                      // meeting · walking: 함께 걸을 코스 (준비 중이면 null)
