import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing: keys and passwords never enter the repository. Without tablet-android/keystore.properties
// (ignored by git) the release APK is built unsigned. Setup: docs/releasing.md.
val keystoreProps = Properties().apply {
    rootProject.file("keystore.properties").takeIf { it.exists() }?.inputStream()?.use { load(it) }
}

android {
    namespace = "io.github.yagizerhan.divit"
    compileSdk = 35

    signingConfigs {
        if (!keystoreProps.isEmpty) create("release") {
            storeFile = file(keystoreProps.getProperty("storeFile"))
            storePassword = keystoreProps.getProperty("storePassword")
            keyAlias = keystoreProps.getProperty("keyAlias")
            keyPassword = keystoreProps.getProperty("keyPassword")
        }
    }

    defaultConfig {
        // Permanent application ID (decision K-13). It can never change once published to a store.
        applicationId = "io.github.yagizerhan.divit"
        minSdk = 30          // Android 11: requestUnbufferedDispatch (low-latency pen input). Tested on Android 13+ (Tab S9 FE+).
        targetSdk = 35
        versionCode = 5
        versionName = "0.9.3"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (!keystoreProps.isEmpty) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    testOptions { unitTests.isReturnDefaultValues = true }
}

// No dependencies in the product: only the Android framework (K-11, no third-party code on the hot path).
dependencies {
    testImplementation("junit:junit:4.13.2")       // EPL-1.0, tests only
    testImplementation("org.json:json:20240303")   // to read the test vectors, tests only
}
