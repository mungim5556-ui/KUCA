namespace KucaMemoServer.Models;

// 건강 (docs/api.md 의 "건강"). 숫자는 서버가 공식으로만 계산하고, AI 는 그 숫자로 말만 한다.

/// <param name="Steps">걸음 수 (휴대폰 걸음 센서 값, 없거나 믿기 어려우면 거리 ÷ 보폭으로 어림)</param>
/// <param name="KcalMin">칼로리 범위 아래 (몸무게를 모르면 50kg 기준)</param>
/// <param name="KcalMax">칼로리 범위 위 (몸무게를 모르면 70kg 기준)</param>
/// <param name="Intensity">light(가벼운 산책) · moderate(보통) · brisk(운동 되는)</param>
/// <param name="IntensityLabel">화면에 쓸 이름</param>
/// <param name="StepsMeasured">true 면 걸음 수가 센서로 잰 값 (산책 끝 응답만), false 면 어림값</param>
public record HealthEstimate(int Steps, int KcalMin, int KcalMax, string Intensity, string IntensityLabel, bool StepsMeasured = false);

/// <summary>코스 난이도 (거리 · 오르막 장소 수로 나눔). Level: easy · normal · hard, Label: 하 · 중 · 상</summary>
public record CourseDifficulty(string Level, string Label, int Hills);

/// <summary>이번 주(월요일 0시, 한국 시각부터) 걸은 분과 목표 (WHO: 일주일 150분)</summary>
public record WeekGoal(int WeekMinutes, int GoalMinutes, int MinutesLeft, int WeekWalks);

/// <param name="CompletesGoal">이 코스를 걸으면 이번 주 목표를 채우는지</param>
/// <param name="Note">코스 카드에 쓸 한 줄 (틀 문장)</param>
public record CourseGoal(int WeekMinutes, int GoalMinutes, int MinutesLeft, bool CompletesGoal, string Note);

/// <summary>GET /api/me/coach: 이번 주 기록을 보고 쓴 한 줄 (AI, 실패하면 틀 문장)</summary>
public record CoachMessage(string Text, string Source, WeekGoal Week, string Note);
