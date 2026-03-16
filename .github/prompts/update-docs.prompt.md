---
description: "Audit and update all project documentation to reflect recent code changes. Run this before any release or after a batch of features."
agent: "agent"
---

Audit all project documentation and bring it up to date with the current codebase.

## Steps

1. **Identify what changed since the last release**:
   - Run `git log $(git describe --tags --abbrev=0)..HEAD --oneline` to list commits since the last tag
   - If there is no tag yet, use `git log --oneline`

2. **Cross-reference each commit against the documentation maintenance table** (from `copilot-instructions.md`):

   | What changed | File(s) to update |
   |---|---|
   | New/removed service or class | `copilot-instructions.md` Key namespaces table |
   | New/changed settings property | `.github/instructions/user-settings.instructions.md` |
   | New/changed P/Invoke | `.github/instructions/pinvoke.instructions.md` |
   | Icon system changes | `.github/instructions/icons.instructions.md` |
   | Installer changes | `.github/instructions/installer.instructions.md` |
   | New/changed XAML patterns | `.github/instructions/xaml.instructions.md` |
   | Drag-and-drop changes | `.github/instructions/drag-drop.instructions.md` |
   | New page or navigation change | `copilot-instructions.md` Architecture section |
   | New dependency added/removed | `copilot-instructions.md` Dependencies list |
   | Any structural change | `ARCHITECTURE.md`, `README.md` |
   | Version process change | `.github/instructions/versioning.instructions.md` |

3. **For each affected file**:
   - Read the current contents
   - Identify what is stale, missing, or incorrect based on the code changes
   - Edit the file to reflect the current state of the codebase — be accurate and concise, do not pad

4. **Check `applyTo` coverage**:
   - For each instruction file in `.github/instructions/`, verify its `applyTo` glob matches the files it actually governs
   - If new source files were added that fall under an instruction file's topic, widen the glob
   - In `copilot-instructions.md`, verify the `<applyTo>` entries in the `<instruction>` blocks match too

5. **Commit the updates**:
   - `git add .github/ ARCHITECTURE.md README.md` (only files that were actually changed)
   - `git commit -m "Update documentation to reflect recent changes"`
   - Push if on the main branch: `git push`

## Notes

- Only update docs for things that actually changed — do not rewrite sections that are still accurate
- If a section is correct, leave it alone
- If you are unsure whether something changed, read the relevant source file to verify before editing the doc
