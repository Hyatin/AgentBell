plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

val centralVersionFile = rootProject.file("../../Directory.Build.props").readText()
fun centralVersion(propertyName: String): String = Regex(
    "<$propertyName>([^<]+)</$propertyName>",
).find(centralVersionFile)
    ?.groupValues
    ?.get(1)
    ?: error("$propertyName is missing from Directory.Build.props")

val agentBellInformationalVersion = centralVersion("AgentBellInformationalVersion")
val agentBellAndroidVersionCode = centralVersion("AgentBellAndroidVersionCode").toInt()
val releaseSigningEnvironment = mapOf(
    "keystore" to System.getenv("AGENTBELL_ANDROID_KEYSTORE"),
    "keystorePassword" to System.getenv("AGENTBELL_ANDROID_KEYSTORE_PASSWORD"),
    "keyAlias" to System.getenv("AGENTBELL_ANDROID_KEY_ALIAS"),
    "keyPassword" to System.getenv("AGENTBELL_ANDROID_KEY_PASSWORD"),
)

fun requestsReleaseArtifact(taskPath: String): Boolean {
    val taskName = taskPath.substringAfterLast(':')
    return taskName in setOf(
        "assemble",
        "assembleRelease",
        "build",
        "buildDependents",
        "buildNeeded",
        "bundle",
        "bundleRelease",
        "installRelease",
        "package",
        "packageRelease",
        "publish",
        "publishRelease",
    ) || (taskName.startsWith("publishReleasePublicationTo") && taskName.endsWith("Repository"))
}

val releaseArtifactTaskRequested = gradle.startParameter.taskNames.any(::requestsReleaseArtifact)
if (releaseArtifactTaskRequested && releaseSigningEnvironment.values.any { it.isNullOrBlank() }) {
    error("Android release signing requires all AGENTBELL_ANDROID_* signing environment variables")
}

android {
    namespace = "com.hyatin.agentbell"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.hyatin.agentbell"
        minSdk = 26
        targetSdk = 36
        versionCode = agentBellAndroidVersionCode
        versionName = agentBellInformationalVersion

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    signingConfigs {
        create("agentBellRelease") {
            if (releaseArtifactTaskRequested) {
                storeFile = file(checkNotNull(releaseSigningEnvironment["keystore"]))
                storePassword = checkNotNull(releaseSigningEnvironment["keystorePassword"])
                keyAlias = checkNotNull(releaseSigningEnvironment["keyAlias"])
                keyPassword = checkNotNull(releaseSigningEnvironment["keyPassword"])
            }
        }
    }

    buildTypes {
        getByName("release") {
            signingConfig = signingConfigs.getByName("agentBellRelease")
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    packaging {
        resources.excludes += "/META-INF/{AL2.0,LGPL2.1}"
        resources.excludes += "DebugProbesKt.bin"
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2026.06.00")

    implementation("androidx.core:core-ktx:1.18.0")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.10.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.10.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("androidx.lifecycle:lifecycle-service:2.10.0")
    implementation("androidx.datastore:datastore-preferences:1.2.1")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.camera:camera-core:1.6.1")
    implementation("androidx.camera:camera-camera2:1.6.1")
    implementation("androidx.camera:camera-lifecycle:1.6.1")
    implementation("androidx.camera:camera-view:1.6.1")
    implementation("com.google.zxing:core:3.5.4")
    implementation("com.squareup.okhttp3:okhttp:5.3.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.11.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.11.0")
    testImplementation("com.squareup.okhttp3:mockwebserver:5.3.0")
    testImplementation("org.json:json:20250517")

    androidTestImplementation(composeBom)
    androidTestImplementation("androidx.test:core-ktx:1.7.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
    androidTestImplementation("androidx.test:rules:1.7.0")
    androidTestImplementation("androidx.test.ext:junit-ktx:1.3.0")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.7.0")
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    debugImplementation("androidx.compose.ui:ui-tooling")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}
