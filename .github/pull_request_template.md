#### Description
A few sentences on what this changes and why.

Fixes # (if applicable)

#### Merge

Use `main` as the target. Open a pull request only when current GitHub rules require one; push directly
when they allow it. Keep a pull request current with `main` only when those rules require it. Merge
after the checks and approvals required by current rules are satisfied. An explicit maintainer's
direction to merge is sufficient; no additional review or approval is required beyond those rules.
Use any merge method enabled in GitHub.

#### Release Notes

For any user-facing change, add one structured fragment under `release-notes/` describing it. A
single fragment may cover related user-facing changes in the same pull request.

- [ ] I added a validated release-note fragment under `release-notes/` for this user-facing change.
- [ ] This change is internal-only and needs no release-note fragment.

For internal-only work, select the second option or put `release-note: none` on its own line in
the PR description. For direct pushes or merge checks that read commit messages, put the same
marker on its own line in the commit message.

See `release-notes/README.md` for the fragment schema. Preview the rendered notes with the command in that file if useful.

#### Database Migration
YES / NO. If yes, which tables?

#### How was this tested?
What you ran and on what setup (OS, Docker or native). Include steps to
reproduce if it's a fix.

#### Screenshots (UI changes only)

---

**A note on AI:** We know AI/agentic coding is everywhere and only getting
more popular. We won't insist that you disclose whether you used it or which
models you used, but in the same spirit, please don't take offense if your PR
is scrutinized and changes are requested.

**Review time:** The longer the PR and the more lines changed, the longer the
review will take. Small, focused PRs merge fastest. If yours is big, please be
patient.
