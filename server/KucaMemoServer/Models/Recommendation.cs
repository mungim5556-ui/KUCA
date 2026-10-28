namespace KucaMemoServer.Models;

// 산책 추천 (docs/api.md 의 "POST /api/recommendations (v2)"). JSON 은 camelCase 로 오간다.

/// <summary>POST /api/recommendations 본문. v1 필드만 보내도 된다 (Pace·AfterMeal 은 v2, 선택).</summary>
public record RecommendRequest(
    int Minutes,
    List<string>? Tastes,
    double Lat,
    double Lon,
    List<string>? VisitedBuildingIds,
    List<string>? CandidateBuildingIds,
    string? Pace = null,          // "slow" · "normal" · "fast" (없으면 계정 취향, 그것도 없으면 normal)
    bool AfterMeal = false,       // 식후 걷기 버튼으로 요청
    string? Wish = null,          // 말로 한 산책 바람 (200자 이내). 있으면 AI 가 판단해 코스를 짠다
    List<VisitCount>? VisitCounts = null,
    double? WeightKg = null,      // (선택) 몸무게 kg — 칼로리 범위를 좁힌다. 서버에 저장하지 않는다   // 장소마다 들른 횟수 (앱의 산책 기록). 안 가 본 곳 · 자주 가던 곳 판단에 씀
    bool Talk = false);           // 걸으며 이야기하기 (산책 메이트 코스): 조용하고 걷기 쉬운 곳만

/// <summary>한 장소를 몇 번 들렀는지</summary>
public record VisitCount(string BuildingId, int Count);

/// <param name="WalkMinutes">앞 장소(첫 곳은 지금 위치)에서 걸어서 몇 분</param>
/// <param name="DistanceM">앞 장소에서 걷는 거리 (m)</param>
public record RecommendStop(string BuildingId, string Name, string Reason, int WalkMinutes, int DistanceM, double Lat, double Lon);

/// <summary>서버가 판단한 지금 상황. Weather 는 GET /api/weather 의 condition, 모르면 null</summary>
public record RecommendContext(string TimeOfDay, string Season, string? Weather);

/// <summary>Source: "ai" (AI 가 고름) 또는 "fallback" (AI 없이 규칙으로 고름)</summary>
public record RecommendResult(
    string Id,
    string Title,
    string Source,
    RecommendContext Context,
    List<RecommendStop> Stops,
    int TotalDistanceM,
    int TotalMinutes,
    string? HealthNote,
    RecommendWish? Wish = null,
    string? Why = null,
    HealthEstimate? Health = null,   // 코스 걸음 수 · 칼로리 범위 · 강도
    CourseGoal? Goal = null,         // 로그인했으면 이번 주 목표와 이 코스의 관계
    CourseDifficulty? Difficulty = null);   // 난이도 하 · 중 · 상 (거리 · 오르막 장소 수)

/// <summary>바람을 어떻게 이해했는지 (앱이 "이렇게 이해했어요" 로 보여 준다). Source: "ai" 또는 "fallback"</summary>
/// <param name="Judgment">어떤 산책이 맞는지 판단과 이유 한 문장</param>
public record RecommendWish(string Summary, string Source, int? Minutes, List<string> Want, List<string> Avoid, string Judgment);
