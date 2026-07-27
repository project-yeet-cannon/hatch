pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        // GeckoView (Firefox's engine, embedded standalone) - only published here.
        maven { url = uri("https://maven.mozilla.org/maven2/") }
    }
}

rootProject.name = "aerie-kiosk"
include(":app")
