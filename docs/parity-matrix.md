# Parity Matrix

Phase 0 output for [PLAN.md](PLAN.md). Every feature of the NetNewsWire Mac app is listed once, marked **Port** (with the test that proves parity) or **Drop** (with the reason).

Inventoried from [Ranchero-Software/NetNewsWire](https://github.com/Ranchero-Software/NetNewsWire) at commit `70c5dce` (2026-10-24), folders `Mac/`, `Shared/` and `Modules/`. Re-run the inventory when rebasing on a newer NNW release.

Test kinds:

- **Unit**: xUnit test against `Core`, `Parser`, `Store` or a `Sync.*` project.
- **Golden**: parser output compared with NNW's fixture output.
- **UI**: scripted FlaUI/WinAppDriver test against the running app.
- **Side-by-side**: manual check on the release checklist, Mac and Windows apps on the same OPML and account.

## Findings that change the plan

1. **Inoreader also needs developer keys.** NNW marks iCloud, Feedly, Feedbin and Inoreader as `isDeveloperRestricted` (`Modules/Account/Sources/Account/Account.swift`). Inoreader needs an app ID and key, like Feedly. Feedbin's restriction needs checking before Phase 4.
2. **Features missing from the plan's scope table:** column layout (Mail-style table timeline), status bar, Dinosaurs window (stale feeds), Activity / Error / Activity Log windows, Account Stats, multiple main windows, theme download from URL, toast actions, first-run default feeds and RSS 1.0 (RDF). All are marked Port below, which adds work mostly to Phases 2 and 5.
3. **Five hidden preferences** exist only as defaults with no UI (rows PR-14 to PR-18). They are cheap to port and long-time users rely on them.

## Feed formats and networking

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| FM-01 | RSS 2.0 | `RSParser/Feeds/XML/RSSParser.swift` | Port | Golden: every `.rss`/`.xml` fixture |
| FM-02 | RSS 1.0 (RDF) | `RSSParser.swift` | Port | Golden: `bio.rdf` |
| FM-03 | Atom | `AtomParser.swift` | Port | Golden: every `.atom` fixture |
| FM-04 | JSON Feed | `JSON/JSONFeedParser.swift` | Port | Golden: every `.json` fixture except RSS-in-JSON |
| FM-05 | RSS-in-JSON | `JSON/RSSInJSONParser.swift` | Port | Golden: `ScriptingNews.json` |
| FM-06 | Media RSS (thumbnails, enclosures) | `MediaRSS.swift` | Port | Golden: `media-group.rss`, `youtube.atom` |
| FM-07 | Podcast enclosures shown in article | `ArticleRenderer+Enclosures.swift` | Port | Golden: `atp.rss`; UI: enclosure link renders |
| FM-08 | Format sniffing from bytes | `Data+ProbablyFormat.swift` | Port | Unit: each fixture detected as its own type |
| FM-09 | Feed finder (HTML `<link>` discovery) | `Modules/FeedFinder` | Port | Golden: `DaringFireball.html`, `sixcolors.html`, `furbo.html` |
| FM-10 | HTML metadata (favicons, apple-touch-icon, og:image) | `Modules/HTMLMetadata` | Port | Golden: `inessential.html`, `coco.html` |
| FM-11 | OPML parse | `XML/OPMLParser.swift` | Port | Golden: `Subs.opml`, `SubsNoTitleAttributes.opml` |
| FM-12 | Conditional GET (ETag / Last-Modified) | `Modules/RSWeb` | Port | Unit: second fetch sends both headers; 304 skips parse |
| FM-13 | Per-host throttling | `Modules/RSWeb`, `SyncRateLimiter.swift` | Port | Unit: concurrent requests to one host stay under the limit |
| FM-14 | Content rules for article web view | `Shared/Resources/ContentRules.json` | Port | Unit: rules load into WebView2 request filter |

## Accounts and sync

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| AC-01 | Local account ("On This PC") | `Account/LocalAccount` | Port | Unit: add, refresh, delete feed; state survives restart |
| AC-02 | Feedbin | `Account/Feedbin` | Port | Unit: recorded HTTP; side-by-side round trip |
| AC-03 | NewsBlur | `Account/NewsBlur`, `Modules/NewsBlur` | Port | Unit: recorded HTTP; side-by-side round trip |
| AC-04 | Inoreader | `Account/ReaderAPI` | Port | Needs Inoreader app key (finding 1). Unit + side-by-side |
| AC-05 | The Old Reader | `Account/ReaderAPI` | Port | Unit: recorded HTTP; side-by-side round trip |
| AC-06 | BazQux | `Account/ReaderAPI` | Port | Unit: recorded HTTP; side-by-side round trip |
| AC-07 | FreshRSS (self-hosted URL) | `Account/ReaderAPI` | Port | Unit: recorded HTTP; side-by-side against a FreshRSS container |
| AC-08 | Feedly (OAuth in browser) | `Account/Feedly` | Port | Only if Feedly grants API access. Unit + side-by-side |
| AC-09 | iCloud (CloudKit) account | `Account/CloudKit`, `Modules/CloudKitSync` | Drop | Data lives in Ranchero's private CloudKit container |
| AC-10 | Sync status database (pending changes queue) | `Modules/SyncDatabase` | Port | Unit: offline mark-read is sent on next sync |
| AC-11 | Multiple accounts, enable/disable each | `Preferences/Accounts` | Port | UI: disabled account hides from sidebar and skips refresh |
| AC-12 | Credentials storage | Keychain via `Modules/Secrets` | Port | Windows Credential Manager. Unit: save, read, delete |
| AC-13 | Default feeds on first run | `Shared/Importers/DefaultFeeds.opml` | Port | UI: fresh profile shows the default feeds |

## Main window and layout

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| MW-01 | Three-pane layout (sidebar, timeline, article) | `MainWindow/` | Port | UI: all three panes present and resizable |
| MW-02 | Column layout (timeline as table: unread, starred, feed, title, date) | `Timeline/TimelineColumn.swift`, `+ColumnLayout.swift` | Port | UI: View > Use Column Layout switches layout; columns sort |
| MW-03 | Toggle sidebar | `toggleSidebar:` | Port | UI: shortcut hides and shows sidebar |
| MW-04 | Status bar (counts or last refresh, click to cycle; link hover URL) | `StatusBar/` | Port | UI: click cycles unread count, folders/feeds, last refresh |
| MW-05 | Status bar gear menu (selection actions) | `statusBarMenu()` | Port | UI: menu matches sidebar context menu for selection |
| MW-06 | Theme menu in status bar | `toggleStatusBarThemeMenu:` | Port | UI: picking a theme re-renders article |
| MW-07 | Multiple main windows | `newMainWindow:` | Port | UI: second window has independent selection |
| MW-08 | Window state restore (panes, selection, layout) | `*WindowState.swift` | Port | UI: restart restores layout and selected feed |
| MW-09 | Sidebar/timeline/article font sizes (hidden defaults) | `sidebarFontSize` etc. | Port | Unit: setting is applied on launch |
| MW-10 | Full screen | `toggleFullScreen:` | Port | F11. UI: toggles full screen |
| MW-11 | Show/hide toolbar | `toggleToolbarShown:` | Port | UI: toolbar hides and shows |
| MW-12 | Toolbar customization palette | `runToolbarCustomizationPalette:` | Drop | No native WinUI equivalent; default toolbar ships fixed. Revisit after 1.0 |

### Toolbar items

Default order: Toggle Sidebar, Refresh, New (Feed/Folder menu) | Mark All as Read, Hide Read Articles | Mark Read, Star, Next Unread, Reader View, Share, Open in Browser, Search.

| ID | Item | Decision | Test / reason |
| --- | --- | --- | --- |
| TB-01 | Toggle Sidebar | Port | UI: click toggles sidebar |
| TB-02 | Refresh (shows progress) | Port | UI: click starts refresh; progress visible |
| TB-03 | New Feed / New Folder menu | Port | UI: each item opens its sheet |
| TB-04 | Mark All as Read | Port | UI: timeline all read |
| TB-05 | Hide Read Articles toggle | Port | UI: state matches View menu |
| TB-06 | Mark Read / Unread (state follows selection) | Port | UI: icon flips with selection state |
| TB-07 | Star / Unstar | Port | UI: icon flips with selection state |
| TB-08 | Next Unread | Port | UI: selects next unread across feeds |
| TB-09 | Reader View (shows extracting progress, failure state) | Port | UI: idle, processing, on, failed states |
| TB-10 | Share | Port | Windows' share sheet (`DataTransferManager`) with the article's title and link; see SH-01 |
| TB-11 | Open in Browser | Port | UI: opens article link |
| TB-12 | Article Theme menu (optional item) | Port | UI: picking theme re-renders |
| TB-13 | Search field | Port | UI: typing filters timeline |
| TB-14 | Clean Up (optional item) | Port | UI: removes read articles from timeline view |

## Sidebar

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| SB-01 | Accounts as groups, folders, feeds | `SidebarViewController.swift` | Port | UI: tree matches imported OPML |
| SB-02 | Unread counts: show, dot, hide | `UnreadCountDisplay.swift` | Port | UI: each setting renders |
| SB-03 | Favicons and feed icons | `Modules/Images`, `IconImageCache.swift` | Port | UI: icons load; cache folder populated |
| SB-04 | Drag and drop: move feeds/folders, between accounts | `SidebarOutlineDataSource.swift` | Port | UI: drag moves feed; sync account reflects it |
| SB-05 | Drag URL or feed onto sidebar to subscribe | `PasteboardFeed.swift` | Port | UI: dropping a URL opens Add Feed |
| SB-06 | Inline rename | `Sidebar/Renaming` | Port | UI: F2 / Rename edits in place |
| SB-07 | Delete with confirmation, undo | `SidebarDeleteItemsAlert.swift` | Port | UI: delete asks; Ctrl+Z restores |
| SB-08 | Hide read feeds | `toggleReadFeedsFilter:` | Port | UI: feeds with no unread hide |
| SB-09 | Double-click feed opens home page; hidden pref marks all read instead | `GruberFeedDoubleClickMarkAsRead` | Port | UI: both behaviors |
| SB-10 | Context menu, nothing selected: New Feed, New Folder | `menuForNoSelection()` | Port | UI: items present |
| SB-11 | Context menu, feed: Mark All as Read, Open Home Page, Copy Feed URL, Copy Home Page URL, Show Notifications, Always Use Reader View, Rename, Delete | `menuForFeed(_:)` | Port | UI: items present in this order, check states correct |
| SB-12 | Context menu, folder: Mark All as Read, Rename, Delete | `menuForFolder(_:)` | Port | UI: items present |
| SB-13 | Context menu, smart feed: Mark All as Read | `menuForSmartFeed(_:)` | Port | UI: item present |
| SB-14 | Context menu, multiple: Mark All as Read, Delete | `menuForMultipleObjects(_:)` | Port | UI: items present |

## Smart feeds and timeline

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| SF-01 | Today | `TodayFeedDelegate.swift` | Port | Unit: same article set as `fetchType .today` |
| SF-02 | All Unread | `UnreadFeed.swift` | Port | Unit: count equals sum of feeds |
| SF-03 | Starred | `StarredFeedDelegate.swift` | Port | Unit: starred articles only |
| SF-04 | Search (as a feed, across all accounts) | `SearchFeedDelegate.swift` | Port | Unit: title and body match; side-by-side same results |
| SF-05 | Search within current timeline | `SearchTimelineFeedDelegate.swift` | Port | Unit: limited to selected feed |
| TL-01 | Sort by date, ascending/descending | `ArticleSorter.swift` | Port | Unit: sort order matches NNW for fixtures |
| TL-02 | Sort by feed, title, unread, starred (column layout) | `sortArticlesByField:` | Port | UI: View > Sort Articles By changes order |
| TL-03 | Group by feed | `timelineGroupByFeed` | Port | Unit + UI |
| TL-04 | Hide read articles | `toggleReadArticlesFilter:` | Port | UI: read articles hide; current selection stays |
| TL-05 | Clean up (drop read from current view) | `cleanUp:` | Port | UI: read rows removed |
| TL-06 | Row shows title, summary, feed, date, icon, author avatar, unread dot, star | `Timeline/Cell` | Port | Side-by-side screenshot review |
| TL-07 | Separators (hidden pref) | `CorreiaSeparators` | Port | UI: setting toggles separators |
| TL-08 | Context menu: Mark Read/Unread, Star/Unstar, Mark Above/Below as Read, Select Feed in Sidebar, Mark All as Read in feed, Open in Browser, Copy Article URL, Copy External URL, Share | `menu(for:)` | Port (Share dropped) | UI: items present in this order |
| TL-09 | Drag article out as URL | `ArticlePasteboardWriter.swift` | Port | UI: drop on browser opens URL |
| TL-10 | Undo for mark read / star | `MarkStatusCommand.swift` | Port | UI: Ctrl+Z reverts |

## Reading state

| ID | Feature | Decision | Test / reason |
| --- | --- | --- | --- |
| RS-01 | Mark read / unread (toggle) | Port | Unit + UI |
| RS-02 | Star / unstar | Port | Unit + UI |
| RS-03 | Mark all as read | Port | Unit + UI |
| RS-04 | Mark older as read | Port | Unit: articles older than selection marked |
| RS-05 | Mark above / below as read | Port | Unit + UI |
| RS-06 | Mark unread and go to next unread | Port | UI |
| RS-07 | Mark all in timeline read and go to next unread | Port | UI |
| RS-08 | Auto mark read on select | Port | UI: selecting marks read |

## Article view

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| AV-01 | HTML template, core and stylesheet CSS, scripts | `Shared/Article Rendering` | Port | Side-by-side screenshot of 10 fixture articles |
| AV-02 | Bundled themes (Default, Appanoose, Biblioteca, Hyperlegible, NewsFax, Promenade, Sepia, Tiqoe Dark, Verdana Revival) | `Resources/Themes` | Port | Side-by-side screenshot per theme |
| AV-03 | `.nnwtheme` import by double-click | Info.plist doc type | Port | UI: opening a file installs the theme |
| AV-04 | Theme download from http(s) URL (zip) | `ArticleThemeDownloader.swift` | Port | Unit: download, unzip, install; rejects non-http |
| AV-05 | Open Themes Folder | `showThemesFolder:` | Port | UI: Explorer opens folder |
| AV-06 | Article text size: S, M, L, XL, XXL | `ArticleTextSize.swift` | Port | UI: each size changes rendered CSS |
| AV-07 | Enable JavaScript toggle | `articleContentJavascriptEnabled` | Port | Unit: WebView2 setting follows pref |
| AV-08 | Reader View (on demand and per feed always on) | `Shared/Article Extractor` | Port | Local Readability port. Unit: extraction on `DaringFireball.html` |
| AV-09 | Footnotes popover | `newsfoot.js` | Port | UI: footnote opens popover |
| AV-10 | Link hover shows URL in status bar | `DetailLinkOverlayView.swift` | Port | UI |
| AV-11 | Find in article (find, next, previous) | Edit > Find | Port | UI: Ctrl+F, F3, Shift+F3 |
| AV-12 | Feed icons in article header via custom scheme | `DetailIconSchemeHandler.swift` | Port | WebView2 resource handler. UI: icon renders |
| AV-13 | Special-case rendering rules | `ArticleRenderingSpecialCases.swift` | Port | Golden: same HTML output for affected fixtures |
| AV-14 | Web Inspector (debug) | `toggleWebInspectorEnabled:` | Port | WebView2 DevTools behind debug menu |

## Feed management and import/export

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| FD-01 | Add Feed sheet (URL, name, account, folder; remembers last account/folder) | `AddFeed/` | Port | UI: adds feed; defaults remembered |
| FD-02 | Add Feed from list | `AddFeedFromListSheet.xib` | Port | UI: picks one of several discovered feeds |
| FD-03 | Add Folder sheet | `AddFolder/` | Port | UI |
| FD-04 | Import OPML into chosen account | `importOPMLFromFile:` | Port | Unit: `Subs.opml` round trip |
| FD-05 | Export OPML from chosen account | `exportOPML:` | Port | Unit: export, re-import, same tree |
| FD-06 | Import NNW 3 subscriptions | `importNNW3FromFile:` | Drop | Legacy Mac-only file format |
| FD-07 | Add NetNewsWire News feed (Help menu) | `addAppNews:` | Port | Point at this app's own news feed |
| FD-08 | Dinosaurs window: feeds not updated in N months (default 6), select, delete, open home page, copy URL, undo | `Mac/Dinosaurs` | Port | UI: stale fixture feed listed; delete undoes |

## Inspector

| ID | Feature | Decision | Test / reason |
| --- | --- | --- | --- |
| IN-01 | Feed: name (editable), home page, feed URL | Port | UI: rename persists |
| IN-02 | Feed: show notifications for new articles | Port | UI: toggle matches context menu |
| IN-03 | Feed: always use Reader View | Port | UI: toggle matches context menu |
| IN-04 | Folder: name | Port | UI |
| IN-05 | Smart feed: name | Port | UI |
| IN-06 | Nothing / multiple selection placeholder | Port | UI |

## Notifications

| ID | Feature | Decision | Test / reason |
| --- | --- | --- | --- |
| NT-01 | New-article toast per feed | Port | Windows toasts via AppUserModelID + COM activator. UI: toast appears |
| NT-02 | Toast actions: Mark as Read, Mark as Starred | Port | UI: action updates state without opening app |
| NT-03 | Clicking toast opens article | Port | UI |
| NT-04 | Dock/badge unread count | Port | Taskbar badge. UI: badge shows count |

## Preferences

| ID | Pane | Setting | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| PR-01 | General | Unread counts: show / dot / hide | Port | UI |
| PR-02 | General | Article text size | Port | See AV-06 |
| PR-03 | General | Article theme + Open Themes Folder | Port | See AV-02, AV-05 |
| PR-04 | General | Enable JavaScript | Port | See AV-07 |
| PR-05 | General | Browser picker | Port | List installed browsers from registry. UI: choice used |
| PR-06 | General | Open web pages in background | Port | UI: Shift does the opposite |
| PR-07 | General | Safari extension: open feeds in NNW / default reader | Drop | Safari only. Windows uses `feed:` handler registration (OT-04) |
| PR-08 | General | Refresh interval: manual, 30 min, 1/2/4/8 h | Port | Unit: scheduler fires at interval |
| PR-09 | Accounts | Add, remove, enable, edit credentials per account | Port | UI |
| PR-10 | Accounts | Add-account list grouped: local, web, self-hosted | Port | UI; iCloud group dropped |
| PR-11 | Advanced | App updates: check automatically, release vs test builds, Check Now | Port | Velopack channels. UI |
| PR-12 | Advanced | Send crash logs automatically | Port | Tied to crash reporting choice |
| PR-13 | Advanced | Privacy policy link | Port | Needs own policy |
| PR-14 | Hidden | `ShowDebugMenu` | Port | Unit |
| PR-15 | Hidden | `CorreiaSeparators` (timeline separators) | Port | See TL-07 |
| PR-16 | Hidden | `KafasisTitleMode` (title on main window) | Port | UI |
| PR-17 | Hidden | `GruberFeedDoubleClickMarkAsRead` | Port | See SB-09 |
| PR-18 | Hidden | `DevroeSuppressSyncOnLaunch` | Port | Unit: no refresh at launch |

## Utility windows

| ID | Feature | Mac source | Decision | Test / reason |
| --- | --- | --- | --- | --- |
| UW-01 | Current Activity (refresh progress per feed) | `Mac/CurrentActivity` | Port | UI |
| UW-02 | Activity Log | `Mac/ActivityLog` | Port | UI |
| UW-03 | Error Log | `Mac/ErrorLog` | Port | UI: parse failure logged |
| UW-04 | Account Stats | `Mac/AccountStats` | Port | UI |
| UW-05 | iCloud Storage Stats | `Mac/CloudKitStats` | Drop | iCloud dropped |
| UW-06 | Keyboard Shortcuts window | `KeyboardShortcuts.html` | Port | Regenerate from this app's map |
| UW-07 | About window, credits | `Mac/About`, `Credits.rtf` | Port | Credit NetNewsWire and MIT notice |
| UW-08 | Crash reporter window and sender | `Mac/CrashReporter` | Port | Infrastructure; tool choice open |

## Other

| ID | Feature | Decision | Test / reason |
| --- | --- | --- | --- |
| OT-01 | Dark mode, follows system | Port | UI: theme switches live |
| OT-02 | Open in browser, foreground or background | Port | UI |
| OT-03 | `feed:`, `feeds:`, app-named scheme URLs open Add Feed | Port | HKCU registration. UI: launching URL opens sheet |
| OT-04 | Register as default feed reader | Port | Installer writes handler; removed on uninstall |
| OT-05 | Help menu links (website, release notes, support, repository, bug tracker, forum) | Port | Point at this project's own links |
| OT-06 | Open Application Support folder | Port | Opens `%LOCALAPPDATA%\<AppName>` |
| OT-07 | Debug menu (crash tests, image cache folder, search debug) | Port | Behind PR-14 |
| OT-08 | Accessibility | Port | Narrator / UI Automation names on every control |

## Dropped Apple-only features

| ID | Feature | Mac source | Reason |
| --- | --- | --- | --- |
| DR-01 | AppleScript dictionary | `Mac/Scripting`, `AppleScript/`, `NetNewsWire.sdef` | macOS only |
| DR-02 | Safari subscribe extension | `Mac/SafariExtension` | Safari only |
| DR-03 | Share extension (subscribe from Share menu) | `Mac/ShareExtension` | macOS only |
| SH-01 | Share via macOS sharing services | `SharingServiceDelegate.swift` | Port | Windows' share sheet in its place: the article's title and link |
| DR-04 | Send to MarsEdit, Send to Micro.blog | `Shared/ExtensionPoints` | Mac apps |
| DR-05 | Handoff / NSUserActivity | `Shared/Activity` | Apple only |
| DR-06 | Spotlight indexing | `Shared/Activity` | Apple only |
| DR-07 | Widgets | `Shared/Widget` | iOS only |
| DR-08 | iCloud Drive missing alert | Debug menu | iCloud dropped |
| DR-09 | App menu Services, Hide, Hide Others, Show All | `MainMenu.xib` | macOS app menu |
| DR-10 | Edit menu Spelling, Substitutions, Transformations, Speech, Paste and Match Style | `MainMenu.xib` | Cocoa text system; WinUI text boxes have their own |
| DR-11 | Find > Use Selection for Find, Jump to Selection | `MainMenu.xib` | Cocoa find panel |
| DR-12 | Window > Minimize, Zoom, Bring All to Front | `MainMenu.xib` | Window chrome on Windows |
| DR-13 | Sparkle updater | `AppDelegate.swift` | Replaced by Velopack (PR-11) |

## Keyboard map

Mapping rule: Cmd → Ctrl, Option → Alt, Shift stays. Mac Control combinations become Ctrl+Alt (see open question 1). Single-key shortcuts work only when focus is not in a text field, as on the Mac. Every row needs a UI test that sends the key and checks the action ran.

### Single-key shortcuts (global)

| Mac key | Windows key | Action |
| --- | --- | --- |
| Space | Space | Scroll article or go to next unread |
| Shift+Space | Shift+Space | Scroll article up |
| `-` | `-` | Go to previous unread |
| `n`, `+`, Shift+`+` | same | Next unread |
| `r`, `u` | same | Toggle read |
| `k` | `k` | Mark all as read |
| `m` | `m` | Mark unread and go to next unread |
| `l` | `l` | Mark all in timeline read and go to next unread |
| `o` | `o` | Mark older as read |
| `s` | `s` | Toggle starred |
| `b`, Return, keypad Enter | same | Open in browser |
| Shift+`b`, Shift+Return, Shift+keypad Enter | same | Open in browser, opposite of background setting |
| `a` | `a` | Previous feed |
| `z` | `z` | Next feed |
| Cmd+`'` | Ctrl+`'` | Clean up |

### Sidebar

| Mac key | Windows key | Action |
| --- | --- | --- |
| Tab, Right | same | Move focus to timeline |
| `,`, Cmd+Left | `,`, Ctrl+Left | Collapse selected row |
| `.`, Cmd+Right | `.`, Ctrl+Right | Expand selected row |
| `;`, Cmd+Opt+Left | `;`, Ctrl+Alt+Left | Collapse all |
| `'`, Cmd+Opt+Right | `'`, Ctrl+Alt+Right | Expand all |
| Delete, Forward Delete | Backspace, Delete | Delete selection |

### Timeline and article

| Mac key | Windows key | Action |
| --- | --- | --- |
| Up / Down | same | Select previous / next article |
| Left (timeline) | same | Move focus to sidebar |
| Right (timeline) | same | Move focus to article |
| Left (article) | same | Move focus to timeline |

### Menu shortcuts

| Menu | Command | Mac | Windows |
| --- | --- | --- | --- |
| App | Preferences | Cmd+, | Ctrl+, |
| App | Quit | Cmd+Q | Ctrl+Q (Alt+F4 also closes) |
| File | New Feed | Cmd+N | Ctrl+N |
| File | New Folder | Shift+Cmd+N | Ctrl+Shift+N |
| File | New Window | Opt+Cmd+N | Ctrl+Alt+N |
| File | Refresh | Cmd+R | Ctrl+R and F5 |
| File | Import Subscriptions | none | none |
| File | Export Subscriptions | Opt+Cmd+E | Ctrl+Alt+E |
| File | Close Window | Cmd+W | Ctrl+W |
| Edit | Undo / Redo | Cmd+Z / Shift+Cmd+Z | Ctrl+Z / Ctrl+Y and Ctrl+Shift+Z |
| Edit | Cut / Copy / Paste / Select All | Cmd+X/C/V/A | Ctrl+X/C/V/A |
| Edit | Copy Article URL | Shift+Cmd+C | Ctrl+Shift+C |
| Edit | Copy External URL | Ctrl+Cmd+C | Ctrl+Alt+C |
| Edit | Article Search | Opt+Cmd+F | Ctrl+Alt+F |
| Edit | Find in Article | Cmd+F | Ctrl+F |
| Edit | Find Next / Previous | Cmd+G / Shift+Cmd+G | F3 / Shift+F3 |
| View | Clean Up Articles | Cmd+' | Ctrl+' |
| View | Hide Read Articles | Shift+Cmd+H | Ctrl+Shift+H |
| View | Hide Read Feeds | Shift+Cmd+F | Ctrl+Shift+F |
| View | Show Sidebar | Ctrl+Cmd+S | Ctrl+Alt+S |
| View | Show Toolbar | Opt+Cmd+T | Ctrl+Alt+T |
| View | Full Screen | Ctrl+Cmd+F | F11 |
| View | Sort Articles By, Use Column Layout, Show Status Bar, Theme Menu in Status Bar | none | none |
| Go | Next Unread | Cmd+/ | Ctrl+/ |
| Go | Today / All Unread / Starred | Cmd+1 / 2 / 3 | Ctrl+1 / 2 / 3 |
| Article | Mark as Read | Shift+Cmd+U | Ctrl+Shift+U |
| Article | Mark All as Read | Cmd+K | Ctrl+K |
| Article | Mark Above as Read | Ctrl+Cmd+K | Ctrl+Alt+K |
| Article | Mark Below as Read | Shift+Cmd+K | Ctrl+Shift+K |
| Article | Mark as Starred | Shift+Cmd+L | Ctrl+Shift+L |
| Article | Show Reader View | Shift+Cmd+R | Ctrl+Shift+R |
| Article | Open in Browser | Cmd+Right | Ctrl+Right (article focus only) |
| Article | Open in Browser Inverted | Shift+Cmd+Right | Ctrl+Shift+Right |
| Window | Main Window | Cmd+0 | Ctrl+0 |
| Window | Info (Inspector) | Cmd+I | Ctrl+I |
| Window | Dinosaurs | Cmd+6 | Ctrl+6 |
| Window | Current Activity | Cmd+7 | Ctrl+7 |
| Window | Activity Log | Cmd+8 | Ctrl+8 |
| Window | Error Log | Cmd+9 | Ctrl+9 |
| Window | Account Stats | none | none |
| Help | Help | Cmd+? | F1 |
| Help | Keyboard Shortcuts | none | none |

## Open questions

1. **Ctrl+Alt shortcuts.** Ctrl+Alt equals AltGr on many European layouts, so Ctrl+Alt+C, +E, +F, +K, +N, +S and +T can collide with typed characters. The alternative is Ctrl+Shift+Alt for those seven.
2. **Ctrl+Right for Open in Browser** conflicts with word-jump in text fields and sidebar expand. The proposal limits it to article focus; Ctrl+Enter is the alternative.
3. **Toolbar customization (MW-12)** is dropped for 1.0. Confirm.
4. **Share (SH-01)** could map to the Windows Share UI (`DataTransferManager`) instead of being dropped.
5. **Feedbin** is marked developer-restricted in NNW. Check whether it needs a client agreement before Phase 4.
