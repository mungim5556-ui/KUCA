// 앱이 URL 로 열릴 때(카카오톡 로그인에서 돌아올 때) 카카오 SDK 가 받게 한다.
// Unity 6 은 UIScene(UnityScene) 을 써서 URL 이 application:openURL: 이 아니라 scene:openURLContexts: 로 들어오고,
// UnityScene 은 그 URL 을 kUnityOnOpenURL 알림으로 뿌린다. 그래서 두 길을 모두 받는다.
#import "UnityAppController.h"
#import "PluginBase/AppDelegateListener.h"

extern "C" bool kuca_handle_open_url(NSURL* url);

@interface KucaAppController : UnityAppController
@end

@implementation KucaAppController

// UIScene 을 쓰지 않는 경우의 길
- (BOOL)application:(UIApplication*)app openURL:(NSURL*)url options:(NSDictionary<UIApplicationOpenURLOptionsKey, id>*)options
{
    if (kuca_handle_open_url(url))
        return YES;
    return [super application:app openURL:url options:options];
}

@end

IMPL_APP_CONTROLLER_SUBCLASS(KucaAppController)

// UIScene 을 쓰는 경우의 길 (Unity 6 기본): UnityScene 이 보내는 kUnityOnOpenURL 알림
@interface KucaOpenURLListener : NSObject
@end

@implementation KucaOpenURLListener

+ (void)load
{
    static KucaOpenURLListener* listener = nil;
    listener = [KucaOpenURLListener new];
    [[NSNotificationCenter defaultCenter] addObserver:listener selector:@selector(onOpenURL:) name:kUnityOnOpenURL object:nil];
}

- (void)onOpenURL:(NSNotification*)notification
{
    NSURL* url = notification.userInfo[@"url"];
    if (url != nil)
    {
        bool handled = kuca_handle_open_url(url);
        NSLog(@"[KucaAuth] open url %@ handled=%d", url.scheme, handled ? 1 : 0);
    }
}

@end
