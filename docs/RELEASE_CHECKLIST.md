# PKForge Release Checklist

## Preparation

- Confirm the working tree and branch.
- Confirm `main` is the authoritative release base.
- Read `AGENTS.md` and, if present locally, `docs/HANDOFF.md`.
- Update `ApplicationVersion` and `ApplicationDisplayVersion`.
- Remove temporary diagnostics and development-only assets.
- Keep `docs/HANDOFF.md`, local test data, and local tooling state out of the
  release commit unless they are explicitly intended as project documentation.

## Validation

```bash
git diff --check
/tmp/pkforge-dotnet/dotnet test tests/PKForge.Engine.Tests/PKForge.Engine.Tests.csproj --no-restore --nologo
/tmp/pkforge-dotnet/dotnet test tests/PKForge.Domain.Tests/PKForge.Domain.Tests.csproj --no-restore --nologo
/tmp/pkforge-dotnet/dotnet build src/PKForge.App/PKForge.App.csproj \
  -f net10.0-android -r android-arm64 --no-restore --nologo
```

- Verify the APK package ID and version.
- Verify the release APK is signed by the permanent certificate.
- Test save discovery, save mutation, PokéPark navigation, second-screen
  refresh, and Create Pokémon on a device.

## Publication

- Review the staged file list.
- Commit with a focused release message.
- Create an annotated `vX.Y.Z` tag.
- Push the release commit and tag only after owner approval.
- Confirm GitHub Actions tests, build, signature verification, and release asset.
- Fast-forward `main` to the exact released commit.
- Confirm `origin/main`, the tag, and the GitHub release all point to the same
  commit.
