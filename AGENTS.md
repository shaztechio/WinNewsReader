# WinNewsReader

A native Windows feed reader (.NET 10, WinUI 3, WebView2, SQLite) aiming for feature parity with NetNewsWire for Mac. See [README.md](README.md) for an overview and how to build and run it.

## Documents

Read the one that matches the task before starting.

| File | What it is |
| --- | --- |
| [docs/FEATURES.md](docs/FEATURES.md) | How the app is used: window, accounts, preferences, shortcuts and the `wnr` tool. Update it when behavior changes |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | What each project holds, and where data is stored |
| [docs/PLAN.md](docs/PLAN.md) | The plan, with a checkbox per piece of work. Tick boxes as work lands |
| [docs/parity-matrix.md](docs/parity-matrix.md) | Every NetNewsWire feature, its status (Done, Unchecked, Keys, Dropped and so on) and how it is tested. Keep the status and the counts at the top current |
| [docs/MANUAL-TEST-CHECKLIST.md](docs/MANUAL-TEST-CHECKLIST.md) | Steps for the features that are built but not yet confirmed by hand, and the real-account checks for the sync services |
| [packaging/windows/README.md](packaging/windows/README.md) | The Microsoft Store MSIX and Certum code signing: how they work, and what is still to do |
| [docs/FUTURE-SYNC-SERVICES.md](docs/FUTURE-SYNC-SERVICES.md) | Inoreader and Feedly: why they aren't built, what access they need, and what to do once it exists |
| [docs/RELEASING.md](docs/RELEASING.md) | How a release is built and published (Velopack installer, GitHub Release, the update feed on winnewsreader.com) |
| [docs/winnewsreader.com.zone](docs/winnewsreader.com.zone) | The DNS records for the website |

The website is in `site/` (GitHub Pages): the help page is `site/help/index.html`, so a change to what the app does usually needs a change there too.

## Working here

- NetNewsWire's source is the reference. Read it with `gh api repos/Ranchero-Software/NetNewsWire/contents/<path>` rather than guessing its behavior.
- Run all tests with `dotnet test` from the repository root. Sync accounts are tested against in-memory fake servers in `tests/Sync.*.Tests`.
- The app locks its own DLLs while it runs: close `WinNewsReader.exe` before building.
- Commit only when asked, and push right after every commit. End commit messages with the `Co-Authored-By` line the tooling gives.
- Every change goes in through a Pull Request: never commit to `main`. Make a branch, commit and push it, then open a PR against `main` with `gh pr create`.
- Write commit titles and PR titles as [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): summary`, such as `feat(sync): add BazQux`, `fix: stop the taskbar count flickering`, `docs: update the manual test checklist`, `ci: run tests on pull requests`. The usual types are `feat`, `fix`, `docs`, `refactor`, `test`, `build`, `ci` and `chore`.
- The repository is public and MIT licensed: keep personal paths, credentials and anything private out of commits and docs.
- Never commit secrets (API keys, passwords). Release-time keys belong in GitHub Actions repository secrets.
