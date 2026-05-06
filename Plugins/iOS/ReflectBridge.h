// ReflectBridge.h
// C entry points called from Unity C# via DllImport("__Internal").
// Keep C linkage so symbol names are stable.

#ifndef REFLECT_BRIDGE_H
#define REFLECT_BRIDGE_H

#ifdef __cplusplus
extern "C" {
#endif

void _reflect_initialize(const char* unityReceiver, bool adConsent);
void _reflect_collect_device_info(void);
void _reflect_collect_referral(void);
void _reflect_set_ad_consent(bool granted);
void _reflect_request_att(void);

#ifdef __cplusplus
}
#endif

#endif // REFLECT_BRIDGE_H
