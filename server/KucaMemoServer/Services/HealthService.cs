using System.Text.Json;
using System.Text.RegularExpressions;
using KucaMemoServer.Models;

namespace KucaMemoServer.Services;

/// <summary>
/// 건강 숫자 (docs/api.md 의 "건강"). [가정] 표시는 팀이 정하지 않은 제안값이다.
/// 칼로리 = MET × 몸무게(kg) × 시간(h), MET 은 2011 신체활동 개요서(Compendium of Physical Activities)의 걷기 값.
/// 걸음 센서가 없으므로 칼로리는 범위로만 보여 준다. 몸무게는 앱이 요청마다 보내고 서버에 저장하지 않는다.
/// </summary>
public static class HealthRules
{
    /// <summary>걷기 MET: 느긋 2.8 (약 3.2km/h) · 보통 3.5 (약 4.5km/h) · 빠름 4.3 (약 5.4km/h)</summary>
    public static double Met(string pace) => pace switch { "slow" => 2.8, "fast" => 4.3, _ => 3.5 };
    /// <summary>[가정] 몸무게를 모를 때 범위 (kg)</summary>
    public const double UnknownWeightMin = 50, UnknownWeightMax = 70;
    /// <summary>[가정] 몸무게를 알 때 ± 비율 (걸음 센서 없음 · 오르막 모름)</summary>
    public const double KnownWeightSpread = 0.1;
    public const double MinWeightKg = 30, MaxWeightKg = 200;
    /// <summary>[가정] 보폭 (m). 키를 받지 않으므로 성인 평균</summary>
    public const double StrideM = 0.7;
    /// <summary>[가정] 센서 걸음 수를 믿는 보폭 범위 (m). 이 밖이면 GPS 거리와 안 맞으니 어림값을 쓴다</summary>
    public const double MinMeasuredStrideM = 0.25, MaxMeasuredStrideM = 1.6;
    /// <summary>[가정] 이보다 짧게 걸었으면 거리로 걸음 수를 맞춰 볼 수 없어 센서 값을 쓰지 않는다 (m)</summary>
    public const int MinDistanceForMeasuredM = 100;
    public const int MaxSteps = 100_000;
    /// <summary>WHO 권장: 일주일에 중간 강도 활동 150분</summary>
    public const int WeeklyGoalMinutes = 150;
    /// <summary>[가정] 속도로 걷기 빠르기를 나눈다 (m/분): 70 미만 느긋 · 85 미만 보통 · 이상 빠름</summary>
    public const int SlowBelowMpm = 70, NormalBelowMpm = 85;
    public const string Disclaimer = "일반 건강 정보이며 의료 조언이 아닙니다.";

    /// <summary>0 이하는 "모름" (앱의 JsonUtility 는 칸을 뺄 수 없어 0 을 보낸다)</summary>
    public static double? Known(double? kg) => kg is > 0 ? kg : null;

    public static bool ValidWeight(double? kg) => Known(kg) is not { } w || w is >= MinWeightKg and <= MaxWeightKg;

    public static string PaceFromSpeed(int distanceM, int durationSec)
    {
        if (durationSec <= 0) return "normal";
        double mpm = distanceM / (durationSec / 60.0);
        return mpm < SlowBelowMpm ? "slow" : mpm < NormalBelowMpm ? "normal" : "fast";
    }

    public static HealthEstimate Estimate(int distanceM, int minutes, string pace, double? weightKg)
    {
        double hours = Math.Max(0, minutes) / 60.0, met = Met(pace);
        (double lo, double hi) = Known(weightKg) is { } w
            ? (w * (1 - KnownWeightSpread), w * (1 + KnownWeightSpread))
            : (UnknownWeightMin, UnknownWeightMax);
        int steps = (int)Math.Round(Math.Max(0, distanceM) / StrideM / 10.0) * 10;   // 10 걸음 단위
        (string key, string label) = pace switch
        {
            "slow" => ("light", "가벼운 산책"),
            "fast" => ("brisk", "운동 되는 걷기"),
            _ => ("moderate", "보통 걷기"),
        };
        return new HealthEstimate(steps, (int)Math.Round(met * lo * hours), (int)Math.Round(met * hi * hours), key, label);
    }

    /// <summary>
    /// 센서 걸음 수를 쓸지: 0 이하는 "모름" (앱이 칸을 뺄 수 없음), 걸은 거리로 나눈 보폭이 0.25~1.6m 일 때만 믿는다.
    /// 걸음 수는 보상 · 완료에 쓰지 않고 화면에만 보이므로, 안 맞으면 거절하지 않고 어림값으로 돌아간다.
    /// </summary>
    public static int? MeasuredSteps(int? steps, int distanceM)
    {
        if (steps is not > 0 || distanceM < MinDistanceForMeasuredM) return null;
        double stride = distanceM / (double)steps.Value;
        return stride is >= MinMeasuredStrideM and <= MaxMeasuredStrideM ? steps : null;
    }

    public static bool ValidSteps(int? steps) => steps is null or <= MaxSteps;

    /// <summary>[가정] 난이도: 이 거리 이하 · 오르막 없음이면 하, 이 거리 넘거나 오르막이 많으면 상 (m)</summary>
    public const int EasyMaxM = 1500, HardOverM = 3000, HardWithHillOverM = 2000, HardHills = 2;

    /// <summary>코스 난이도: 거리와 가는 길에 힘든 오르막이 있는 장소 수 (spots.json 의 hill)</summary>
    public static CourseDifficulty Difficulty(int distanceM, int hills)
    {
        if (distanceM > HardOverM || hills >= HardHills || hills > 0 && distanceM > HardWithHillOverM)
            return new CourseDifficulty("hard", "상", hills);
        if (distanceM <= EasyMaxM && hills == 0)
            return new CourseDifficulty("easy", "하", hills);
        return new CourseDifficulty("normal", "중", hills);
    }

    /// <summary>이번 주가 시작된 때 (한국 시각 월요일 0시)</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset now)
    {
        DateTimeOffset kst = now.ToOffset(TimeSpan.FromHours(9));
        int sinceMonday = ((int)kst.DayOfWeek + 6) % 7;
        return new DateTimeOffset(kst.Year, kst.Month, kst.Day, 0, 0, 0, TimeSpan.FromHours(9)).AddDays(-sinceMonday);
    }
}

/// <summary>주간 목표 계산 (산책 기록에서)</summary>
public class HealthService(WalkStore walks, TimeProvider clock)
{
    public WeekGoal Week(string userId)
    {
        var done = walks.FinishedSince(userId, HealthRules.WeekStart(clock.GetUtcNow()));
        int minutes = done.Sum(w => w.DurationSec) / 60;
        return new WeekGoal(minutes, HealthRules.WeeklyGoalMinutes, Math.Max(0, HealthRules.WeeklyGoalMinutes - minutes), done.Count);
    }

    /// <summary>코스 카드의 목표 한 줄: 이 코스로 이번 주 목표를 채우는지</summary>
    public static CourseGoal ForCourse(WeekGoal week, int courseMinutes)
    {
        bool completes = week.MinutesLeft > 0 && courseMinutes >= week.MinutesLeft;
        string note = week.MinutesLeft == 0 ? $"이번 주 목표 {week.GoalMinutes}분을 이미 채웠어요"
            : completes ? "이 코스로 걸으면 이번 주 목표를 채워요"
            : $"이 코스로 걸으면 이번 주 목표까지 {week.MinutesLeft - courseMinutes}분 남아요";
        return new CourseGoal(week.WeekMinutes, week.GoalMinutes, week.MinutesLeft, completes, note);
    }

    /// <summary>요일 · 시간대 버릇 (최근 4주): 가장 자주 걷기 시작한 요일과 시간대 (기록이 2번 미만이면 null)</summary>
    public (string? Day, string? Time) Habit(string userId)
    {
        var recent = walks.FinishedSince(userId, clock.GetUtcNow().AddDays(-28));
        if (recent.Count < 2) return (null, null);
        string[] days = ["일", "월", "화", "수", "목", "금", "토"];
        var kst = recent.Select(w => w.StartedAt.ToOffset(TimeSpan.FromHours(9))).ToList();
        string day = days[(int)kst.GroupBy(t => t.DayOfWeek).OrderByDescending(g => g.Count()).First().Key] + "요일";
        string time = kst.GroupBy(t => RecommendRules.TimeOfDay(t.Hour)).OrderByDescending(g => g.Count()).First().Key switch
        {
            "morning" => "아침", "day" => "낮", "evening" => "저녁", _ => "밤",
        };
        return (day, time);
    }
}

/// <summary>
/// 주간 건강 코치 한 줄 (GET /api/me/coach). 챗쿠 AI(산책 추천과 같은 IRouteAi)가 이번 주 숫자 요약만 보고 격려와 다음 제안을 쓴다.
/// - 서버가 준 숫자 말고 다른 숫자가 있거나, 체중 · 다이어트 · 질병 같은 말이 있으면 버리고 틀 문장으로 바꾼다.
/// - 같은 날 같은 기록이면 다시 부르지 않는다 (메모리 캐시).
/// </summary>
public partial class CoachService(IRouteAi ai, HealthService health, MetricsStore metrics, TimeProvider clock, ILogger<CoachService> log)
{
    public const int MaxLength = 90;
    static readonly Dictionary<string, (string Key, CoachMessage Message)> cache = [];
    /// <summary>효과 단정 · 의료 표현 (이 말이 들어 있으면 AI 문장을 버린다). "살짝" · "약속" 은 걸리지 않게 붙여 쓴 꼴로</summary>
    static readonly string[] Forbidden = ["체중", "몸무게", "다이어트", "살이", "살을", "살 빠", "감량", "혈당", "혈압", "질병", "질환", "치료", "당뇨", "비만", "의사", "약을", "복용"];

    public async Task<CoachMessage> GetAsync(string userId, CancellationToken ct)
    {
        WeekGoal week = health.Week(userId);
        (string? day, string? time) = health.Habit(userId);
        DateTimeOffset kst = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(9));
        int daysLeft = 7 - ((int)kst.DayOfWeek + 6) % 7;   // 오늘 포함 남은 날
        string key = $"{kst:yyyy-MM-dd}|{week.WeekMinutes}|{week.WeekWalks}";
        lock (cache)
            if (cache.TryGetValue(userId, out var hit) && hit.Key == key)
                return hit.Message;

        string text = Fallback(week, day, time);
        string source = "fallback";
        if (ai.IsConfigured)
        {
            string system =
                "너는 경희대 국제캠퍼스 산책 앱의 다정한 걷기 코치야. 학생의 이번 주 걷기 기록을 보고 격려와 다음 산책 제안을 한두 문장으로 써. " +
                "아래에 주어진 숫자만 쓰고, 다른 숫자(칼로리 · 걸음 수 · 날짜 등)는 만들지 마. " +
                "체중 · 다이어트 · 살 · 혈당 · 질병 · 치료 같은 건강 효과나 의료 이야기는 하지 마. " +
                $"따뜻한 존댓말, {MaxLength}자 이내, 이모지 없이. 반드시 JSON 하나만 답해: {{\"text\": \"…\"}}";
            string user =
                $"이번 주 걸은 시간: {week.WeekMinutes}분 (목표 {week.GoalMinutes}분, 남은 {week.MinutesLeft}분)\n" +
                $"이번 주 산책 횟수: {week.WeekWalks}번\n" +
                $"이번 주 남은 날: 오늘 포함 {daysLeft}일\n" +
                $"자주 걷는 때: {(day != null ? $"{day} {time}" : "아직 모름")}\n" +
                "추천할 수 있는 산책 길이: 15분 · 30분 · 60분";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var allowed = new HashSet<string> { $"{week.WeekMinutes}", $"{week.GoalMinutes}", $"{week.MinutesLeft}", $"{week.WeekWalks}", $"{daysLeft}", "15", "30", "60" };
            if (Check(await ai.CompleteAsync(system, user, timeout.Token), allowed) is { } said)
                (text, source) = (said, "ai");
            else
                log.LogWarning("코치 AI 답을 쓰지 못해 틀 문장으로 대신합니다");
        }
        metrics.Record(MetricsStore.AiCall, "coach", source == "ai" ? 1 : 0);
        var message = new CoachMessage(text, source, week, HealthRules.Disclaimer);
        lock (cache)
            cache[userId] = (key, message);
        return message;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    /// <summary>{"text":"…"} 에서 한 줄. 허용한 숫자만 · 금지어 없음 · 길이 안이면 그 문장, 아니면 null</summary>
    public static string? Check(string? answer, ISet<string> allowedNumbers)
    {
        if (answer == null) return null;
        int start = answer.IndexOf('{'), end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(answer[start..(end + 1)]);
            string text = doc.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
            if (text.Length is 0 or > MaxLength || text.Contains("http")) return null;
            if (Number().Matches(text).Any(m => !allowedNumbers.Contains(m.Value))) return null;   // 지어낸 숫자
            if (Forbidden.Any(text.Contains)) return null;                                           // 효과 단정 · 의료
            return text;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>AI 를 못 쓸 때의 한 줄</summary>
    public static string Fallback(WeekGoal week, string? day, string? time)
    {
        if (week.MinutesLeft == 0)
            return $"이번 주 목표 {week.GoalMinutes}분을 채웠어요! 이 흐름 그대로 가볍게 걸어요.";
        if (week.WeekWalks == 0)
            return "이번 주 첫 산책을 시작해 볼까요? 공강에 15분이면 충분해요.";
        string habit = day != null ? $" {day} {time}에 자주 걸으셨죠. 그때 한 번 더 걸어 봐요." : " 공강에 짧게 한 번 더 걸어 봐요.";
        return $"이번 주 {week.WeekMinutes}분 걸었어요. 목표까지 {week.MinutesLeft}분 남았어요." + habit;
    }

    /// <summary>테스트용: 캐시 비우기</summary>
    public static void ClearCache() { lock (cache) cache.Clear(); }
}
