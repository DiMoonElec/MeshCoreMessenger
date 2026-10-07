#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>
#import <stdatomic.h>

typedef void (*McmClick)(const char *token);
typedef void (*McmResult)(int status);
static _Atomic(McmClick) clickCallback;
static id delegateOwner;
static atomic_bool stopped;
static NSMutableSet<NSString *> *inFlight;
static NSMutableSet<NSString *> *cancelled;

static int statusValue(UNNotificationSettings *settings) {
    switch (settings.authorizationStatus) {
        case UNAuthorizationStatusAuthorized: case UNAuthorizationStatusProvisional: return 1;
        case UNAuthorizationStatusDenied: return 2;
        default: return 0;
    }
}
@interface McmNotificationDelegate : NSObject<UNUserNotificationCenterDelegate> @end
@implementation McmNotificationDelegate
- (void)userNotificationCenter:(UNUserNotificationCenter *)center didReceiveNotificationResponse:(UNNotificationResponse *)response withCompletionHandler:(void (^)(void))completion {
    NSString *token = response.notification.request.content.userInfo[@"mcm"];
    if ([token isKindOfClass:NSString.class]) dispatch_async(dispatch_get_main_queue(), ^{
        McmClick callback = atomic_load(&clickCallback);
        if (!stopped && callback) callback(token.UTF8String);
    });
    completion();
}
- (void)userNotificationCenter:(UNUserNotificationCenter *)center willPresentNotification:(UNNotification *)notification withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completion {
    completion(stopped ? UNNotificationPresentationOptionNone : UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList);
}
@end
__attribute__((visibility("default"))) int mcm_notifications_init(McmClick callback) {
    // UNUserNotificationCenter requires a real app bundle. A dotnet CLI host is unsupported.
    if (![NSBundle.mainBundle.bundleIdentifier length] || ![NSBundle.mainBundle.bundlePath hasSuffix:@".app"]) return 0;
    stopped = NO; clickCallback = callback;
    inFlight = [NSMutableSet new]; cancelled = [NSMutableSet new];
    delegateOwner = [McmNotificationDelegate new];
    UNUserNotificationCenter.currentNotificationCenter.delegate = delegateOwner;
    return 1;
}
__attribute__((visibility("default"))) void mcm_notifications_status(McmResult result) {
    [UNUserNotificationCenter.currentNotificationCenter getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings *settings) { result(statusValue(settings)); }];
}
__attribute__((visibility("default"))) void mcm_notifications_authorize(McmResult result) {
    [UNUserNotificationCenter.currentNotificationCenter requestAuthorizationWithOptions:UNAuthorizationOptionAlert completionHandler:^(BOOL granted, NSError *error) {
        result(error ? 3 : (granted ? 1 : 2));
    }];
}
__attribute__((visibility("default"))) void mcm_notifications_show(const char *token, const char *title, const char *body, McmResult result) {
    if (stopped) { result(3); return; }
    NSString *identifier = [NSString stringWithUTF8String:token];
    @synchronized(inFlight) { [inFlight addObject:identifier]; }
    UNMutableNotificationContent *content = [UNMutableNotificationContent new];
    content.title = [NSString stringWithUTF8String:title]; content.body = [NSString stringWithUTF8String:body];
    content.userInfo = @{@"mcm": identifier};
    UNNotificationRequest *request = [UNNotificationRequest requestWithIdentifier:identifier content:content trigger:nil];
    [UNUserNotificationCenter.currentNotificationCenter addNotificationRequest:request withCompletionHandler:^(NSError *error) {
        BOOL remove;
        @synchronized(inFlight) {
            remove = stopped || [cancelled containsObject:identifier];
            [inFlight removeObject:identifier]; [cancelled removeObject:identifier];
        }
        if (remove) {
            [UNUserNotificationCenter.currentNotificationCenter removePendingNotificationRequestsWithIdentifiers:@[identifier]];
            [UNUserNotificationCenter.currentNotificationCenter removeDeliveredNotificationsWithIdentifiers:@[identifier]];
        }
        result(error ? 3 : 1);
    }];
}
__attribute__((visibility("default"))) void mcm_notifications_remove(const char *token) {
    NSString *identifier = [NSString stringWithUTF8String:token];
    @synchronized(inFlight) { if ([inFlight containsObject:identifier]) [cancelled addObject:identifier]; }
    [UNUserNotificationCenter.currentNotificationCenter removePendingNotificationRequestsWithIdentifiers:@[identifier]];
    [UNUserNotificationCenter.currentNotificationCenter removeDeliveredNotificationsWithIdentifiers:@[identifier]];
}
__attribute__((visibility("default"))) void mcm_notifications_stop(void) {
    stopped = YES; clickCallback = NULL;
    UNUserNotificationCenter.currentNotificationCenter.delegate = nil;
    delegateOwner = nil;
}
