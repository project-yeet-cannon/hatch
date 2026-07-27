# Packages the signed release APK (built beforehand by `./gradlew
# assembleRelease` in CI, see .github/workflows/publish.yml) as a plain
# static file so the `files` service in compose.prod.yml can serve it over
# unauthed GET for the QR-provisioning download step in TODO.md.
FROM nginx:alpine

COPY app/build/outputs/apk/release/app-release.apk /usr/share/nginx/html/app-release.apk
