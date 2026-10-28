# NetNewsWire for Windows — Port Plan

Oct 27, 2026 · Shazron · [Source doc](https://claude.ai/artifact/PMhsodLRm8pCExdGHzmDm5)

## Summary

Build a native Windows feed reader with feature parity to the NetNewsWire Mac app, in roughly 4–6.5 months of solo full-time work.

| Decision | Choice |
| --- | --- |
| Parity target | NetNewsWire **Mac** app only; Mac- and iOS-only features dropped |
| Platform | Windows 10 (1809+) and Windows 11 |
| Stack | C# / .NET LTS + WinUI 3 (Windows App SDK), no Swift reuse |
| Distribution | Direct download, signed installer with auto-update |
| Source | [Ranchero-Software/NetNewsWire](https://github.com/Ranchero-Software/NetNewsWire) (MIT) |

## Prerequisites

Three things gate the project before code: the name, API access, and accepting the iCloud gap.

- **Name and branding.** The MIT license covers the code, not the NetNewsWire name or icon. Ship under your own name, credit the original, and give Brent Simmons a heads-up.
- **API keys.** Public NNW builds have iCloud, Feedly and Reader View disabled because the keys aren't in the repo. You need your own Feedly developer access (historically hard to get) and your own Reader View extractor. NNW uses Feedbin's private extract service; use a local Readability port such as SmartReader instead.
- **iCloud sync can't be matched.** NNW's iCloud data lives in Ranchero's private CloudKit container. It's dropped along with the other Apple-only features.
- **Code signing.** Start now; eligibility checks and certificate issuance can take weeks (see Distribution).

## Parity scope

Everything in the Mac app ports 1:1 except features tied to Apple services. This list is a draft; Phase 0 confirms it against the source.

| Area | Port 1:1 |
| --- | --- |
| Formats | RSS, Atom, JSON Feed, RSS-in-JSON; conditional GET; feed finder (HTML link discovery) |
| Accounts | Local ("On This PC"), Feedbin, Inoreader, NewsBlur, The Old Reader, BazQux, FreshRSS; Feedly if API access is granted |
| Window | Three-pane layout; same toolbar actions, menus and context menus as the Mac |
| Smart feeds | Today, All Unread, Starred |
| Reading state | Mark read/unread, star, mark all read, mark older/above/below as read, hide read articles, hide read feeds, sort order, group by feed, search |
| Feed management | Folders, drag and drop, rename, delete, OPML import/export |
| Inspector | Feed name, URL, home page, new-article notifications (Windows toasts), Reader View default |
| Article view | NNW HTML templates and themes in WebView2, `.nnwtheme` import, Reader View |
| Keyboard | The full Mac shortcut map, identical behavior |
| Preferences | Same panes and options, minus anything tied to a dropped feature |
| Other | Favicons, feed icons, author avatars, dark mode, refresh intervals, open in browser (foreground or background), `feed:` URL handling |

**Infrastructure, not features:** Windows Credential Manager (replaces Keychain), auto-update (replaces Sparkle), crash reporting, Narrator / UI Automation accessibility.

**Dropped:** iCloud account, AppleScript, Safari subscribe extension, Handoff, macOS Share menu, Spotlight and other Apple system services, everything iOS-only.

## Platform: Windows 10

Support Windows 10 1809 (build 17763) and up, knowing Windows 10 itself is now out of support for consumers.

- Mainstream support ended October 2025 and consumer Extended Security Updates ended this month. Only enterprise ESU customers still get patches.
- 1809 is the Windows App SDK minimum. Confirm it against the current SDK release notes; if Microsoft raises it, your floor moves too.
- Mica isn't available on Windows 10. Use the Acrylic or solid-background fallback; rounded corners and some styling also differ.
- Many Windows 10 machines lack the WebView2 runtime, so the installer must bootstrap it.
- Keep a Windows 10 VM in the test matrix from day one.

## Stack and solution layout

The app is C# / .NET with WinUI 3, unpackaged and self-contained, so users need nothing preinstalled except WebView2, which the installer handles.

| Layer | Choice |
| --- | --- |
| Runtime | .NET (current LTS), self-contained |
| UI | WinUI 3 / Windows App SDK, unpackaged; CommunityToolkit.Mvvm |
| Sidebar / timeline | NavigationView or TreeView; virtualized ListView |
| Article view | WebView2 with NNW's HTML/CSS templates |
| Storage | Microsoft.Data.Sqlite; schema modeled on NNW's ArticlesDatabase and SyncDatabase |
| Networking | HttpClient with ETag/Last-Modified and per-host throttling |
| Reader View | SmartReader or another Readability port |
| Tests | xUnit; NNW feed fixtures copied in under their MIT notice; FlaUI or WinAppDriver for UI |

```
src/
  Core/              models, account abstraction, refresh scheduler
  Parser/            RSS, Atom, JSON Feed, RSS-in-JSON, OPML, FeedFinder
  Store/             SQLite articles + sync-status DBs
  Sync.GoogleReader/ Inoreader, FreshRSS, The Old Reader, BazQux
  Sync.Feedbin/
  Sync.NewsBlur/
  Sync.Feedly/       only if API access is granted
  Reader/            content extraction
  App/               WinUI 3 shell, views, viewmodels, themes, shortcuts
tests/
  Parser.Tests/      NNW golden fixtures
  Store.Tests/
  Sync.*.Tests/      recorded HTTP + round-trip tests
  UI.Tests/          shortcut map, smoke tests
```

## Distribution

Ship a signed Velopack installer from GitHub Releases or your own domain; without signing, SmartScreen will block or scare off most users.

- **Installer and updates:** Velopack for install, delta updates and relaunch. It works with unpackaged WinUI apps.
- **Signing, option 1:** Azure Trusted Signing (now Artifact Signing). Eligibility has been limited by country and entity type, so check for Singapore and for Geppetto Labs as an organization.
- **Signing, option 2:** an OV code-signing certificate from a CA, which now requires a hardware token or cloud HSM. SmartScreen reputation builds slowly, so early users will see warnings.
- **Notifications:** unpackaged apps must register an AppUserModelID and a COM activator themselves.
- **`feed:` handler and `.nnwtheme` association:** HKCU registry entries, written by the installer and removed on uninstall.
- **App data:** `%LOCALAPPDATA%\<AppName>`.
- **Optional:** a WinGet manifest pointing at the signed installer.

## Phases and timeline

The phases add up to 17–28 weeks, so plan on 4–6.5 months solo; sync services decide where in that range you land.

| Phase | Duration | Exit test |
| --- | --- | --- |
| 0. Inventory | 1–2 weeks | Parity matrix built from `Mac/`, `Shared/` and `Modules/`; every row Port or Drop, with a test |
| 1. Core engine | 3–5 weeks | Headless CLI subscribes, refreshes and lists unread; NNW parser fixtures pass |
| 2. Local account and shell | 4–6 weeks | Usable daily on Windows 10 and 11 with a local account |
| 3. Article view | 2–3 weeks | Themes render as on the Mac; Reader View works |
| **4. Sync services** | **5–8 weeks, biggest variable** | Round-trip tests pass against the Mac app for every service |
| 5. Keyboard and polish | 1–2 weeks | Full shortcut map passes; Narrator pass done; toasts work |
| 6. Ship | 1–2 weeks | Signed installer, update feed live, WebView2 bootstrap tested on a clean Windows 10 VM |

Agent-assisted porting can shorten Phases 1 and 4, which are mostly translation against existing tests. UI polish in Phases 2, 3 and 5 compresses least.

## Parity verification

Parity is proven by a release checklist run side by side with the Mac app, not by inspection.

- **Parity matrix as checklist:** every row from Phase 0 is Port or Drop, each Port row has a pass/fail test, and each Drop row has a reason.
- **Same inputs:** run the Mac app and the Windows app on the same OPML and the same sync account.
- **Golden parser tests:** all of NNW's fixtures pass.
- **Keyboard map:** scripted UI tests cover every shortcut.
- **Sync round trips:** for each service, mark read, star or move on Windows, then confirm the state on the Mac app, and the reverse.

## Next steps

- [ ] Start the code-signing route (Trusted Signing eligibility or OV certificate)
- [ ] Apply for Feedly API access
- [ ] Pick the app's name
- [ ] Email Brent Simmons about the port
- [x] Phase 0: extract menus, preferences, Inspector and shortcuts from `Mac/` into the [parity matrix](parity-matrix.md)
- [x] Copy NNW test fixtures into `Parser.Tests` and get the first parser passing
- [x] Phase 1: parsers, feed finder, store with folders, OPML import/export and the `wnr` CLI; all NNW parser tests pass
- [ ] Phase 2: WinUI 3 shell over the local account
  - [x] Three-pane window: sidebar (smart feeds, folders, feeds, unread counts), timeline, WebView2 article view; resizable panes
  - [x] Add feed and folder, rename and delete, OPML import and export, refresh at launch and hourly, mark read/starred, first shortcuts
  - [x] App icon (`tools/make-icon.ps1`)
  - [x] Keyboard map: single-key shortcuts, pane focus keys, and shortcuts inside the article page; next and previous unread across feeds
  - [x] Favicons in the sidebar and timeline
  - [ ] Feed icons (larger, from the feed or apple-touch-icon) for the timeline and article header
  - [x] Hide read articles (per row) and hide read feeds, sort order, clean up; dates follow NetNewsWire's logical publish date
  - [x] Remember window, panes, open folders and selection; NetNewsWire's default feeds on first launch
  - [x] Move feeds between folders: drag and drop (Ctrl copies, local only) and a Move to menu, in both accounts
  - [x] Move or copy feeds between accounts, by subscribing in the other account (JSON Feeds can't go to FreshRSS, as in NetNewsWire)
  - [ ] Search, dragging URLs and folders, inline rename, undo, Inspector, preferences, multiple windows
- [ ] Phase 3: article view
  - [x] NetNewsWire's template, core and stylesheet CSS, page scripts and special cases (AV-01), its eight bundled themes (AV-02), Open Themes Folder (AV-05), article text size; feed icons in the article header
  - [x] Reader View (AV-08, TB-09): SmartReader extracts the article from its page, on demand (Ctrl+Shift+R) or always for a feed set to Always Use Reader View
  - [ ] Theme import by opening a `.nnwtheme` (AV-03) and download from a URL (AV-04); turning off content JavaScript per site
- [ ] Phase 4, started early: FreshRSS
  - [x] Account layer (several accounts, one store each), sync status queue, Credential Manager
  - [x] Reader API client and account: feeds, folders, articles and statuses both ways; subscribe, unsubscribe, rename and delete folders
  - [x] Checked against a real FreshRSS server: 101 feeds, 11 folders, read and unread both ways
  - [x] Moving feeds between folders (FreshRSS keeps each feed in one folder, so no top level or copies)
  - [ ] OPML import into FreshRSS; Inoreader, The Old Reader and BazQux variants
