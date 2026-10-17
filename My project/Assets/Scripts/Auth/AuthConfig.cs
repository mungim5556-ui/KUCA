/// <summary>로그인 설정</summary>
public static class AuthConfig
{
    /// <summary>
    /// Sign in with Apple. 유료 Apple Developer 계정이 있어야 동작한다 (무료 계정은 이 기능을 켜면 서명이 실패).
    /// 유료 계정이 생기면 true 로 바꾸면 버튼과 Xcode 기능(entitlement)이 함께 켜진다.
    /// </summary>
    public const bool AppleLoginEnabled = false;

    /// <summary>로그인할 수 있는 학교 메일 도메인</summary>
    public const string SchoolDomain = "khu.ac.kr";
}
