#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

/// <summary>
/// iOS 빌드 뒤 Xcode 프로젝트와 Info.plist 에 Unity 설정 화면에 없는 항목을 넣는다.
/// - NSLocalNetworkUsageDescription: 같은 와이파이의 PC 메모 서버(http://192.168.x.x:5080)에 붙으려면 iOS 14+ 의 로컬 네트워크 권한 안내 문구가 필요하다.
///   (http 주소 허용은 Player Settings 의 Allow downloads over HTTP = Always allowed 로 한다.)
/// - 공유(Assets/Plugins/iOS/KucaShare.swift): 카카오 SDK(Swift Package)를 UnityFramework 에 붙이고,
///   카카오톡·인스타그램 앱을 열 수 있게 URL 스킴을 등록한다.
/// </summary>
public static class IosBuildSettings
{
    const string KakaoSdkUrl = "https://github.com/kakao/kakao-ios-sdk";
    const string KakaoSdkVersion = "2.29.0";

    [PostProcessBuild(100)]
    public static void OnPostprocessBuild(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS)
            return;

        string projPath = PBXProject.GetPBXProjectPath(path);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);
        string framework = proj.GetUnityFrameworkTargetGuid();
        proj.SetBuildProperty(framework, "SWIFT_VERSION", "5.0");
        proj.SetBuildProperty(framework, "CLANG_ENABLE_MODULES", "YES");
        string kakao = proj.AddRemotePackageReferenceAtVersionUpToNextMajor(KakaoSdkUrl, KakaoSdkVersion);
        foreach (string product in new[] { "KakaoSDKCommon", "KakaoSDKShare", "KakaoSDKTemplate", "KakaoSDKAuth", "KakaoSDKUser" })
            proj.AddRemotePackageFrameworkToProject(framework, product, kakao, false);
        proj.WriteToFile(projPath);

        // Sign in with Apple: 유료 개발자 계정이 있을 때만 켠다 (무료 계정은 이 기능을 넣으면 서명이 실패한다).
        if (AuthConfig.AppleLoginEnabled)
        {
            var capabilities = new ProjectCapabilityManager(projPath, "Unity-iPhone/KUCA.entitlements", null, proj.GetUnityMainTargetGuid());
            capabilities.AddSignInWithApple();
            capabilities.WriteToFile();
        }

        string plistPath = Path.Combine(path, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        PlistElementDict root = plist.root;
        root.SetString("NSLocalNetworkUsageDescription", "같은 와이파이에 있는 KUCA 메모 서버에 연결해 장소 메모와 사진을 주고받기 위해 사용합니다.");

        PlistElementArray schemes = root["LSApplicationQueriesSchemes"] as PlistElementArray ?? root.CreateArray("LSApplicationQueriesSchemes");
        foreach (string s in new[] { "kakaokompassauth", "kakaolink", "kakaoplus", "kakaotalk", "instagram", "instagram-stories" })
            schemes.AddString(s);

        PlistElementArray urlTypes = root["CFBundleURLTypes"] as PlistElementArray ?? root.CreateArray("CFBundleURLTypes");
        PlistElementDict kakaoType = urlTypes.AddDict();
        kakaoType.SetString("CFBundleURLName", "kakao");
        kakaoType.CreateArray("CFBundleURLSchemes").AddString("kakao" + ShareConfig.KakaoNativeAppKey);
        plist.WriteToFile(plistPath);
    }
}
#endif
