using System.Globalization;
using KucaMemoServer.Models;

namespace KucaMemoServer.Services;

/// <summary>
/// 산책 완료 · 보상 숫자는 모두 여기 둔다 (docs/api.md 의 "산책 · 완료 · 리뷰 · 보상").
/// [가정] 표시는 아직 팀이 정하지 않은 제안값이다 (Q6 경험치 표).
/// </summary>
public static class WalkRules
{
    /// <summary>[가정] 계획 거리의 이 비율 이상 걸으면 완료</summary>
    public const double CompleteRatio = 0.8;
    /// <summary>[가정] 추천 없이 걸은 산책 · 매칭 산책은 이 거리(m) 이상이면 완료</summary>
    public const int FreeWalkCompleteM = 500;
    /// <summary>[가정] 이보다 빠르면(초속 m) 탈것으로 보고 거리를 깎는다</summary>
    public const double MaxMetersPerSecond = 2.5;
    public const int MaxDistanceM = 50_000;
    public const int MaxDurationSec = 6 * 60 * 60;

    // 경험치 표 (Q6 제안): 1km 10 · 산책 완료 30 · 함께 걸음 60 · 리뷰 10
    /// <summary>[가정] 100m 마다 1 (= 1km 10), 100m 단위 버림</summary>
    public const int MetersPerDistanceXp = 100;
    public const int WalkXp = 30;
    public const int TogetherXp = 60;
    public const int ReviewXp = 10;
    /// <summary>[가정] 완료 보너스 티켓. 걸은 거리 티켓(1km = 1장)은 앱이 센다</summary>
    public const int CompleteTickets = 1;

    /// <summary>[가정] 리뷰는 산책을 끝낸 뒤 이 시간 안에만</summary>
    public static readonly TimeSpan ReviewWindow = TimeSpan.FromHours(24);
    public const int MaxReviewTextLength = 100;

    /// <summary>레벨 L 이 시작되는 경험치: 다음 레벨까지 100 + 10 × (L − 1) (1→2 100, 2→3 110, 3→4 120 …, 10/24 결정).
    /// 앱 GameHUD.LevelOf 와 같은 규칙</summary>
    public static int LevelStartXp(int level)
    {
        int n = level - 1;   // 지나온 레벨 수
        return 100 * n + 5 * n * (n - 1);
    }

    public static int LevelOf(int xp)
    {
        int level = 1;
        while (LevelStartXp(level + 1) <= xp)
            level++;
        return level;
    }
}

/// <summary>산책 API 가 돌려줄 오류. 엔드포인트가 {error, reason} 과 상태 코드로 바꾼다.</summary>
public class WalkException(int status, string error, string? reason = null) : Exception(error)
{
    public int Status { get; } = status;
    public string? Reason { get; } = reason;
}

/// <summary>
/// 산책 시작 · 끝(완료 판정 · 보상) · 리뷰 · 내 진행도. 경험치는 서버만 계산하고 앱은 받은 값을 보여 준다 (D9).
/// 매칭 산책은 MatchService 가 둘 다 도착할 때 만들고, 두 사람이 모두 끝내면 매칭이 completed 로 끝난다.
/// </summary>
public class WalkService(WalkStore store, RecommendationStore recommendations, MatchService matches, MetricsStore metrics, HealthService health, TimeProvider clock)
{
    public WalkStarted Start(string userId, WalkStartRequest? body)
    {
        string? recId = string.IsNullOrWhiteSpace(body?.RecommendationId) ? null : body.RecommendationId.Trim();
        RecommendationStore.Saved? rec = null;
        if (recId != null && (rec = recommendations.Find(recId)) == null)
            throw new WalkException(404, "추천을 찾을 수 없어요.");
        if (body?.Minutes is int asked && !RecommendRules.AllowedMinutes.Contains(asked))
            throw new WalkException(400, "minutes 는 15, 30, 60 중 하나여야 합니다.");
        int? minutes = body?.Minutes ?? rec?.Minutes;   // 추천의 시간은 말로 정한 10 ~ 60분일 수 있다

        string id = "w_" + Guid.NewGuid().ToString("N");
        DateTimeOffset now = clock.GetUtcNow();
        string[] stops = rec?.StopIds ?? [];
        store.Start(id, userId, "solo", recId, minutes, rec?.PlannedDistanceM, stops, null, now);
        metrics.Record(MetricsStore.WalkStarted, rec?.Source ?? "free");
        return new WalkStarted(id, Iso(now), rec?.PlannedDistanceM, stops.ToList());
    }

    public WalkFinished Finish(string userId, string walkId, WalkFinishRequest? body)
    {
        if (body == null || body.DistanceM < 0 || body.DurationSec < 0 || body.DurationSec > WalkRules.MaxDurationSec)
            throw new WalkException(400, "distanceM, durationSec 는 0 이상이어야 합니다 (최대 6시간).");
        if (!HealthRules.ValidWeight(body.WeightKg))
            throw new WalkException(400, $"weightKg 는 {HealthRules.MinWeightKg}~{HealthRules.MaxWeightKg} 입니다.");
        if (!HealthRules.ValidSteps(body.Steps))
            throw new WalkException(400, $"steps 는 {HealthRules.MaxSteps} 이하입니다.");
        WalkStore.Walk walk = Own(userId, walkId);
        if (walk.FinishedAt != null)
            throw new WalkException(409, "이미 끝낸 산책이에요.");

        // 시간에 비해 너무 멀리 갔으면 탈것으로 보고 깎는다
        int distance = (int)Math.Min(Math.Min(body.DistanceM, body.DurationSec * WalkRules.MaxMetersPerSecond), WalkRules.MaxDistanceM);
        var visited = new HashSet<string>(body.VisitedBuildingIds ?? []);
        bool completed = walk.PlannedDistanceM is > 0 && walk.Stops.Length > 0
            ? visited.Contains(walk.Stops[^1]) || distance >= walk.PlannedDistanceM * WalkRules.CompleteRatio
            : distance >= WalkRules.FreeWalkCompleteM;

        DateTimeOffset now = clock.GetUtcNow();
        store.Finish(walkId, userId, distance, body.DurationSec, completed, now);

        var items = new List<XpItem>();
        int distanceXp = distance / WalkRules.MetersPerDistanceXp;
        if (distanceXp > 0) items.Add(new XpItem("distance", distanceXp));
        if (completed) items.Add(new XpItem("walk", WalkRules.WalkXp));
        if (completed && walk.Kind == "match") items.Add(new XpItem("together", WalkRules.TogetherXp));
        foreach (XpItem item in items)
            store.AddXp(userId, walkId, item.Kind, item.Xp, now);

        if (walk.Kind == "match")
            matches.WalkFinished(userId, walkId);
        metrics.Record(MetricsStore.WalkFinished, SourceOf(walk), completed ? 1 : 0);

        var reward = new WalkReward(completed ? WalkRules.CompleteTickets : 0, items.Sum(i => i.Xp), items);
        // 건강: 실제로 걸은 거리 · 시간 · 빠르기로 걸음 수와 칼로리 범위 (몸무게는 저장하지 않는다)
        HealthEstimate healthNow = HealthRules.Estimate(distance, body.DurationSec / 60,
                                                        HealthRules.PaceFromSpeed(distance, body.DurationSec), body.WeightKg);
        // 휴대폰 걸음 센서 값이 거리와 맞으면 그 값을 쓴다 (칼로리는 그대로 시간 · 빠르기로)
        if (HealthRules.MeasuredSteps(body.Steps, distance) is { } measured)
            healthNow = healthNow with { Steps = measured, StepsMeasured = true };
        return new WalkFinished(completed, distance, reward, ProgressOf(userId), healthNow);
    }

    public ReviewSaved Review(string userId, string walkId, WalkReviewRequest? body, string? text)
    {
        if (body == null || body.CourseRating is < 1 or > 5)
            throw new WalkException(400, "courseRating 은 1~5 입니다.");
        WalkStore.Walk walk = Own(userId, walkId);
        if (walk.FinishedAt == null)
            throw new WalkException(409, "산책을 끝낸 뒤에 리뷰를 쓸 수 있어요.");
        if (walk.Reviewed)
            throw new WalkException(409, "이미 리뷰를 썼어요.");
        DateTimeOffset now = clock.GetUtcNow();
        if (now - walk.FinishedAt.Value > WalkRules.ReviewWindow)
            throw new WalkException(409, "리뷰는 산책을 끝내고 24시간 안에만 쓸 수 있어요.");

        // 코스 별점만 받는다. 매칭 산책이어도 상대를 평가하지 않는다
        store.AddReview(walkId, userId, body.CourseRating, text, now);
        metrics.Record(MetricsStore.Review, SourceOf(walk), body.CourseRating);
        store.AddXp(userId, walkId, "review", WalkRules.ReviewXp, now);
        var items = new List<XpItem> { new("review", WalkRules.ReviewXp) };
        return new ReviewSaved(new WalkReward(0, WalkRules.ReviewXp, items), ProgressOf(userId));
    }

    /// <summary>리뷰 글 검사 전에 권한 · 상태만 먼저 본다 (검사는 엔드포인트가 비동기로)</summary>
    public void CheckCanReview(string userId, string walkId) => Own(userId, walkId);

    public Progress ProgressOf(string userId)
    {
        int xp = store.TotalXp(userId);
        int level = WalkRules.LevelOf(xp);
        (int walks, int distance, int together) = store.Totals(userId);
        return new Progress(level, xp, WalkRules.LevelStartXp(level), WalkRules.LevelStartXp(level + 1), walks, distance, together,
                            health.Week(userId));
    }

    /// <summary>지표용 산책 종류: 매칭 · 추천(ai/fallback) · 자유</summary>
    string SourceOf(WalkStore.Walk walk) =>
        walk.Kind == "match" ? "match"
        : walk.RecommendationId != null && recommendations.Find(walk.RecommendationId) is { } rec ? rec.Source
        : "free";

    WalkStore.Walk Own(string userId, string walkId)
    {
        if (store.Find(walkId, userId) is { } walk)
            return walk;
        throw store.Exists(walkId)
            ? new WalkException(403, "내 산책이 아니에요.", "not_yours")
            : new WalkException(404, "산책을 찾을 수 없어요.");
    }

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
