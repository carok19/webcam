plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.camarandi"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.camarandi"
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "1.0"
    }

    signingConfigs {
        // Llave incluida en el repo para que las actualizaciones se instalen encima sin desinstalar.
        // NO sirve para publicar en Play Store (ver README).
        create("compartida") {
            storeFile = file("../keystore/camarandi.jks")
            storePassword = "camarandi"
            keyAlias = "camarandi"
            keyPassword = "camarandi"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("compartida")
        }
        debug {
            signingConfig = signingConfigs.getByName("compartida")
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
    // Sin dependencias externas: sólo el framework de Android + la librería estándar de Kotlin.
}
