plugins { id("com.android.application"); id("org.jetbrains.kotlin.android"); id("org.jetbrains.kotlin.plugin.compose") }
val signingKeys = listOf("ANDROID_KEYSTORE_PATH", "ANDROID_KEYSTORE_PASSWORD", "ANDROID_KEY_ALIAS", "ANDROID_KEY_PASSWORD")
val signingValues = signingKeys.associateWith { System.getenv(it).orEmpty() }
val signingRequested = signingValues.values.any { it.isNotBlank() } || System.getenv("REQUIRE_ANDROID_SIGNING") == "true"
if (signingRequested) {
    require(signingValues.values.all { it.isNotBlank() }) { "Android signing configuration is incomplete; refusing a signing fallback." }
    require(file(signingValues.getValue("ANDROID_KEYSTORE_PATH")).isFile) { "Android signing keystore is missing." }
}
val ciVersionCode = System.getenv("ANDROID_VERSION_CODE")?.let {
    requireNotNull(it.toIntOrNull()?.takeIf { n -> n > 0 }) { "ANDROID_VERSION_CODE must be a positive integer." }
} ?: 1
android {
    namespace = "com.nourix.routeshield"
    compileSdk = 35
    defaultConfig {
        applicationId = "com.nourix.routeshield"
        minSdk = 26
        targetSdk = 35
        versionCode = ciVersionCode
        versionName = "0.1.0-preview"
    }
    splits { abi { isEnable = true; reset(); include("arm64-v8a", "x86_64"); isUniversalApk = false } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    testOptions { unitTests.isReturnDefaultValues = true; unitTests.isIncludeAndroidResources = true; unitTests.all { it.systemProperty("roborazzi.test.record", "true") } }
    packaging { jniLibs.useLegacyPackaging = false; resources.excludes += listOf("META-INF/DEPENDENCIES", "META-INF/LICENSE*", "META-INF/NOTICE*") }
    buildFeatures { compose = true; buildConfig = true }
    bundle { language { enableSplit = false } }
    signingConfigs {
        if (signingRequested) create("production") {
            storeFile = file(signingValues.getValue("ANDROID_KEYSTORE_PATH"))
            storePassword = signingValues.getValue("ANDROID_KEYSTORE_PASSWORD")
            keyAlias = signingValues.getValue("ANDROID_KEY_ALIAS")
            keyPassword = signingValues.getValue("ANDROID_KEY_PASSWORD")
        }
    }
    buildTypes {
        debug { applicationIdSuffix = ".dev"; versionNameSuffix = "-dev" }
        release {
            if (signingRequested) signingConfig = signingConfigs.getByName("production")
            isMinifyEnabled = true; isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
        create("diagnostic") {
            initWith(getByName("release")); applicationIdSuffix = ".diagnostic"
            versionNameSuffix = "-diagnostic"; matchingFallbacks += listOf("release")
            signingConfig = signingConfigs.getByName("debug")
        }
    }
}
dependencies {
    implementation(files("libs/libbox.aar"))
    implementation(platform("androidx.compose:compose-bom:2025.05.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.lifecycle:lifecycle-viewmodel-ktx:2.9.1")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material:material-icons-extended")
    // Explicit Expressive line, not the BOM's stable Material version. Verified minCompileSdk=35.
    implementation("androidx.compose.material3:material3:1.5.0-alpha08")
    implementation("com.materialkolor:material-kolor:3.0.1")
    implementation("androidx.profileinstaller:profileinstaller:1.4.1")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.robolectric:robolectric:4.14.1")
    testImplementation("androidx.test:core:1.6.1")
    testImplementation("androidx.compose.ui:ui-test-junit4")
    testImplementation("io.github.takahirom.roborazzi:roborazzi:1.43.0")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
    testImplementation("org.json:json:20250517")
}
