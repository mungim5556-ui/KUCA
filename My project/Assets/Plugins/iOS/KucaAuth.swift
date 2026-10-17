import UIKit
import AuthenticationServices
import KakaoSDKCommon
import KakaoSDKAuth
import KakaoSDKUser

// 로그인 (Unity 에서 [DllImport("__Internal")] 로 부른다).
// - 카카오: 카카오톡이 있으면 카카오톡으로, 없으면 카카오계정(웹)으로 로그인하고 액세스 토큰을 돌려준다.
// - Apple: Sign in with Apple 의 identityToken(JWT)을 돌려준다. (유료 개발자 계정 + 기능 설정이 있어야 동작)
// 돌려준 토큰은 KUCA 서버(POST /api/auth/kakao · /api/auth/apple)가 진짜인지 확인한다.

public typealias KucaLoginCallback = @convention(c) (UnsafePointer<CChar>?, UnsafePointer<CChar>?) -> Void

private func cstr(_ p: UnsafePointer<CChar>?) -> String { p.map { String(cString: $0) } ?? "" }

private func reply(_ cb: KucaLoginCallback?, token: String?, error: String?) {
    DispatchQueue.main.async {
        guard let cb = cb else { return }
        let t = token ?? "", e = error ?? ""
        t.withCString { tp in
            e.withCString { ep in
                cb(token == nil ? nil : tp, error == nil ? nil : ep)
            }
        }
    }
}

private var kakaoInitialized = false

private func ensureKakao(_ appKey: String) {
    if !kakaoInitialized {
        KakaoSDK.initSDK(appKey: appKey)
        kakaoInitialized = true
    }
}

@_cdecl("kuca_kakao_login")
public func kuca_kakao_login(_ appKey: UnsafePointer<CChar>?, _ done: KucaLoginCallback?) {
    ensureKakao(cstr(appKey))
    let handler: (OAuthToken?, Error?) -> Void = { token, error in
        if let token = token {
            reply(done, token: token.accessToken, error: nil)
        } else {
            NSLog("[KucaAuth] kakao login error: %@", String(describing: error))
            if let sdkError = error as? SdkError, sdkError.isClientFailed, case .Cancelled = sdkError.getClientError().reason {
                reply(done, token: nil, error: "cancelled")
            } else {
                reply(done, token: nil, error: "카카오 로그인에 실패했어요: \(error.map { $0.localizedDescription } ?? "알 수 없는 오류")")
            }
        }
    }
    DispatchQueue.main.async {
        if UserApi.isKakaoTalkLoginAvailable() {
            UserApi.shared.loginWithKakaoTalk(completion: handler)
        } else {
            UserApi.shared.loginWithKakaoAccount(completion: handler)
        }
    }
}

/// 카카오톡에서 로그인하고 돌아올 때의 URL 을 카카오 SDK 에 넘긴다 (KucaAppController.mm 이 부른다).
@_cdecl("kuca_handle_open_url")
public func kuca_handle_open_url(_ url: NSURL) -> Bool {
    let u = url as URL
    // openURL 은 메인 스레드에서 불린다
    return MainActor.assumeIsolated {
        AuthApi.isKakaoTalkLoginUrl(u) ? AuthController.handleOpenUrl(url: u) : false
    }
}

// ---------- Sign in with Apple ----------

private final class AppleLoginDelegate: NSObject, ASAuthorizationControllerDelegate, ASAuthorizationControllerPresentationContextProviding {
    let done: KucaLoginCallback?
    init(_ done: KucaLoginCallback?) { self.done = done }

    func authorizationController(controller: ASAuthorizationController, didCompleteWithAuthorization authorization: ASAuthorization) {
        if let cred = authorization.credential as? ASAuthorizationAppleIDCredential,
           let data = cred.identityToken, let jwt = String(data: data, encoding: .utf8) {
            reply(done, token: jwt, error: nil)
        } else {
            reply(done, token: nil, error: "Apple 로그인 정보를 받지 못했어요")
        }
        appleDelegate = nil
    }

    func authorizationController(controller: ASAuthorizationController, didCompleteWithError error: Error) {
        let canceled = (error as? ASAuthorizationError)?.code == .canceled
        reply(done, token: nil, error: canceled ? "cancelled" : "Apple 로그인에 실패했어요: \(error.localizedDescription)")
        appleDelegate = nil
    }

    func presentationAnchor(for controller: ASAuthorizationController) -> ASPresentationAnchor {
        UIApplication.shared.connectedScenes.compactMap { ($0 as? UIWindowScene)?.keyWindow }.first ?? ASPresentationAnchor()
    }
}

private var appleDelegate: AppleLoginDelegate?

@_cdecl("kuca_apple_login")
public func kuca_apple_login(_ done: KucaLoginCallback?) {
    DispatchQueue.main.async {
        let request = ASAuthorizationAppleIDProvider().createRequest()
        request.requestedScopes = [] // 이름·메일은 받지 않는다 (경희대 메일은 따로 인증)
        let controller = ASAuthorizationController(authorizationRequests: [request])
        let delegate = AppleLoginDelegate(done)
        appleDelegate = delegate
        controller.delegate = delegate
        controller.presentationContextProvider = delegate
        controller.performRequests()
    }
}
