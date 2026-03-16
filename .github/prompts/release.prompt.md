---
description: "Bump the version number and create a new release. Handles Directory.Build.props edit, commit, tag, and push."
agent: "agent"
---

Create a new release for Little Launcher.

## Inputs

- **Version type**: patch, minor, or major (ask the user if not specified)
- **Summary**: A brief description of what changed (ask or infer from recent commits)

## Steps

1. **Determine the new version**:
   - Read the current `<Version>` from `Directory.Build.props`
   - Bump the appropriate component (patch/minor/major) and reset lower components to 0
   - Confirm with the user: "Releasing vX.Y.Z — proceed?"

2. **Review and update documentation** (mandatory before committing):
   - Run `git log <previous-tag>..HEAD --oneline` to see all commits since the last release
   - Cross-reference each commit against the Documentation Maintenance table in `copilot-instructions.md`
   - Read and update every instruction/doc file that is affected:
     - New/changed service or class → `copilot-instructions.md` Key namespaces table
     - New/changed settings property → `user-settings.instructions.md`
     - New/changed P/Invoke → `pinvoke.instructions.md`
     - New/changed XAML patterns → `xaml.instructions.md`
     - Icon system changes → `icons.instructions.md`
     - Drag-and-drop changes → `drag-drop.instructions.md`
     - Installer changes → `installer.instructions.md`
     - New page or navigation → `copilot-instructions.md` Architecture section
     - New dependency → `copilot-instructions.md` Dependencies list
     - Any structural change → `ARCHITECTURE.md`, `README.md`
   - If an instruction file's `applyTo` glob wouldn't match new files created in this release, widen it
   - Include updated doc files in the version-bump commit
   - If no docs need updating, explicitly confirm that before proceeding

3. **Update `Directory.Build.props`**:
   - Change `<Version>X.Y.Z</Version>` to the new version
   - This is the **only code file** that needs editing — all other consumers derive the version automatically

4. **Commit**:
   - `git add Directory.Build.props` (plus any documentation files updated in step 2)
   - `git commit -m "Bump version to vX.Y.Z"` (or `"Bump version to vX.Y.Z and update docs"` if docs were touched)

5. **Tag**:
   - `git tag -a vX.Y.Z -m "vX.Y.Z: <summary>"`

6. **Push**:
   - `git push origin main vX.Y.Z`
   - The GitHub Action (`.github/workflows/build-msix.yml`) will automatically build, package, and publish the release

7. **Verify** (optional):
   - `gh run list --repo RyanEwen/LittleLauncher --limit 1` to confirm the workflow started
   - The release will appear at `https://github.com/RyanEwen/LittleLauncher/releases/tag/vX.Y.Z`
