# Pipopu

## Versioning

- Bump the app version with every update. Use a patch increment for fixes and small changes, or a larger increment when the scope warrants it.
- Keep the user-facing versions in sync. Update `<Version>` in `windows/Pipopu/Pipopu.csproj` and both `MARKETING_VERSION` entries in `ios/Pipopu.xcodeproj/project.pbxproj`.
- Increase both iOS `CURRENT_PROJECT_VERSION` entries with every version bump. The iOS app and its extensions inherit these project settings.
- Read the version displayed in Settings from the build metadata.

## GitHub

- Finish each repository change by committing it and pushing it to GitHub. Verify that the push succeeded before reporting the update complete.
