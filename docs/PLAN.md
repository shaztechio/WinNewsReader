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

The [parity matrix](parity-matrix.md) has a status for each of its 237 rows (audited 2026-10-29): 164 done, 1 partial, 4 open (all of them other sync accounts), 36 unchecked (built, but the shortcuts, dragging, multiple selection, file pickers, double-click, right-click menu, link hover, the hidden preferences other than the timeline ones, and failures showing in the logs have not been tried with real input), 8 waiting for the installer, 2 waiting for service keys, and 22 dropped.

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
  - [x] Feed icons (SB-03, NetNewsWire's `FeedIconDownloader`): the feed's own icon, else the home page's largest Apple touch icon, Open Graph or Twitter image, else the favicon, in the sidebar, timeline and article header. NetNewsWire's skip and ignore lists and WordPress size-query fix are in. Generated letter icons for feeds with none are left for later
  - [x] Hide read articles (per row) and hide read feeds, sort order, clean up; dates follow NetNewsWire's logical publish date
  - [x] Remember window, panes, open folders and selection; NetNewsWire's default feeds on first launch
  - [x] Move feeds between folders: drag and drop (Ctrl copies, local only) and a Move to menu, in both accounts
  - [x] Move or copy feeds between accounts, by subscribing in the other account (JSON Feeds can't go to FreshRSS, as in NetNewsWire)
  - [x] Inspector (IN-01, IN-03 to IN-06) as a Get Info dialog (Ctrl+I): feed name, home page, feed URL and Always Use Reader View; folder name; smart feed name. Feed rename (SB-11) keeps an edited name, renamed on the server for FreshRSS. New-article notifications (IN-02) are in, below
  - [x] Search (TB-13, SF-04): NetNewsWire's full-text index (FTS4 over title, plain-text body and authors, prefix match per word), across every account, in a toolbar box (Ctrl+Alt+F). Search within the current timeline (SF-05, iOS's scope) is left for later
  - [x] NetNewsWire 7's toolbar layout (TB-01 to TB-13): three groups over the three panes, Toggle Sidebar (Ctrl+Alt+S) on the left over the sidebar with Refresh and a +▾ menu (New, the view settings and Help) on the right, and Share through Windows' share sheet (SH-01)
  - [x] Help: +▾ → Help and F1 open the help docs at winnewsreader.com/help/ (`site/help/`); the submenu also has Keyboard Shortcuts, the website, source code and Report a Problem (OT-05)
  - [x] New-article notifications (IN-02, NetNewsWire's `UserNotificationManager`): per feed, from the right-click menu or Get Info; Windows toasts with the feed's icon, Mark as Read and Mark as Starred, at most five per feed per refresh and then a count; removed once read. Windows' own toast API, since the Windows App SDK's needs its runtime installed; so clicks work while the app runs, and it clears its notifications on exit. Clicks that start the app wait for a COM activator or packaging
  - [x] Multiple selection in the timeline (Ctrl/Shift-click, Ctrl+A): read, star, share and open act on them all with NetNewsWire's any-unread/any-unstarred rule, and the article pane shows a stack of the selected articles. NetNewsWire's article menu: Mark as Read/Unread, Starred/Unstarred, Mark Above/Below as Read, Select “Feed” in Sidebar, Mark All as Read in “Feed”, Open in Browser, Copy URLs, Share, plus Reader View
  - [x] Preferences window (+▾ → Preferences…, Ctrl+,): General (PR-01 unread counts, PR-02 to PR-05 text size, theme, JavaScript, browser picker, PR-08 refresh interval) and Accounts (PR-09 turn on or off, add, remove), plus Light/Dark/System appearance for the whole app, articles included (NetNewsWire only follows the system). Account name, server, username and password can be edited (a blank password keeps the saved one); names are kept unique, and the same server and username can't be two accounts (NetNewsWire's `duplicateServiceAccount`). Not yet: Advanced (updates and crash logs need the installer), and PR-06 open in background, which Windows browsers don't allow from outside
  - [x] App badge count (NT-04, NetNewsWire's Dock badge): the unread total on the taskbar icon, drawn as an overlay since Windows' badge API needs a packaged app; follows the Unread Counts preference (number, 99+, dot or none)
  - [x] Empty timeline: with no feed (or a group heading) selected, the middle column says "No Feed Selected", as the article pane says "No Article Selected"
  - [x] Find in Article (Ctrl+F, F3, Shift+F3, Esc) with a match count, Full Screen (F11), Mark Above/Below as Read, Copy Article/External URL, Ctrl+W, Ctrl+0 and Ctrl+Right to open in the browser. Double-clicking a feed opens its home page, and right-clicking empty sidebar space offers New Feed and New Folder. The find bar and Full Screen were driven in the running app; the keystrokes, double-click and right-click were not (no real input available), so those matrix rows read Unchecked.
  - [x] Partial rows, first batch: Keyboard Shortcuts window (`KeyboardShortcuts`, a test keeps it equal to the help page's table), Help → Release Notes with a news page at winnewsreader.com/news/, Show Status Bar, and New Feed offering the last used account and folder when nothing is selected. The window and the status bar toggle were driven in the running app.
  - [x] Partial rows, second batch: Rename in place (F2 or Rename…), Import and Export Subscriptions for a chosen account (`OpmlImporter` flattens the file and subscribes feed by feed on a sync account; tested with a fake account), and the author avatar in timeline rows (`AuthorAvatar`, NetNewsWire's one-author rule, tested). Rename and avatars were driven in the running app; the import and export pickers were not.
  - [x] Partial rows, third batch: folders can be dragged to another account with their feeds (`FeedTransfer.TransferFolderAsync`, tested; nothing is removed if a feed fails to cross), an accessibility audit through UI Automation (every interactive control in the main window, Preferences and the utility windows has a name), and the rows Windows can't do are marked Dropped or Installer.
  - [x] Multiple main windows (+▾ → New Window, Ctrl+Alt+N): each window has its own view model and selection over the shared accounts, and `AppChanges` carries read and starred changes, sidebar changes and appearance between them. The first window owns saved state, refresh and notifications, and hands them to another if it closes first; the app ends with the last window. Driven in the running app: independent selection, a change in one window reaching the other, closing the first, and exit with the last. View settings such as sort order don't follow into windows that are already open.
  - [x] Several rows selected in the sidebar with Ctrl+click and Shift+click (`RowSelection`, tested): the rows are shaded, and their menu and the Delete key offer Mark All as Read and Delete for all of them. The tree stays a single-selection control underneath, so the timeline shows the row clicked last. Built, not tried with a real mouse.
  - [x] Content rules: NetNewsWire's `ContentRules.json` (42 ad and tracking hosts, MIT, in `src/Core/ArticleRendering/Resources/`) is loaded by `ContentRules` and the article view refuses matching requests with a 403. Tested; and in the running app a planted article showed an image matching a test rule blocked while another from the same host loaded.
  - [x] Add Feed from a list: when an address has several feeds, the New Feed dialog lists them (`IAccount.FindFeedsAsync`, `FeedFinder.Ranked`, tested) and subscribes to the one chosen. Driven in the running app against wordpress.org/news, which offers four.
  - [x] Help → Add WinNewsReader News Feed, with an Atom feed at winnewsreader.com/feed.xml (`site/feed.xml`, parsed by a test), and the hidden ShowDebugMenu setting for a Debug submenu: Open Web Inspector, Open Icon Cache Folder, Rebuild Search Index (tested). The Debug menu was driven in the running app; the news feed subscription was checked against the live site.
  - [x] Drag and drop: a link dropped on the sidebar opens Add Feed with the address filled in and the account and folder of the row it landed on (`DroppedUrl` handles `feed:` links, tested), and articles drag out of the timeline as web links. Built, but not dragged by hand (it needs a mouse).
  - [x] Status bar: a gear button with the sidebar menu for the selected row, a theme button, and a status line that cycles last refresh, unread count and feed and folder counts when clicked (`StatusLine`, tested), and shows the address of a hovered link. The gear menu, theme menu and cycling were driven in the running app; the link address was not (it needs the pointer over the article).
  - [x] Column layout (+▾ → Use Column Layout): the timeline as a table (unread, starred, feed, title, date) with a clickable header that sorts by each column and reverses on a second click, and +▾ → Sort Articles By. `ArticleSorting` is a tested pure function and sorts the rows as displayed, so an untitled post sorts by the text shown for it. Driven in the running app: every column, reversing, restore after restart, and back to cards. The search box now shrinks when the article pane is narrow.
  - [x] Timeline: Group by Feed (+▾ menu), a search scope button that limits a search to the selected feed, folder or smart feed, and the hidden preferences TimelineSeparators, SidebarFontSize and TimelineFontSize (in settings.json). All driven in the running app; the font sizes apply to the sidebar rows and the timeline titles and summaries, not the small date and feed lines.
  - [x] Utility windows from the +▾ → Window menu (Ctrl+6 Dinosaurs, Ctrl+7 Current Activity, Ctrl+8 Activity Log, Ctrl+9 Error Log, and Account Stats): an in-memory `ActivityLog` that the local account fills as each feed refreshes, per-account counts, and feeds with no article in six months, whose deletion can be undone. Activity Log, Account Stats and Dinosaurs (delete, then Undo Delete) were driven in the running app; the shortcuts, and a failure or a download under way showing in the logs, were not.
  - [x] About window, Open App Data Folder, Hide/Show Toolbar (Ctrl+Alt+T) and the hidden preferences SuppressRefreshOnLaunch, ArticleTitleInWindowTitle and DoubleClickFeedMarksRead (in settings.json). About and Hide Toolbar were driven in the running app; the three preferences have a settings test only.
  - [x] Undo and redo (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z, and Undo/Redo in the +▾ menu, which names the command) for mark read/unread, star/unstar, Mark All as Read and Mark Older as Read, and for deleting a feed or folder, which subscribes again in the same folder with the same edited name. Mark read and star were undone and redone in the running app; deleting was not driven there (it needs a right-click), so its store steps are covered by tests instead.
  - [x] Screenshots on the help page, taken from a clean demo data folder with NetNewsWire's starter feeds, so no personal feeds show
  - [x] Dragging URLs and folders, inline rename, undo, multiple windows (see the lines above; the drags and Ctrl+click are built but not yet tried by hand)
- [x] Phase 3: article view
  - [x] NetNewsWire's template, core and stylesheet CSS, page scripts and special cases (AV-01), its eight bundled themes (AV-02), Open Themes Folder (AV-05), article text size; feed icons in the article header
  - [x] Reader View (AV-08, TB-09): SmartReader extracts the article from its page, on demand (Ctrl+Shift+R) or always for a feed set to Always Use Reader View
  - [x] Theme import (AV-03) and download from a URL (AV-04): **Install Theme…** takes a zip or a theme's `Info.plist`, since a `.nnwtheme` is a folder on Windows and can't be double-clicked; **Install Theme from URL…** downloads a zip. Same checks and prompts as NetNewsWire. The `netnewswire://theme/add?url=` link waits for a URL scheme registration
  - [x] Enable JavaScript (AV-07) in the +▾ menu, on by default, applied per article as NetNewsWire does; always off for sites that break with it (slashdot.org). The app's own page scripts run either way
- [ ] Phase 4, started early: FreshRSS
  - [x] Account layer (several accounts, one store each), sync status queue, Credential Manager
  - [x] Reader API client and account: feeds, folders, articles and statuses both ways; subscribe, unsubscribe, rename and delete folders
  - [x] Checked against a real FreshRSS server: 101 feeds, 11 folders, read and unread both ways
  - [x] Moving feeds between folders (FreshRSS keeps each feed in one folder, so no top level or copies)
  - [x] OPML import into a sync account, subscribing feed by feed (tested with a fake account; the file picker not driven)
  - [ ] Inoreader, The Old Reader and BazQux variants
