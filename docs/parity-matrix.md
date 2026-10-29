# Parity Matrix

Phase 0 output for [PLAN.md](PLAN.md). Every feature of the NetNewsWire Mac app is listed once, marked **Port** (with the test that proves parity) or **Drop** (with the reason).

Inventoried from [Ranchero-Software/NetNewsWire](https://github.com/Ranchero-Software/NetNewsWire) at commit `70c5dce` (2026-10-24), folders `Mac/`, `Shared/` and `Modules/`. Re-run the inventory when rebasing on a newer NNW release.

Test kinds:

- **Unit**: xUnit test against `Core`, `Parser`, `Store` or a `Sync.*` project.
- **Golden**: parser output compared with NNW's fixture output.
- **UI**: scripted FlaUI/WinAppDriver test against the running app.
- **Side-by-side**: manual check on the release checklist, Mac and Windows apps on the same OPML and account.

## Status

Audited against the app on 2026-10-29. Each row has a status:

- **Done**: built and checked by hand in the running app. The scripted UI tests the Test column asks for (FlaUI or WinAppDriver) aren't written yet, so most of these rows have unit tests only where the logic lives in `Core`, `Store` or `Sync.*`.
- **Partial**: built, but less than the row says; the note says what's missing.
- **Open**: not built yet.
- **Unchecked**: built, but not tested.
- **Installer**: waits for the signed installer (Phase 6).
- **Keys**: waits for a service's developer keys or API access.
- **Dropped**: not ported, for the reason given.

| Status | Rows |
| --- | --- |
| Done | 168 |
| Partial | 1 |
| Open | 4 |
| Unchecked | 39 |
| Installer | 0 |
| Keys | 2 |
| Dropped | 23 |
| **Total** | **237** |

By section:

| Section | Done | Partial | Open | Unchecked | Installer | Keys | Dropped |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Feed formats and networking | 14 |  |  |  |  |  |  |
| Accounts and sync | 6 |  | 4 |  |  | 2 | 1 |
| Main window and layout | 10 |  |  | 1 |  |  | 1 |
| Toolbar items | 14 |  |  |  |  |  |  |
| Sidebar | 13 |  |  | 7 |  |  |  |
| Smart feeds and timeline | 14 |  |  | 1 |  |  |  |
| Reading state | 8 |  |  |  |  |  |  |
| Article view | 12 |  |  | 2 |  |  |  |
| Feed management and import/export | 4 |  |  | 3 |  |  | 1 |
| Inspector | 6 |  |  |  |  |  |  |
| Notifications | 3 |  |  | 1 |  |  |  |
| Preferences | 10 | 1 |  | 4 |  |  | 3 |
| Utility windows | 5 |  |  | 2 |  |  | 1 |
| Other | 7 |  |  |  |  |  | 2 |
| Dropped Apple-only features |  |  |  |  |  |  | 13 |
| Single-key shortcuts (global) | 14 |  |  |  |  |  | 1 |
| Timeline and article | 4 |  |  |  |  |  |  |
| Menu shortcuts | 24 |  |  | 18 |  |  |  |

## Findings that change the plan

1. **Inoreader also needs developer keys.** NNW marks iCloud, Feedly, Feedbin and Inoreader as `isDeveloperRestricted` (`Modules/Account/Sources/Account/Account.swift`). Inoreader needs an app ID and key, like Feedly. Feedbin's restriction needs checking before Phase 4.
2. **Features missing from the plan's scope table:** column layout (Mail-style table timeline), status bar, Dinosaurs window (stale feeds), Activity / Error / Activity Log windows, Account Stats, multiple main windows, theme download from URL, toast actions, first-run default feeds and RSS 1.0 (RDF). All are marked Port below, which adds work mostly to Phases 2 and 5.
3. **Five hidden preferences** exist only as defaults with no UI (rows PR-14 to PR-18). They are cheap to port and long-time users rely on them.

## Feed formats and networking

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| FM-01 | RSS 2.0 | `RSParser/Feeds/XML/RSSParser.swift` | Port | Done | Golden: every `.rss`/`.xml` fixture |
| FM-02 | RSS 1.0 (RDF) | `RSSParser.swift` | Port | Done | Golden: `bio.rdf` |
| FM-03 | Atom | `AtomParser.swift` | Port | Done | Golden: every `.atom` fixture |
| FM-04 | JSON Feed | `JSON/JSONFeedParser.swift` | Port | Done | Golden: every `.json` fixture except RSS-in-JSON |
| FM-05 | RSS-in-JSON | `JSON/RSSInJSONParser.swift` | Port | Done | Golden: `ScriptingNews.json` |
| FM-06 | Media RSS (thumbnails, enclosures) | `MediaRSS.swift` | Port | Done | Golden: `media-group.rss`, `youtube.atom` |
| FM-07 | Podcast enclosures shown in article | `ArticleRenderer+Enclosures.swift` | Port | Done | Golden: `atp.rss`; UI: enclosure link renders |
| FM-08 | Format sniffing from bytes | `Data+ProbablyFormat.swift` | Port | Done | Unit: each fixture detected as its own type |
| FM-09 | Feed finder (HTML `<link>` discovery) | `Modules/FeedFinder` | Port | Done | Golden: `DaringFireball.html`, `sixcolors.html`, `furbo.html` |
| FM-10 | HTML metadata (favicons, apple-touch-icon, og:image) | `Modules/HTMLMetadata` | Port | Done | Golden: `inessential.html`, `coco.html` |
| FM-11 | OPML parse | `XML/OPMLParser.swift` | Port | Done | Golden: `Subs.opml`, `SubsNoTitleAttributes.opml` |
| FM-12 | Conditional GET (ETag / Last-Modified) | `Modules/RSWeb` | Port | Done | Unit: second fetch sends both headers; 304 skips parse |
| FM-13 | Per-host throttling | `Modules/RSWeb`, `SyncRateLimiter.swift` | Port | Done | Unit: concurrent requests to one host stay under the limit |
| FM-14 | Content rules for article web view | `Shared/Resources/ContentRules.json` | Port | Done | Unit: rules load into WebView2 request filter |

## Accounts and sync

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| AC-01 | Local account ("On This PC") | `Account/LocalAccount` | Port | Done | Unit: add, refresh, delete feed; state survives restart |
| AC-02 | Feedbin | `Account/Feedbin` | Port | Open: Feedbin | Unit: recorded HTTP; side-by-side round trip |
| AC-03 | NewsBlur | `Account/NewsBlur`, `Modules/NewsBlur` | Port | Open: NewsBlur | Unit: recorded HTTP; side-by-side round trip |
| AC-04 | Inoreader | `Account/ReaderAPI` | Port | Keys: needs Inoreader app keys | Needs Inoreader app key (finding 1). Unit + side-by-side |
| AC-05 | The Old Reader | `Account/ReaderAPI` | Port | Done: Reader API variant, fixed host; Old Reader ID rules | Unit: fake Reader API server |
| AC-06 | BazQux | `Account/ReaderAPI` | Port | Done: Reader API variant, fixed host; Old Reader ID rules | Unit: fake Reader API server |
| AC-07 | FreshRSS (self-hosted URL) | `Account/ReaderAPI` | Port | Done | Unit: recorded HTTP; side-by-side against a FreshRSS container |
| AC-08 | Feedly (OAuth in browser) | `Account/Feedly` | Port | Keys: needs Feedly API access | Only if Feedly grants API access. Unit + side-by-side |
| AC-09 | iCloud (CloudKit) account | `Account/CloudKit`, `Modules/CloudKitSync` | Drop | Dropped | Data lives in Ranchero's private CloudKit container |
| AC-10 | Sync status database (pending changes queue) | `Modules/SyncDatabase` | Port | Done | Unit: offline mark-read is sent on next sync |
| AC-11 | Multiple accounts, enable/disable each | `Preferences/Accounts` | Port | Done | UI: disabled account hides from sidebar and skips refresh |
| AC-12 | Credentials storage | Keychain via `Modules/Secrets` | Port | Done | Windows Credential Manager. Unit: save, read, delete |
| AC-13 | Default feeds on first run | `Shared/Importers/DefaultFeeds.opml` | Port | Done | UI: fresh profile shows the default feeds |

## Main window and layout

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| MW-01 | Three-pane layout (sidebar, timeline, article) | `MainWindow/` | Port | Done | UI: all three panes present and resizable |
| MW-02 | Column layout (timeline as table: unread, starred, feed, title, date) | `Timeline/TimelineColumn.swift`, `+ColumnLayout.swift` | Port | Done | UI: View > Use Column Layout switches layout; columns sort |
| MW-03 | Toggle sidebar | `toggleSidebar:` | Port | Done | UI: shortcut hides and shows sidebar |
| MW-04 | Status bar (counts or last refresh, click to cycle; link hover URL) | `StatusBar/` | Port | Unchecked: clicking cycles last refresh, unread and feeds counts (driven); the hovered link's address is built, not hovered | UI: click cycles unread count, folders/feeds, last refresh |
| MW-05 | Status bar gear menu (selection actions) | `statusBarMenu()` | Port | Done | UI: menu matches sidebar context menu for selection |
| MW-06 | Theme menu in status bar | `toggleStatusBarThemeMenu:` | Port | Done | UI: picking a theme re-renders article |
| MW-07 | Multiple main windows | `newMainWindow:` | Port | Done | UI: second window has independent selection |
| MW-08 | Window state restore (panes, selection, layout) | `*WindowState.swift` | Port | Done | UI: restart restores layout and selected feed |
| MW-09 | Sidebar/timeline/article font sizes (hidden defaults) | `sidebarFontSize` etc. | Port | Done | Unit: setting is applied on launch |
| MW-10 | Full screen | `toggleFullScreen:` | Port | Done | F11. UI: toggles full screen |
| MW-11 | Show/hide toolbar | `toggleToolbarShown:` | Port | Done | UI: toolbar hides and shows |
| MW-12 | Toolbar customization palette | `runToolbarCustomizationPalette:` | Drop | Dropped | No native WinUI equivalent; default toolbar ships fixed. Revisit after 1.0 |

### Toolbar items

Default order: Toggle Sidebar, Refresh, New (Feed/Folder menu) | Mark All as Read, Hide Read Articles | Mark Read, Star, Next Unread, Reader View, Share, Open in Browser, Search.

| ID | Item | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- |
| TB-01 | Toggle Sidebar | Port | Done | UI: click toggles sidebar |
| TB-02 | Refresh (shows progress) | Port | Done | UI: click starts refresh; progress visible |
| TB-03 | New Feed / New Folder menu | Port | Done | UI: each item opens its sheet |
| TB-04 | Mark All as Read | Port | Done | UI: timeline all read |
| TB-05 | Hide Read Articles toggle | Port | Done | UI: state matches View menu |
| TB-06 | Mark Read / Unread (state follows selection) | Port | Done | UI: icon flips with selection state |
| TB-07 | Star / Unstar | Port | Done | UI: icon flips with selection state |
| TB-08 | Next Unread | Port | Done | UI: selects next unread across feeds |
| TB-09 | Reader View (shows extracting progress, failure state) | Port | Done | UI: idle, processing, on, failed states |
| TB-10 | Share | Port | Done | Windows' share sheet (`DataTransferManager`) with the article's title and link; see SH-01 |
| TB-11 | Open in Browser | Port | Done | UI: opens article link |
| TB-12 | Article Theme menu (optional item) | Port | Done | UI: picking theme re-renders |
| TB-13 | Search field | Port | Done | UI: typing filters timeline |
| TB-14 | Clean Up (optional item) | Port | Done | UI: removes read articles from timeline view |

## Sidebar

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| SB-01 | Accounts as groups, folders, feeds | `SidebarViewController.swift` | Port | Done | UI: tree matches imported OPML |
| SB-02 | Unread counts: show, dot, hide | `UnreadCountDisplay.swift` | Port | Done | UI: each setting renders |
| SB-03 | Favicons and feed icons | `Modules/Images`, `IconImageCache.swift` | Port | Done | UI: icons load; cache folder populated |
| SB-04 | Drag and drop: move feeds/folders, between accounts | `SidebarOutlineDataSource.swift` | Port | Unchecked: feeds drag within and between accounts, and now folders drag to another account (they move with their feeds; the transfer is unit tested); dragging needs a mouse and, for a folder, a second account | UI: drag moves feed; sync account reflects it |
| SB-05 | Drag URL or feed onto sidebar to subscribe | `PasteboardFeed.swift` | Port | Unchecked: built (drop on a row picks its account and folder); dragging needs a mouse, not tried; address parsing is unit tested | UI: dropping a URL opens Add Feed |
| SB-06 | Inline rename | `Sidebar/Renaming` | Port | Unchecked: Rename… and F2 rename in place; driven from the status bar menu, committing by moving focus; Enter and Esc not pressed | UI: F2 / Rename edits in place |
| SB-07 | Delete with confirmation, undo | `SidebarDeleteItemsAlert.swift` | Port | Unchecked: asks first; undo built (store steps tested), delete not driven in the app | UI: delete asks; Ctrl+Z restores |
| SB-08 | Hide read feeds | `toggleReadFeedsFilter:` | Port | Done | UI: feeds with no unread hide |
| SB-09 | Double-click feed opens home page; hidden pref marks all read instead | `GruberFeedDoubleClickMarkAsRead` | Port | Unchecked: double-click needs a mouse; not tested | UI: both behaviors |
| SB-10 | Context menu, nothing selected: New Feed, New Folder | `menuForNoSelection()` | Port | Unchecked: right-click needs a mouse; not tested | UI: items present |
| SB-11 | Context menu, feed: Mark All as Read, Open Home Page, Copy Feed URL, Copy Home Page URL, Show Notifications, Always Use Reader View, Rename, Delete | `menuForFeed(_:)` | Port | Done | UI: items present in this order, check states correct |
| SB-12 | Context menu, folder: Mark All as Read, Rename, Delete | `menuForFolder(_:)` | Port | Done | UI: items present |
| SB-13 | Context menu, smart feed: Mark All as Read | `menuForSmartFeed(_:)` | Port | Done | UI: item present |
| SB-14 | Context menu, multiple: Mark All as Read, Delete | `menuForMultipleObjects(_:)` | Port | Unchecked: built (Ctrl+click, Shift+click, menu and Delete key for the chosen rows; selection rules unit tested); it needs a mouse and keyboard together, not tried | UI: items present |

## Smart feeds and timeline

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| SF-01 | Today | `TodayFeedDelegate.swift` | Port | Done | Unit: same article set as `fetchType .today` |
| SF-02 | All Unread | `UnreadFeed.swift` | Port | Done | Unit: count equals sum of feeds |
| SF-03 | Starred | `StarredFeedDelegate.swift` | Port | Done | Unit: starred articles only |
| SF-04 | Search (as a feed, across all accounts) | `SearchFeedDelegate.swift` | Port | Done | Unit: title and body match; side-by-side same results |
| SF-05 | Search within current timeline | `SearchTimelineFeedDelegate.swift` | Port | Done | Unit: limited to selected feed |
| TL-01 | Sort by date, ascending/descending | `ArticleSorter.swift` | Port | Done | Unit: sort order matches NNW for fixtures |
| TL-02 | Sort by feed, title, unread, starred (column layout) | `sortArticlesByField:` | Port | Done | UI: View > Sort Articles By changes order |
| TL-03 | Group by feed | `timelineGroupByFeed` | Port | Done | Unit + UI |
| TL-04 | Hide read articles | `toggleReadArticlesFilter:` | Port | Done | UI: read articles hide; current selection stays |
| TL-05 | Clean up (drop read from current view) | `cleanUp:` | Port | Done | UI: read rows removed |
| TL-06 | Row shows title, summary, feed, date, icon, author avatar, unread dot, star | `Timeline/Cell` | Port | Done | Side-by-side screenshot review |
| TL-07 | Separators (hidden pref) | `CorreiaSeparators` | Port | Done | UI: setting toggles separators |
| TL-08 | Context menu: Mark Read/Unread, Star/Unstar, Mark Above/Below as Read, Select Feed in Sidebar, Mark All as Read in feed, Open in Browser, Copy Article URL, Copy External URL, Share | `menu(for:)` | Port | Done | UI: items present in this order |
| TL-09 | Drag article out as URL | `ArticlePasteboardWriter.swift` | Port | Unchecked: built (one article drags as a web link, several as lines of text); dragging needs a mouse, not tried | UI: drop on browser opens URL |
| TL-10 | Undo for mark read / star | `MarkStatusCommand.swift` | Port | Done | UI: Ctrl+Z reverts |

## Reading state

| ID | Feature | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- |
| RS-01 | Mark read / unread (toggle) | Port | Done | Unit + UI |
| RS-02 | Star / unstar | Port | Done | Unit + UI |
| RS-03 | Mark all as read | Port | Done | Unit + UI |
| RS-04 | Mark older as read | Port | Done | Unit: articles older than selection marked |
| RS-05 | Mark above / below as read | Port | Done | Unit + UI |
| RS-06 | Mark unread and go to next unread | Port | Done | UI |
| RS-07 | Mark all in timeline read and go to next unread | Port | Done | UI |
| RS-08 | Auto mark read on select | Port | Done | UI: selecting marks read |

## Article view

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| AV-01 | HTML template, core and stylesheet CSS, scripts | `Shared/Article Rendering` | Port | Done | Side-by-side screenshot of 10 fixture articles |
| AV-02 | Bundled themes (Default, Appanoose, Biblioteca, Hyperlegible, NewsFax, Promenade, Sepia, Tiqoe Dark, Verdana Revival) | `Resources/Themes` | Port | Done | Side-by-side screenshot per theme |
| AV-03 | `.nnwtheme` import by double-click | Info.plist doc type | Port | Unchecked: .nnwtheme is registered to open with the app and a launch with a theme path installs it; not tried with a real theme file or a double-click | UI: opening a file installs the theme |
| AV-04 | Theme download from http(s) URL (zip) | `ArticleThemeDownloader.swift` | Port | Done | Unit: download, unzip, install; rejects non-http |
| AV-05 | Open Themes Folder | `showThemesFolder:` | Port | Done | UI: Explorer opens folder |
| AV-06 | Article text size: S, M, L, XL, XXL | `ArticleTextSize.swift` | Port | Done | UI: each size changes rendered CSS |
| AV-07 | Enable JavaScript toggle | `articleContentJavascriptEnabled` | Port | Done | Unit: WebView2 setting follows pref |
| AV-08 | Reader View (on demand and per feed always on) | `Shared/Article Extractor` | Port | Done | Local Readability port. Unit: extraction on `DaringFireball.html` |
| AV-09 | Footnotes popover | `newsfoot.js` | Port | Unchecked: newsfoot.js runs in the page; the popover isn't tested | UI: footnote opens popover |
| AV-10 | Link hover shows URL in status bar | `DetailLinkOverlayView.swift` | Port | Done: WebView2 shows the hovered link itself | UI |
| AV-11 | Find in article (find, next, previous) | Edit > Find | Port | Done | UI: Ctrl+F, F3, Shift+F3 |
| AV-12 | Feed icons in article header via custom scheme | `DetailIconSchemeHandler.swift` | Port | Done | WebView2 resource handler. UI: icon renders |
| AV-13 | Special-case rendering rules | `ArticleRenderingSpecialCases.swift` | Port | Done | Golden: same HTML output for affected fixtures |
| AV-14 | Web Inspector (debug) | `toggleWebInspectorEnabled:` | Port | Done | WebView2 DevTools behind debug menu |

## Feed management and import/export

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| FD-01 | Add Feed sheet (URL, name, account, folder; remembers last account/folder) | `AddFeed/` | Port | Unchecked: with no row selected, New Feed offers the account and folder last used (kept in settings.json); needs a second account or folder to see, not driven | UI: adds feed; defaults remembered |
| FD-02 | Add Feed from list | `AddFeedFromListSheet.xib` | Port | Done | UI: picks one of several discovered feeds |
| FD-03 | Add Folder sheet | `AddFolder/` | Port | Done | UI |
| FD-04 | Import OPML into chosen account | `importOPMLFromFile:` | Port | Unchecked: Import Subscriptions asks which account when there is more than one, and a sync account subscribes to each feed in turn (the flattening and the loop are unit tested); the chooser and the file picker weren't driven | Unit: `Subs.opml` round trip |
| FD-05 | Export OPML from chosen account | `exportOPML:` | Port | Unchecked: Export Subscriptions asks which account when there is more than one; the picker wasn't driven | Unit: export, re-import, same tree |
| FD-06 | Import NNW 3 subscriptions | `importNNW3FromFile:` | Drop | Dropped | Legacy Mac-only file format |
| FD-07 | Add NetNewsWire News feed (Help menu) | `addAppNews:` | Port | Done | Point at this app's own news feed |
| FD-08 | Dinosaurs window: feeds not updated in N months (default 6), select, delete, open home page, copy URL, undo | `Mac/Dinosaurs` | Port | Done | UI: stale fixture feed listed; delete undoes |

## Inspector

| ID | Feature | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- |
| IN-01 | Feed: name (editable), home page, feed URL | Port | Done | UI: rename persists |
| IN-02 | Feed: show notifications for new articles | Port | Done | Windows toasts; toggle matches context menu |
| IN-03 | Feed: always use Reader View | Port | Done | UI: toggle matches context menu |
| IN-04 | Folder: name | Port | Done | UI |
| IN-05 | Smart feed: name | Port | Done | UI |
| IN-06 | Nothing / multiple selection placeholder | Port | Done | UI |

## Notifications

| ID | Feature | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- |
| NT-01 | New-article toast per feed | Port | Done | Windows toasts via AppUserModelID + COM activator. UI: toast appears |
| NT-02 | Toast actions: Mark as Read, Mark as Starred | Port | Done | UI: action updates state without opening app |
| NT-03 | Clicking toast opens article | Port | Unchecked: a toast click works while the app runs; starting the app from a toast needs an installed copy to try | UI |
| NT-04 | Dock/badge unread count | Port | Done | Taskbar badge. UI: badge shows count |

## Preferences

| ID | Pane | Setting | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| PR-01 | General | Unread counts: show / dot / hide | Port | Done | UI |
| PR-02 | General | Article text size | Port | Done | See AV-06 |
| PR-03 | General | Article theme + Open Themes Folder | Port | Done | See AV-02, AV-05 |
| PR-04 | General | Enable JavaScript | Port | Done | See AV-07 |
| PR-05 | General | Browser picker | Port | Done | List installed browsers from registry. UI: choice used |
| PR-06 | General | Open web pages in background | Drop | Dropped | Windows browsers give no way to open a tab in the background from another app |
| PR-07 | General | Safari extension: open feeds in NNW / default reader | Drop | Dropped | Safari only. Windows uses `feed:` handler registration (OT-04) |
| PR-08 | General | Refresh interval: manual, 30 min, 1/2/4/8 h | Port | Done | Unit: scheduler fires at interval |
| PR-09 | Accounts | Add, remove, enable, edit credentials per account | Port | Done | UI |
| PR-10 | Accounts | Add-account list grouped: local, web, self-hosted | Port | Partial: FreshRSS only | UI; iCloud group dropped |
| PR-11 | Advanced | App updates: check automatically, release vs test builds, Check Now | Port | Unchecked: Preferences → Advanced and Help → Check for Updates… use Velopack against winnewsreader.com/releases/; the packaged app knows it is installed and reports the empty feed; no release has been published yet to update to | Velopack channels. UI |
| PR-12 | Advanced | Send crash logs automatically | Port | Dropped: the app never sends crash reports; they stay on the PC and the next start offers them (see UW-08) | Tied to crash reporting choice |
| PR-13 | Advanced | Privacy policy link | Port | Done: privacy policy at winnewsreader.com/privacy/, linked from Preferences → Advanced | Needs own policy |
| PR-14 | Hidden | `ShowDebugMenu` | Port | Done | Unit |
| PR-15 | Hidden | `CorreiaSeparators` (timeline separators) | Port | Done | See TL-07 |
| PR-16 | Hidden | `KafasisTitleMode` (title on main window) | Port | Unchecked: ArticleTitleInWindowTitle in settings.json; setting tested, behavior not run | UI |
| PR-17 | Hidden | `GruberFeedDoubleClickMarkAsRead` | Port | Unchecked: DoubleClickFeedMarksRead in settings.json; setting tested, behavior not run | See SB-09 |
| PR-18 | Hidden | `DevroeSuppressSyncOnLaunch` | Port | Unchecked: SuppressRefreshOnLaunch in settings.json; setting tested, behavior not run | Unit: no refresh at launch |

## Utility windows

| ID | Feature | Mac source | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- | --- |
| UW-01 | Current Activity (refresh progress per feed) | `Mac/CurrentActivity` | Port | Unchecked: window opens and updates; a download under way wasn't caught on screen (unit tested) | UI |
| UW-02 | Activity Log | `Mac/ActivityLog` | Port | Done | UI |
| UW-03 | Error Log | `Mac/ErrorLog` | Port | Unchecked: window opens; a logged failure wasn't shown on screen (unit tested) | UI: parse failure logged |
| UW-04 | Account Stats | `Mac/AccountStats` | Port | Done | UI |
| UW-05 | iCloud Storage Stats | `Mac/CloudKitStats` | Drop | Dropped | iCloud dropped |
| UW-06 | Keyboard Shortcuts window | `KeyboardShortcuts.html` | Port | Done | Regenerate from this app's map |
| UW-07 | About window, credits | `Mac/About`, `Credits.rtf` | Port | Done | Credit NetNewsWire and MIT notice |
| UW-08 | Crash reporter window and sender | `Mac/CrashReporter` | Port | Done: a crash leaves a report on the PC and the next start offers Show Report, Report a Problem or Dismiss (nothing is sent) | Infrastructure; tool choice open |

## Other

| ID | Feature | Decision | Status | Test / reason |
| --- | --- | --- | --- | --- |
| OT-01 | Dark mode, follows system | Port | Done | UI: theme switches live |
| OT-02 | Open in browser, foreground or background | Port | Dropped: Windows gives no way to open a browser tab in the background from another app (see PR-06) | UI |
| OT-03 | `feed:`, `feeds:`, app-named scheme URLs open Add Feed | Port | Done: feed:, feeds: and winnewsreader: links open Add Feed in the running copy (a second launch hands its arguments over; driven with a feed: link) | HKCU registration. UI: launching URL opens sheet |
| OT-04 | Register as default feed reader | Port | Done: the installer hook registers the schemes, .nnwtheme and a Default apps entry, and uninstall removes them (both hooks run and the registry checked); the Default apps page itself wasn't looked at | Installer writes handler; removed on uninstall |
| OT-05 | Help menu links (website, release notes, support, repository, bug tracker, forum) | Port | Done: help, keyboard shortcuts, website, release notes (the site's news page), source code and bug tracker; no forum, since the repository has no discussions | Point at this project's own links: help, keyboard shortcuts, website, source code, issues. Release notes and a forum wait for releases |
| OT-06 | Open Application Support folder | Port | Done | Opens `%LOCALAPPDATA%\<AppName>` |
| OT-07 | Debug menu (crash tests, image cache folder, search debug) | Port | Dropped: Web Inspector, icon cache folder and rebuild search index are in the hidden Debug menu; there is no crash reporter here to test with crash items (see UW-08) | Behind PR-14 |
| OT-08 | Accessibility | Port | Done: every interactive control in the main window, Preferences and the utility windows has an accessible name (checked through UI Automation); no Narrator listening pass | Narrator / UI Automation names on every control |
| SH-01 | Share (macOS sharing services on the Mac) | Port | Done | Windows' share sheet with the article's title and link; see TB-10 |

## Dropped Apple-only features

| ID | Feature | Mac source | Reason | Status |
| --- | --- | --- | --- | --- |
| DR-01 | AppleScript dictionary | `Mac/Scripting`, `AppleScript/`, `NetNewsWire.sdef` | macOS only | Dropped |
| DR-02 | Safari subscribe extension | `Mac/SafariExtension` | Safari only | Dropped |
| DR-03 | Share extension (subscribe from Share menu) | `Mac/ShareExtension` | macOS only | Dropped |
| DR-04 | Send to MarsEdit, Send to Micro.blog | `Shared/ExtensionPoints` | Mac apps | Dropped |
| DR-05 | Handoff / NSUserActivity | `Shared/Activity` | Apple only | Dropped |
| DR-06 | Spotlight indexing | `Shared/Activity` | Apple only | Dropped |
| DR-07 | Widgets | `Shared/Widget` | iOS only | Dropped |
| DR-08 | iCloud Drive missing alert | Debug menu | iCloud dropped | Dropped |
| DR-09 | App menu Services, Hide, Hide Others, Show All | `MainMenu.xib` | macOS app menu | Dropped |
| DR-10 | Edit menu Spelling, Substitutions, Transformations, Speech, Paste and Match Style | `MainMenu.xib` | Cocoa text system; WinUI text boxes have their own | Dropped |
| DR-11 | Find > Use Selection for Find, Jump to Selection | `MainMenu.xib` | Cocoa find panel | Dropped |
| DR-12 | Window > Minimize, Zoom, Bring All to Front | `MainMenu.xib` | Window chrome on Windows | Dropped |
| DR-13 | Sparkle updater | `AppDelegate.swift` | Replaced by Velopack (PR-11) | Dropped |

## Keyboard map

Mapping rule: Cmd → Ctrl, Option → Alt, Shift stays. Mac Control combinations become Ctrl+Alt (see open question 1). Single-key shortcuts work only when focus is not in a text field, as on the Mac. Every row needs a UI test that sends the key and checks the action ran.

### Single-key shortcuts (global)

| Mac key | Windows key | Action | Status |
| --- | --- | --- | --- |
| Space | Space | Scroll article or go to next unread | Done |
| Shift+Space | Shift+Space | Scroll article up | Done |
| `-` | `-` | Go to previous unread | Done |
| `n`, `+`, Shift+`+` | same | Next unread | Done |
| `r`, `u` | same | Toggle read | Done |
| `k` | `k` | Mark all as read | Done |
| `m` | `m` | Mark unread and go to next unread | Done |
| `l` | `l` | Mark all in timeline read and go to next unread | Done |
| `o` | `o` | Mark older as read | Done |
| `s` | `s` | Toggle starred | Done |
| `b`, Return, keypad Enter | same | Open in browser | Done |
| Shift+`b`, Shift+Return, Shift+keypad Enter | same | Open in browser, opposite of background setting | Dropped: Windows can't open a browser tab in the background (see PR-06); the keys open it normally |
| `a` | `a` | Previous feed | Done |
| `z` | `z` | Next feed | Done |
| Cmd+`'` | Ctrl+`'` | Clean up | Done |

### Sidebar

| Mac key | Windows key | Action | Status |
| --- | --- | --- | --- |
| Tab, Right | same | Move focus to timeline | Done |
| `,`, Cmd+Left | `,`, Ctrl+Left | Collapse selected row | Done |
| `.`, Cmd+Right | `.`, Ctrl+Right | Expand selected row | Done |
| `;`, Cmd+Opt+Left | `;`, Ctrl+Alt+Left | Collapse all | Done |
| `'`, Cmd+Opt+Right | `'`, Ctrl+Alt+Right | Expand all | Done |
| Delete, Forward Delete | Backspace, Delete | Delete selection | Done |

### Timeline and article

| Mac key | Windows key | Action | Status |
| --- | --- | --- | --- |
| Up / Down | same | Select previous / next article | Done |
| Left (timeline) | same | Move focus to sidebar | Done |
| Right (timeline) | same | Move focus to article | Done |
| Left (article) | same | Move focus to timeline | Done |

### Menu shortcuts

| Menu | Command | Mac | Windows | Status |
| --- | --- | --- | --- | --- |
| App | Preferences | Cmd+, | Ctrl+, | Done |
| App | Quit | Cmd+Q | Ctrl+Q (Alt+F4 also closes) | Done |
| File | New Feed | Cmd+N | Ctrl+N | Done |
| File | New Folder | Shift+Cmd+N | Ctrl+Shift+N | Done |
| File | New Window | Opt+Cmd+N | Ctrl+Alt+N | Unchecked: Ctrl+Alt+N built, not pressed; the menu bar item opens a window |
| File | Refresh | Cmd+R | Ctrl+R and F5 | Done |
| File | Import Subscriptions | none | none | Done |
| File | Export Subscriptions | Opt+Cmd+E | Ctrl+Alt+E | Done |
| File | Close Window | Cmd+W | Ctrl+W | Unchecked: Ctrl+W built, not pressed |
| Edit | Undo / Redo | Cmd+Z / Shift+Cmd+Z | Ctrl+Z / Ctrl+Y and Ctrl+Shift+Z | Unchecked: Ctrl+Z / Ctrl+Y built, not pressed; the menu bar items work |
| Edit | Cut / Copy / Paste / Select All | Cmd+X/C/V/A | Ctrl+X/C/V/A | Done |
| Edit | Copy Article URL | Shift+Cmd+C | Ctrl+Shift+C | Unchecked: Ctrl+Shift+C built, not pressed |
| Edit | Copy External URL | Ctrl+Cmd+C | Ctrl+Alt+C | Unchecked: Ctrl+Alt+C built, not pressed |
| Edit | Article Search | Opt+Cmd+F | Ctrl+Alt+F | Done |
| Edit | Find in Article | Cmd+F | Ctrl+F | Unchecked: Ctrl+F built; the find bar works from the menu bar |
| Edit | Find Next / Previous | Cmd+G / Shift+Cmd+G | F3 / Shift+F3 | Unchecked: F3 built; the buttons work |
| View | Clean Up Articles | Cmd+' | Ctrl+' | Done |
| View | Hide Read Articles | Shift+Cmd+H | Ctrl+Shift+H | Done |
| View | Hide Read Feeds | Shift+Cmd+F | Ctrl+Shift+F | Done |
| View | Show Sidebar | Ctrl+Cmd+S | Ctrl+Alt+S | Done |
| View | Show Toolbar | Opt+Cmd+T | Ctrl+Alt+T | Unchecked: Ctrl+Alt+T built, not pressed; the menu bar item hides it |
| View | Full Screen | Ctrl+Cmd+F | F11 | Unchecked: F11 built; the menu bar item works |
| View | Sort Articles By, Use Column Layout, Show Status Bar, Theme Menu in Status Bar | none | none | Done |
| Go | Next Unread | Cmd+/ | Ctrl+/ | Done |
| Go | Today / All Unread / Starred | Cmd+1 / 2 / 3 | Ctrl+1 / 2 / 3 | Done |
| Article | Mark as Read | Shift+Cmd+U | Ctrl+Shift+U | Done |
| Article | Mark All as Read | Cmd+K | Ctrl+K | Done |
| Article | Mark Above as Read | Ctrl+Cmd+K | Ctrl+Alt+K | Unchecked: Ctrl+Alt+K built, not pressed |
| Article | Mark Below as Read | Shift+Cmd+K | Ctrl+Shift+K | Unchecked: Ctrl+Shift+K built, not pressed |
| Article | Mark as Starred | Shift+Cmd+L | Ctrl+Shift+L | Done |
| Article | Show Reader View | Shift+Cmd+R | Ctrl+Shift+R | Done |
| Article | Open in Browser | Cmd+Right | Ctrl+Right (article focus only) | Unchecked: Ctrl+Right (article focus) built, not pressed |
| Article | Open in Browser Inverted | Shift+Cmd+Right | Ctrl+Shift+Right | Unchecked: Ctrl+Shift+Right built, not pressed |
| Window | Main Window | Cmd+0 | Ctrl+0 | Unchecked: Ctrl+0 built, not pressed |
| Window | Info (Inspector) | Cmd+I | Ctrl+I | Done |
| Window | Dinosaurs | Cmd+6 | Ctrl+6 | Unchecked: Ctrl+6 built, not pressed; the menu bar opens it |
| Window | Current Activity | Cmd+7 | Ctrl+7 | Unchecked: Ctrl+7 built, not pressed; the menu bar opens it |
| Window | Activity Log | Cmd+8 | Ctrl+8 | Unchecked: Ctrl+8 built, not pressed; the menu bar opens it |
| Window | Error Log | Cmd+9 | Ctrl+9 | Unchecked: Ctrl+9 built, not pressed; the menu bar opens it |
| Window | Account Stats | none | none | Done |
| Help | Help | Cmd+? | F1 | Done |
| Help | Keyboard Shortcuts | none | none | Done |

## Open questions

1. **Ctrl+Alt shortcuts.** Ctrl+Alt equals AltGr on many European layouts, so Ctrl+Alt+C, +E, +F, +K, +N, +S and +T can collide with typed characters. The alternative is Ctrl+Shift+Alt for those seven.
2. **Ctrl+Right for Open in Browser** conflicts with word-jump in text fields and sidebar expand. The proposal limits it to article focus; Ctrl+Enter is the alternative.
3. **Toolbar customization (MW-12)** is dropped for 1.0. Confirm.
4. **Share (SH-01)** is resolved: it uses the Windows Share UI (`DataTransferManager`).
5. **Feedbin** is marked developer-restricted in NNW. Check whether it needs a client agreement before Phase 4.
