---
description: "Stage, review, and commit changes with a well-formed message. Checks whether documentation needs updating before committing."
agent: "agent"
---

Commit the current changes to git.

## Inputs

- **Message hint**: An optional summary from the user describing the change (infer from the diff if not provided)

## Steps

1. **Review the staged and unstaged changes**:
   - Run `git diff --stat` and `git diff --cached --stat` to see what's changed
   - Stage all modified/new files with `git add -A` unless a file looks like it should be gitignored (e.g. build output, credentials, IDE-specific files not already tracked)

2. **Review and update documentation** (mandatory before committing):
   - Cross-reference the changed files against the Documentation Maintenance table in `copilot-instructions.md`
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
   - If an instruction file's `applyTo` glob wouldn't match new files, widen it
   - Stage any updated doc files alongside the code changes
   - If no docs need updating, explicitly confirm that before proceeding

3. **Write the commit message**:
   - Use imperative mood, max ~50 chars for the subject line
   - If the change is non-trivial, add a blank line then a body explaining _why_ (not just _what_)
   - If docs were updated, append " and update docs" to the subject or note it in the body

4. **Commit**:
   - Show the proposed commit message and ask the user to confirm or adjust
   - `git commit -m "<message>"`

5. **Push**:
   - After committing, ask the user if they want to push
   - Never push without explicit confirmation
