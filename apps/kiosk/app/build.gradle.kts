import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val localProperties = Properties().apply {
    val localPropertiesFile = rootProject.file("local.properties")
    if (localPropertiesFile.exists()) {
        localPropertiesFile.inputStream().use { load(it) }
    }
}

android {
    namespace = "family.landis.aeriekiosk"
    compileSdk = 34

    defaultConfig {
        applicationId = "family.landis.aeriekiosk"
        // 28+ so DevicePolicyManager#setLockTaskFeatures is always available —
        // it's central to how this app locks the tablet down, not optional.
        minSdk = 28
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"

        // The bare geckoview-omni artifact bundles all 4 ABIs; the kiosk
        // tablets are budget ARM devices, so x86/x86_64 just add dead weight
        // to the APK. Add them back if this ever needs to run on an emulator.
        ndk {
            abiFilters += listOf("armeabi-v7a", "arm64-v8a")
        }
    }

    signingConfigs {
        // Only defined when local.properties supplies the release keystore,
        // so debug builds and CI checkouts without the keystore still work.
        if (localProperties.getProperty("RELEASE_STORE_FILE") != null) {
            create("release") {
                storeFile = file(localProperties.getProperty("RELEASE_STORE_FILE"))
                storePassword = localProperties.getProperty("RELEASE_STORE_PASSWORD")
                keyAlias = localProperties.getProperty("RELEASE_KEY_ALIAS")
                keyPassword = localProperties.getProperty("RELEASE_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (localProperties.getProperty("RELEASE_STORE_FILE") != null) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // Newer androidx.core releases require compileSdk 37 / AGP 9.1+; staying on
    // compileSdk 34 (max recommended for AGP 8.5.2) pins these accordingly.
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.1")

    // Firefox's rendering engine, embedded standalone. The kiosk tablets run
    // Android 9 with a WebView stuck on Chrome 74 (2019, predates ?./?? syntax
    // support), which left the dashboard's bundle unable to parse. GeckoView
    // ships and updates its own engine, independent of the OS/WebView.
    //
    // Pinned to 133 (Dec 2024) rather than the current release: GeckoView
    // 134+ bumps its androidx.core/media3 transitive deps to versions that
    // require compileSdk 35+ and AGP 8.6+, which this project deliberately
    // stays below (see compileSdk comment above). Bump this only alongside
    // an AGP/compileSdk upgrade — check compatibility with
    // ./gradlew :app:checkDebugAarMetadata before raising it, versions at
    // https://maven.mozilla.org/maven2/org/mozilla/geckoview/geckoview-omni/maven-metadata.xml
    implementation("org.mozilla.geckoview:geckoview-omni:133.0.20241209150345")
}
