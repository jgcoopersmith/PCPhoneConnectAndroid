import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

/**
 * Release signing comes from an untracked keystore.properties (or the matching
 * environment variables, for CI). If neither is present the release build is
 * left unsigned rather than failing, so a fresh checkout still compiles.
 */
val keystorePropsFile = rootProject.file("keystore.properties")
val keystoreProps = Properties().apply {
    if (keystorePropsFile.exists()) keystorePropsFile.inputStream().use { load(it) }
}

fun signingValue(key: String, env: String): String? =
    keystoreProps.getProperty(key) ?: System.getenv(env)

val releaseStorePath = signingValue("storeFile", "PCPC_STORE_FILE")

android {
    namespace = "com.j0ker.pcphoneconnect"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.j0ker.pcphoneconnect"
        minSdk = 26
        targetSdk = 36
        versionCode = 158
        versionName = "1.58"
    }

    signingConfigs {
        create("release") {
            if (releaseStorePath != null) {
                storeFile = rootProject.file(releaseStorePath)
                storePassword = signingValue("storePassword", "PCPC_STORE_PASSWORD")
                keyAlias = signingValue("keyAlias", "PCPC_KEY_ALIAS")
                keyPassword = signingValue("keyPassword", "PCPC_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
            if (releaseStorePath != null) {
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

    buildFeatures {
        buildConfig = true
        viewBinding = true
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.appcompat)
    implementation(libs.material)
}

