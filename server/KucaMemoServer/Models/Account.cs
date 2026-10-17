using KucaMemoServer.Services;

namespace KucaMemoServer.Models;

// 계정 (docs/api.md 의 "로그인 · 계정"). JSON 은 camelCase.

/// <summary>앱에 돌려주는 내 계정 정보</summary>
public record Account(
    string Id,
    string Provider,          // "kakao" · "apple" · "dev" · "guest"(체험 계정)
    string? Nickname,
    string? KhuEmail,         // 인증을 마친 경희대 메일 (없으면 null)
    bool EmailVerified,
    string? PreferencesJson,  // 산책 취향 체크리스트 (앱의 WalkPreferences JSON 그대로)
    DateTime CreatedAt,
    string? Gender = null)    // "male" · "female" (매칭 "동성만" 조건에만 씀, 다른 사람에게 보이지 않음)
{
    /// <summary>로그인 없이 둘러보는 체험 계정 (메일 인증 없음, 일정 기간 뒤 자동 삭제)</summary>
    public bool IsGuest => Provider == AccountStore.GuestProvider;

    /// <summary>앱을 쓰려면 경희대 메일 인증(체험 계정은 면제) · 닉네임 · 성별이 있어야 한다</summary>
    public bool Ready => (EmailVerified || IsGuest) && !string.IsNullOrWhiteSpace(Nickname) && Gender != null;
}

public record LoginResult(string Token, Account User);

public record KakaoLoginRequest(string? AccessToken);
public record AppleLoginRequest(string? IdentityToken);
public record DevLoginRequest(string? Name);
public record NicknameRequest(string? Nickname);
public record GenderRequest(string? Gender);
public record EmailStartRequest(string? Email);
public record EmailVerifyRequest(string? Code);
