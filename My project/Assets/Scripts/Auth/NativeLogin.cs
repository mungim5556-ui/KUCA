using System;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
using AOT;
#endif

/// <summary>
/// 카카오 / Apple 로그인 (iOS 네이티브 Assets/Plugins/iOS/KucaAuth.swift).
/// done(토큰, 에러): 성공하면 토큰, 실패하면 에러 문구, 사용자가 취소하면 에러 "cancelled".
/// </summary>
public static class NativeLogin
{
    public const string Cancelled = "cancelled";

#if UNITY_IOS && !UNITY_EDITOR
    delegate void Callback(IntPtr token, IntPtr error);
    [DllImport("__Internal")] static extern void kuca_kakao_login(string appKey, Callback done);
    [DllImport("__Internal")] static extern void kuca_apple_login(Callback done);

    static Action<string, string> pending;

    [MonoPInvokeCallback(typeof(Callback))]
    static void OnDone(IntPtr token, IntPtr error)
    {
        Action<string, string> cb = pending;
        pending = null;
        cb?.Invoke(token == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(token), error == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(error));
    }
#endif

    public static bool Supported =>
#if UNITY_IOS && !UNITY_EDITOR
        true;
#else
        false;
#endif

    public static void Kakao(Action<string, string> done)
    {
#if UNITY_IOS && !UNITY_EDITOR
        pending = done;
        kuca_kakao_login(ShareConfig.KakaoNativeAppKey, OnDone);
#else
        done(null, "카카오 로그인은 아이폰에서 할 수 있어요");
#endif
    }

    public static void Apple(Action<string, string> done)
    {
#if UNITY_IOS && !UNITY_EDITOR
        pending = done;
        kuca_apple_login(OnDone);
#else
        done(null, "Apple 로그인은 아이폰에서 할 수 있어요");
#endif
    }
}
