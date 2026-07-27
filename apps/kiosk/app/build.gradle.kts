plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
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
    }

    buildTypes {
        release {
            isMinifyEnabled = false
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
}
