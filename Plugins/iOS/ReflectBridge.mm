// ReflectBridge.mm
// Objective-C++ bridge for the Reflect SDK on iOS.
// Collects device + referral data, manages ATT prompt, and delivers JSON
// payloads back to Unity via UnitySendMessage.

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <sys/utsname.h>
#import <sys/sysctl.h>
#import <ifaddrs.h>
#import <net/if.h>

#import <AdSupport/AdSupport.h>

// AppTrackingTransparency is only available iOS 14+ at runtime.
#if __has_include(<AppTrackingTransparency/AppTrackingTransparency.h>)
#import <AppTrackingTransparency/AppTrackingTransparency.h>
#define REFLECT_HAS_ATT 1
#endif

// AdServices is iOS 14.3+ — attribution token for Apple Search Ads.
#if __has_include(<AdServices/AdServices.h>)
#import <AdServices/AdServices.h>
#define REFLECT_HAS_ADSERVICES 1
#endif

// NOTE: AdAttributionKit (iOS 17.4+) is a SWIFT-ONLY framework with no Objective-C
// API, so it cannot be driven from this .mm — SKAdNetwork below handles all conversion
// value updates (its updatePostbackConversionValue: remains valid on iOS 17.4+).

// SKAdNetwork — iOS 11.3+ for updateConversionValue, iOS 15.4+ for
// updatePostbackConversionValue. Used as fallback when AdAttributionKit unavailable.
#if __has_include(<StoreKit/SKAdNetwork.h>)
#import <StoreKit/SKAdNetwork.h>
#define REFLECT_HAS_SKADNETWORK 1
#endif

#import "ReflectBridge.h"

// Unity's UnitySendMessage shim. Declared extern so we don't need the Unity headers.
extern "C" void UnitySendMessage(const char* obj, const char* method, const char* msg);

// ─── Module-level state ─────────────────────────────────────────────────
static NSString* gReceiver    = nil;
static BOOL      gAdConsent   = YES;

static char* reflect_strdup(NSString* s) {
    if (s == nil) s = @"";
    const char* c = [s UTF8String];
    size_t len = strlen(c) + 1;
    char* out = (char*)malloc(len);
    memcpy(out, c, len);
    return out;
}

static void SendToUnity(NSString* method, NSString* payload) {
    if (gReceiver == nil || method == nil) return;
    const char* obj = [gReceiver UTF8String];
    const char* m   = [method  UTF8String];
    const char* msg = payload ? [payload UTF8String] : "";
    UnitySendMessage(obj, m, msg);
}

static NSString* JsonStringFrom(NSDictionary* d) {
    if (d == nil) return @"{}";
    NSError* err = nil;
    NSData* data = [NSJSONSerialization dataWithJSONObject:d options:0 error:&err];
    if (err || data == nil) return @"{}";
    return [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
}

// ─── Helpers ────────────────────────────────────────────────────────────
static NSString* HardwareModel(void) {
    struct utsname sys;
    uname(&sys);
    return [NSString stringWithCString:sys.machine encoding:NSUTF8StringEncoding];
}

static long long TotalRamMB(void) {
    return (long long)([[NSProcessInfo processInfo] physicalMemory] / (1024 * 1024));
}

static BOOL IsJailbroken(void) {
    NSArray<NSString*>* suspects = @[
        @"/Applications/Cydia.app", @"/Library/MobileSubstrate/MobileSubstrate.dylib",
        @"/bin/bash", @"/usr/sbin/sshd", @"/etc/apt", @"/private/var/lib/apt/"
    ];
    NSFileManager* fm = [NSFileManager defaultManager];
    for (NSString* p in suspects) {
        if ([fm fileExistsAtPath:p]) return YES;
    }
    FILE* f = fopen("/private/var/mobile/Library/Preferences/.GlobalPreferences.plist", "r");
    if (f == NULL) {
        // Sandboxed apps can't open that — which is the correct behavior.
        return NO;
    }
    fclose(f);
    return YES;
}

static BOOL IsRunningOnSimulator(void) {
#if TARGET_IPHONE_SIMULATOR
    return YES;
#else
    return NO;
#endif
}

// ─── ATT ────────────────────────────────────────────────────────────────
// Connectivity via active network interfaces (no extra framework / no async wait):
// en* = Wi-Fi/wired, pdp_ip* = cellular. Matches the Android connection_type field
// instead of the previous hardcoded "unknown".
static NSString* ReflectConnectionType(void) {
    struct ifaddrs *addrs = NULL;
    BOOL wifi = NO, cell = NO;
    if (getifaddrs(&addrs) == 0) {
        for (struct ifaddrs *a = addrs; a != NULL; a = a->ifa_next) {
            if (a->ifa_addr == NULL) continue;
            if (!(a->ifa_flags & IFF_UP) || !(a->ifa_flags & IFF_RUNNING)) continue;
            if (a->ifa_flags & IFF_LOOPBACK) continue;
            sa_family_t fam = a->ifa_addr->sa_family;
            if (fam != AF_INET && fam != AF_INET6) continue;
            NSString *name = [NSString stringWithUTF8String:a->ifa_name];
            if ([name hasPrefix:@"en"])           wifi = YES;   // Wi-Fi (or wired)
            else if ([name hasPrefix:@"pdp_ip"])  cell = YES;   // cellular
        }
        freeifaddrs(addrs);
    }
    if (wifi) return @"wifi";
    if (cell) return @"cellular";
    return @"none";
}

// VPN detection via tunnelling interfaces (ppp/ipsec/tap/tun). utun* is deliberately
// excluded — iOS creates utun interfaces for non-VPN system services (e.g. iCloud
// Private Relay), so flagging them would be a false positive.
static BOOL ReflectVpnDetected(void) {
    struct ifaddrs *addrs = NULL;
    BOOL vpn = NO;
    if (getifaddrs(&addrs) == 0) {
        for (struct ifaddrs *a = addrs; a != NULL && !vpn; a = a->ifa_next) {
            if (a->ifa_name == NULL) continue;
            if (!(a->ifa_flags & IFF_UP) || !(a->ifa_flags & IFF_RUNNING)) continue;
            NSString *name = [NSString stringWithUTF8String:a->ifa_name];
            if ([name hasPrefix:@"ppp"] || [name hasPrefix:@"ipsec"] ||
                [name hasPrefix:@"tap"] || [name hasPrefix:@"tun"]) {
                vpn = YES;
            }
        }
        freeifaddrs(addrs);
    }
    return vpn;
}

static int CurrentAttStatus(void) {
#if REFLECT_HAS_ATT
    if (@available(iOS 14, *)) {
        return (int)[ATTrackingManager trackingAuthorizationStatus];
    }
#endif
    return 99; // Unavailable
}

static NSString* ReadIdfaIfAllowed(void) {
#if REFLECT_HAS_ATT
    if (@available(iOS 14, *)) {
        if ([ATTrackingManager trackingAuthorizationStatus] != ATTrackingManagerAuthorizationStatusAuthorized)
            return nil;
    }
#endif
    if (!gAdConsent) return nil;
    NSUUID* idfa = [[ASIdentifierManager sharedManager] advertisingIdentifier];
    NSString* str = [idfa UUIDString];
    if ([str isEqualToString:@"00000000-0000-0000-0000-000000000000"]) return nil;
    return str;
}

// ─── AdServices attribution token ───────────────────────────────────────
static NSString* ReadAttributionToken(void) {
#if REFLECT_HAS_ADSERVICES
    if (@available(iOS 14.3, *)) {
        NSError* err = nil;
        NSString* token = [AAAttribution attributionTokenWithError:&err];
        if (err == nil && token.length > 0) return token;
    }
#endif
    return nil;
}

// ─── Exported C entry points ────────────────────────────────────────────
extern "C" {

void _reflect_initialize(const char* unityReceiver, bool adConsent) {
    gReceiver  = unityReceiver ? [NSString stringWithUTF8String:unityReceiver] : nil;
    gAdConsent = adConsent ? YES : NO;
    NSLog(@"[Reflect] initialized receiver=%@ adConsent=%d", gReceiver, gAdConsent);
}

void _reflect_set_ad_consent(bool granted) {
    gAdConsent = granted ? YES : NO;
}

void _reflect_collect_device_info(void) {
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        NSMutableDictionary* d = [NSMutableDictionary dictionary];

        d[@"os"]                 = @"iOS";
        d[@"os_version"]         = [[UIDevice currentDevice] systemVersion] ?: @"";
        d[@"api_level"]          = @0;
        d[@"device_model"]       = HardwareModel() ?: @"";
        d[@"device_manufacturer"]= @"Apple";
        // Adjust parity: device_type. UIUserInterfaceIdiom → phone | tablet | tv.
        switch ([[UIDevice currentDevice] userInterfaceIdiom]) {
            case UIUserInterfaceIdiomPad:  d[@"device_type"] = @"tablet"; break;
            case UIUserInterfaceIdiomTV:   d[@"device_type"] = @"tv";     break;
            default:                       d[@"device_type"] = @"phone";  break;
        }
        d[@"device_brand"]       = @"Apple";
        d[@"cpu_arch"]           = @(
#if defined(__arm64__) || defined(__aarch64__)
            "arm64"
#elif defined(__x86_64__)
            "x86_64"
#else
            "unknown"
#endif
        );

        CGRect bounds = [[UIScreen mainScreen] nativeBounds];
        d[@"screen_width"]   = @((int)bounds.size.width);
        d[@"screen_height"]  = @((int)bounds.size.height);
        d[@"screen_density"] = @((int)([[UIScreen mainScreen] nativeScale] * 160));
        d[@"total_ram_mb"]   = @(TotalRamMB());

        NSBundle* b = [NSBundle mainBundle];
        d[@"app_bundle_id"]     = b.bundleIdentifier ?: @"";
        d[@"app_version"]       = [b objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"";
        NSString* bundleVer = [b objectForInfoDictionaryKey:@"CFBundleVersion"];
        long long vcode = bundleVer ? [bundleVer longLongValue] : 0;
        if (vcode == 0 && bundleVer.length > 0) {
            // Non-numeric CFBundleVersion (e.g. "build-42") → keep only digits so we
            // don't silently report 0 for the build number.
            NSString* digits = [[bundleVer componentsSeparatedByCharactersInSet:
                [[NSCharacterSet decimalDigitCharacterSet] invertedSet]] componentsJoinedByString:@""];
            if (digits.length > 0) vcode = [digits longLongValue];
        }
        d[@"app_version_code"]  = @(vcode);
        d[@"install_source"]    = @"ios_app_store";
        d[@"first_install_time"]= @0; // iOS doesn't expose this directly.
        d[@"last_update_time"]  = @0;

        NSLocale* loc = [NSLocale currentLocale];
        d[@"language"]      = [loc objectForKey:NSLocaleLanguageCode] ?: @"";
        d[@"locale"]        = loc.localeIdentifier ?: @"";
        NSTimeZone* tz      = [NSTimeZone localTimeZone];
        d[@"timezone"]      = tz.name ?: @"";
        d[@"tz_offset_min"] = @((int)(tz.secondsFromGMT / 60));

        d[@"connection_type"] = ReflectConnectionType();   // wifi | cellular | none
        // Apple no longer provides reliable carrier/MCC/MNC on modern iOS — leave null.
        d[@"carrier"]         = [NSNull null];
        d[@"carrier_mcc"]     = [NSNull null];
        d[@"carrier_mnc"]     = [NSNull null];

        d[@"is_emulator"]          = @(IsRunningOnSimulator());
        d[@"is_rooted"]            = @(IsJailbroken());
        d[@"vpn_detected"]         = @(ReflectVpnDetected());
        // iOS cannot determine mock-location — send null (not a misleading false) so
        // the backend can tell "clean" from "not measured".
        d[@"mock_location_enabled"]= [NSNull null];

        d[@"idfv"]  = [[[UIDevice currentDevice] identifierForVendor] UUIDString] ?: @"";
        // Raw ATT authorization status (0 notDetermined, 1 restricted, 2 denied,
        // 3 authorized) — preserves the discrete state instead of collapsing it into
        // lat_enabled, which conflated "denied" with "not yet prompted".
        int attStatus = CurrentAttStatus();
        d[@"att_status"] = @(attStatus);
        NSString* idfa = ReadIdfaIfAllowed();
        if (idfa) d[@"idfa"] = idfa;
        // lat_enabled only when tracking is genuinely limited/denied (status 2),
        // not when the prompt simply hasn't been shown yet (status 0).
        d[@"lat_enabled"] = @(idfa == nil && attStatus != 0);

        NSString* json = JsonStringFrom(d);
        dispatch_async(dispatch_get_main_queue(), ^{
            SendToUnity(@"OnDeviceInfoJson", json);
        });
    });
}

void _reflect_collect_referral(void) {
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY, 0), ^{
        NSMutableDictionary* d = [NSMutableDictionary dictionary];
        d[@"source"]      = @"ios_adservices";
        d[@"click_ts"]    = @0;
        d[@"install_ts"]  = @0;

        NSString* token = ReadAttributionToken();
        if (token) d[@"attribution_token"] = token;

        NSString* json = JsonStringFrom(d);
        dispatch_async(dispatch_get_main_queue(), ^{
            SendToUnity(@"OnReferralJson", json);
        });
    });
}

void _reflect_request_att(void) {
#if REFLECT_HAS_ATT
    if (@available(iOS 14, *)) {
        [ATTrackingManager requestTrackingAuthorizationWithCompletionHandler:
            ^(ATTrackingManagerAuthorizationStatus status) {
                NSString* payload = [NSString stringWithFormat:@"%lu", (unsigned long)status];
                SendToUnity(@"OnAttStatusCode", payload);
            }];
        return;
    }
#endif
    SendToUnity(@"OnAttStatusCode", @"99"); // Unavailable on pre-iOS 14.
}

// ─── SKAdNetwork conversion value update ─────────────────────────────
// Priority: SKAdNetwork 4.0 (iOS 16.1+, coarse+lock) > legacy (fine only)
//         > SKAdNetwork legacy (iOS 15.4+) > SKAdNetwork 11.3+ (fine only).
//
// coarseValue: "low", "medium", "high", or empty string (no coarse).
// lockWindow:  if true, locks the current postback window immediately.
//
// Result delivered via OnSkanCvUpdateResult: "ok" or "error:<message>".

void _reflect_update_conversion_value(int fineValue, const char* coarseValue, bool lockWindow) {
    NSString* coarse = coarseValue ? [NSString stringWithUTF8String:coarseValue] : @"";

    // NOTE: AdAttributionKit (iOS 17.4+) is a Swift-only framework — it exposes NO
    // Objective-C conversion-value API, so it cannot be driven from this .mm. SKAdNetwork's
    // updatePostbackConversionValue: remains valid on iOS 17.4+ and is used for all SKAN
    // conversion-value updates below. (A prior AdAttributionKit ObjC block here referenced
    // non-existent symbols and broke the entire iOS build on any modern Xcode.)

#if REFLECT_HAS_SKADNETWORK
    // SKAdNetwork 4.0 (iOS 16.1+) — updatePostbackConversionValue:coarseValue:lockWindow:
    if (@available(iOS 16.1, *)) {
        SKAdNetworkCoarseConversionValue coarseCV = nil;
        if ([coarse isEqualToString:@"high"])   coarseCV = SKAdNetworkCoarseConversionValueHigh;
        else if ([coarse isEqualToString:@"medium"]) coarseCV = SKAdNetworkCoarseConversionValueMedium;
        else if ([coarse isEqualToString:@"low"])    coarseCV = SKAdNetworkCoarseConversionValueLow;

        [SKAdNetwork updatePostbackConversionValue:fineValue
                                coarseValue:coarseCV
                                 lockWindow:lockWindow
                          completionHandler:^(NSError* _Nullable err) {
            if (err) {
                NSString* msg = [NSString stringWithFormat:@"error:%@", err.localizedDescription];
                dispatch_async(dispatch_get_main_queue(), ^{ SendToUnity(@"OnSkanCvUpdateResult", msg); });
            } else {
                dispatch_async(dispatch_get_main_queue(), ^{ SendToUnity(@"OnSkanCvUpdateResult", @"ok"); });
            }
        }];
        return;
    }

    // SKAdNetwork legacy (iOS 15.4+) — updatePostbackConversionValue: (fine only, no coarse/lock)
    if (@available(iOS 15.4, *)) {
        [SKAdNetwork updatePostbackConversionValue:fineValue completionHandler:^(NSError* _Nullable err) {
            if (err) {
                NSString* msg = [NSString stringWithFormat:@"error:%@", err.localizedDescription];
                dispatch_async(dispatch_get_main_queue(), ^{ SendToUnity(@"OnSkanCvUpdateResult", msg); });
            } else {
                dispatch_async(dispatch_get_main_queue(), ^{ SendToUnity(@"OnSkanCvUpdateResult", @"ok"); });
            }
        }];
        return;
    }

    // SKAdNetwork 2.0 (iOS 11.3+) — deprecated updateConversionValue: (no completion handler)
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    if (@available(iOS 11.3, *)) {
        [SKAdNetwork updateConversionValue:fineValue];
        dispatch_async(dispatch_get_main_queue(), ^{ SendToUnity(@"OnSkanCvUpdateResult", @"ok"); });
        return;
    }
#pragma clang diagnostic pop
#endif

    // No SKAN API available.
    dispatch_async(dispatch_get_main_queue(), ^{ SendToUnity(@"OnSkanCvUpdateResult", @"error:unsupported"); });
}

} // extern "C"
