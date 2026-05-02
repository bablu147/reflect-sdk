# Android Gradle setup for Reflect

The Java sources in this folder depend on two Google libraries that must be
added to Unity's generated Gradle project.

## Option A — enable Unity's `mainTemplate.gradle` (recommended)

1. In Unity: **Edit → Project Settings → Player → Android → Publishing Settings**
   → tick **Custom Main Gradle Template**.
2. Unity creates `Assets/Plugins/Android/mainTemplate.gradle`.
3. Inside the `dependencies { ... }` block add:

```gradle
dependencies {
    implementation 'com.google.android.gms:play-services-ads-identifier:18.0.1'
    implementation 'com.android.installreferrer:installreferrer:2.2'
    // ... keep any existing lines e.g. **DEPS**
}
```

## Option B — use the Unity Resolver (External Dependency Manager)

If you have **External Dependency Manager for Unity (EDM4U)** installed:

1. Create `Assets/Reflect/Editor/ReflectDependencies.xml` with:

```xml
<dependencies>
    <androidPackages>
        <androidPackage spec="com.google.android.gms:play-services-ads-identifier:18.0.1"/>
        <androidPackage spec="com.android.installreferrer:installreferrer:2.2"/>
    </androidPackages>
</dependencies>
```

2. Run **Assets → External Dependency Manager → Android Resolver → Resolve**.

## Minimum settings

- `minSdkVersion` **21** or higher
- `compileSdkVersion` **33** or higher
- `targetSdkVersion` **33+** (required by Play Store in 2024+)
- Enable **AndroidX** (Player Settings → Android → Publishing Settings).
