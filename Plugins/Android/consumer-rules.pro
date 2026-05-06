# Reflect SDK — consumer ProGuard / R8 rules.
#
# Embedded with the Android plugin so apps with code-shrinking enabled
# (which is the default on release builds) don't strip the native bridge
# methods invoked from C# via UnityPlayer + reflection.
#
# Add to your build.gradle if your build system doesn't auto-pick consumer
# rules from plugins/aar:
#
#   android { buildTypes { release { consumerProguardFiles 'consumer-rules.pro' } } }

# Keep all SDK classes — they're called from C# via JNI / reflection.
-keep class com.reflect.sdk.** { *; }
-keepclassmembers class com.reflect.sdk.** { *; }

# UnityPlayer.UnitySendMessage — used by callbacks back to C#.
-keep class com.unity3d.player.UnityPlayer { *; }

# Google Play Install Referrer library — keep its public surface so the
# async client connection callback isn't stripped.
-keep class com.android.installreferrer.** { *; }
-keepclasseswithmembernames class * { @com.android.installreferrer.** *; }

# Google Play Services Ads Identifier (for GAID).
-keep class com.google.android.gms.ads.identifier.** { *; }
