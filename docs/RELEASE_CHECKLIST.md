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

**Important: `main` receives a SQUASH of `dev`, never a merge or a fast-forward.**
`dev` carries working files that must never be published (`cases/`, `docs/*.md`),
and a normal merge would put them into `main`'s history for good.

```bash
# 1. version bump lands on dev first
git checkout dev
#    edit ApplicationVersion / ApplicationDisplayVersion, commit

# 2. validate (see above), then build the clean main snapshot
git checkout main
git reset --hard origin/main
git merge --squash dev
git rm -r --cached cases
git rm --cached $(git ls-files --cached | grep -E "^docs/.*\.md$")
#    resolve the .gitignore conflict if it appears (keep BOTH blocks:
#    /docs/*.md and dist-diagnostic/ + dist/)
# 3. verify nothing dev-only is staged, then commit + tag
git diff --cached --name-only | grep -cE '^(cases/|docs/.*\.md$)'   # must be 0
git commit -m "release: vX.Y.Z — ..."
git tag -a vX.Y.Z -m "..."
# 4. prove the tag cannot publish dev-only content, then push on owner approval
for c in $(git rev-list vX.Y.Z); do git ls-tree -r --name-only $c | grep -q '^cases/' && echo "CASES in $c"; done
git push origin main
git push origin vX.Y.Z
# 5. back to work
git checkout dev
```

CI builds the tagged commit in Release, signs it with the permanent certificate,
verifies the SHA-256, and publishes the GitHub release asset.

- Confirm GitHub Actions tests, build, signature verification, and release asset.
- Confirm `origin/main`, the tag, and the GitHub release all point to the same
  commit.
- Because of the squash, `dev` and `main` diverge every release: never
  fast-forward `main` to `dev`, and never merge `dev` into `main` directly.
