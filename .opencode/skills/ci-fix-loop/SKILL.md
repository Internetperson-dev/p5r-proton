
---
name: ci-fix-loop
description: Monitor GitHub Actions CI, diagnose failures, fix code, push changes, and repeat until CI passes.
---

# CI Fix Loop

You are responsible for getting the current branch's GitHub Actions CI
to pass.

Use git and the GitHub CLI (gh).

## Objective

Repeat this cycle:

1. Identify the latest CI run for the current branch.
2. Wait for the run to finish.
3. If it succeeds, stop.
4. If it fails, inspect the failed job logs.
5. Diagnose the root cause.
6. Fix the issue.
7. Run relevant tests locally.
8. Commit and push the fix.
9. Find and monitor the new CI run.
10. Repeat.

Never force pushh
Do as much yourslef as possbile. Get into a loop like a real developer without having to ask me or me needing to prompt you
