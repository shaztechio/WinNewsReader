# Android feed reader plan

Date: 2026-11-10. Status: proposed; no Android implementation has started.

## Goal and scope

Build a native Android companion with the same reading, feed-management and sync capabilities as WinNewsReader. Phones, tablets, foldables and devices with external keyboards are in scope. Use Android navigation and system integrations while preserving the underlying actions and data semantics.

The baseline is [FEATURES.md](FEATURES.md), [ARCHITECTURE.md](ARCHITECTURE.md), the [parity matrix](parity-matrix.md), and the [manual test checklist](MANUAL-TEST-CHECKLIST.md). Include implemented Windows features marked Unchecked, but verify them independently: a Windows status does not establish Android correctness. The original Windows plan and its status counts are not Android progress indicators.

The Android app has its own local data. A shared service account synchronizes subscriptions and reading state between Windows and Android; local accounts do not synchronize automatically. OPML transfers subscriptions and folders, not article history, read state or stars. No new hosted sync service is required.

Use a working name until branding, package ID and distribution ownership are decided. Preserve MIT notices for reused source, fixtures and assets; review the licenses of added dependencies and bundled themes.

## Proposed technical choices

| Area | Proposal | Reason / decision gate |
| --- | --- | --- |
| Language and UI | Kotlin, Jetpack Compose, Material 3 adaptive layouts | Native lifecycle, accessibility, touch and large-screen support |
| Platform floor | Android 10 / API 29, provisionally | Confirm dependency compatibility, device coverage and maintenance cost in Phase 0 |
| Build | Gradle Kotlin DSL; pin compatible stable toolchain versions at implementation time | Avoid committing this plan to unverified future versions |
| Architecture | UI state holders/ViewModels, repositories, suspend account APIs and Flow | Separate rendering, persistence and sync from Android screens |
| Database | Room over SQLite, one article database per account | Account isolation, migrations, transactions and observable queries |
| Search | SQLite FTS with a verified prefix-search query layer | Match title, body and author behavior rather than silently changing search semantics |
| Network | OkHttp, coroutines and explicit per-host limits | Conditional GET, redirects, cancellation and testable service clients |
| Article rendering | Android System WebView with local HTML/CSS assets | Reuse template/theme concepts and render feed HTML |
| Reader View | Local Readability-compatible extraction behind an interface | Spike extraction quality and licensing before selecting a library |
| Preferences | DataStore | Separate UI settings from account databases |
| Secrets | Encrypted app-private credential records using Android Keystore-backed keys | Keep passwords and tokens out of ordinary preferences, logs and backups |
| Background work | WorkManager with per-account coordination | Durable, cancellable, network-constrained refresh and sync |
| Tests | JVM unit tests, fake HTTP servers, database instrumentation and Compose UI tests | Verify behavior without requiring live service accounts in CI |

This is a proposed Kotlin port of the portable engine, not direct reuse of C# binaries. Reuse fixtures, protocol contracts, HTML/CSS, compatible scripts and expected outputs where licensing permits. Port parser identity rules and service quirks deliberately. WinUI, WebView2, Windows Credential Manager and Velopack require Android replacements.

Before committing to the port, estimate the maintenance cost against a .NET-for-Android shell using existing libraries. A short build/device spike must establish dependency compatibility, trimming/AOT behavior, WebView integration and background execution. Keep Kotlin as the default unless measured reuse clearly outweighs maintaining the Android interop layer; record the final choice before building the engine.

## Feature parity and Android behavior

Every capability below is a release target unless explicitly described as on hold or replaced. Smaller milestones provide usable builds without redefining full parity.

| Area / baseline IDs | Android requirement | Acceptance evidence |
| --- | --- | --- |
| Formats and networking, FM-01–14 | RSS 2.0, RSS 1.0/RDF, Atom, JSON Feed, RSS-in-JSON, Media RSS, enclosures, byte sniffing, feed discovery, HTML metadata, OPML, conditional GET, per-host limits and article content rules | Same fixtures produce equivalent identities, content and metadata; 304 responses avoid parsing; concurrency limits hold |
| Accounts, AC | Local “On This Device”; Feedbin, NewsBlur, The Old Reader, BazQux and FreshRSS; multiple accounts; add/edit/remove/enable; unique names and duplicate-login prevention | Fake-server tests plus real-account Windows/Android round trips for each service |
| First run, AC-13 | Seed the existing starter subscriptions and project news feed once; preserve user changes on upgrade | Fresh install and upgrade tests |
| Sidebar, SB | Accounts, folders, feeds, smart feeds, icons, count/dot/hidden unread indicators, hide read feeds, rename, delete with confirmation and undo | Imported hierarchy and counts match Windows; all actions accessible through touch menus |
| Feed management, FD / SB | Add by feed or website URL, choose discovered feed, remember destination, create folders, rename, move/copy feeds and folders within and across accounts | Explicit Move/Copy actions work without dragging; remote operations converge and partial failures are recoverable |
| OPML, FD-04–05 | Import/export for a selected account through the system document picker, retaining folder membership where supported | Export/reimport yields equivalent subscriptions; sync imports report progress and failed items |
| Smart feeds, SF | Today, All Unread, Starred; search across enabled accounts and within the current timeline | Fixture-defined membership and prefix matching agree with Windows, including timezone boundaries |
| Timeline, TL | Date/feed/title/read/star sorting and direction; grouping by feed; hide read; clean up current view; title, summary, date, feed/icon/avatar and state indicators | Stable ordering for equal values; filtering preserves selection; clean up does not delete stored articles |
| Reading state, RS | Toggle read/star, mark all/older/above/below read, mark unread and advance, mark all and advance, next/previous unread, automatic read on opening | Transaction and undo tests; next unread crosses feeds consistently; “above/below” uses the displayed ordering |
| Multiple selection, TL / toolbar | Long-press selection mode with select all, batch read/star/share/open/copy and an article stack for reading selected items | Batch semantics match Windows; opening multiple browser links requires an explicit batch action |
| Article view, AV | Templates, special-case rendering, feed icons, footnotes, author images, enclosure links, embedded video, find next/previous, text sizes and themes | Fixture screenshots, touch/link/footnote tests, playable permitted embeds and useful failure states |
| Reader View, AV-08 | On demand and per-feed default; loading/cancel/failure states; plain HTTP extraction first, optional isolated WebView fallback for script-built pages | Static/script-built/refused pages tested; failure leaves feed content readable and records a diagnostic |
| Themes, AV-02–06 | All nine existing bundled themes; import zipped themes or selected theme documents; install from HTTP(S) URL; manage installed themes | Fixtures cover each theme and size; safe archive extraction; Android font substitutions leave originals unchanged |
| Inspector, IN | Feed/folder/smart-feed details; editable names where supported; URLs; Reader View and notification preferences; empty/multiple-selection states | Renames survive refresh and reach the server; settings apply to the chosen feed |
| Notifications, NT | Per-feed opt-in, open article, Mark Read and Mark Starred actions; remove notifications when articles become read | Permission denial, cold launch, background actions, restart and duplicate-refresh tests |
| Preferences, PR | System/light/dark appearance including articles, independent unread indicators, text sizes, theme, JavaScript, browser choice, refresh frequency, account management, privacy and diagnostic settings | Persist across process death; preference changes update current views |
| Utilities, UW / FD-08 | Current Activity, Activity Log, Error Log, Account Stats, stale feeds with default six-month threshold, help, shortcuts, about/credits and local crash reports | Stale-feed delete supports undo; logs show refresh/extraction failures; reporting requires an explicit user action |
| Sharing and links, SH / OT | Android Sharesheet; receive shared URLs for subscription; copy article/external/feed/home-page URLs; open article/home page in browser; subscribe through supported feed schemes | Test receiving and sending intents, invalid URLs, duplicate delivery and cold start |
| Keyboard and pointer | Existing reading/navigation shortcuts where Android permits; selection, context menus, drag/drop on supported large-screen surfaces | Hardware-keyboard tests with text-field focus and international layouts; menu equivalents for every action |
| Accessibility, OT-08 | TalkBack, logical focus order, meaningful state labels, scalable text, contrast and accessible alternatives to gestures | Manual TalkBack pass on phone/tablet; automated semantics checks |

### Phone navigation

- Start on the last selected account/feed or smart feed, with an app bar showing unread state, refresh and search. Expose accounts/folders through a navigation drawer or dedicated subscriptions screen.
- Open timeline → article as separate destinations. Back restores the previous list position; incoming links and notifications build a sensible back stack.
- Keep read/unread, star and next unread readily available in the article view. Put less frequent actions, themes and feed details in named menus.
- Use long-press for multiple selection. Optional swipe actions supplement visible commands. Bulk destructive actions identify their scope before execution.
- Preserve selected feed/article, scroll position and reader mode through rotation and process recreation. Do not mark an article read merely because a background pane was composed.

### Tablets, foldables and desktop windows

Use available window size, rather than device model, to choose one, two or three panes. Large windows can show subscriptions, timeline and article together; medium windows use list/detail. Preserve the selected item when folding, resizing or entering split screen. Compose's [adaptive list-detail guidance](https://developer.android.com/develop/adaptive-apps/guides/list-detail) supplies the navigation model.

Provide the Windows-style column timeline with sortable headings on sufficiently wide windows. Phones expose equivalent sort controls. Support independent selections in multiple app windows where supported, with shared account repositories and serialized account writes. Include immersive reading, toolbar/sidebar visibility controls and remembered layout preferences.

### Platform replacements and limits

| Windows capability | Android equivalent / limit |
| --- | --- |
| Menu bar, status bar and gear menu | App bars, overflow/context menus and a refresh/count details surface; theme picker remains reachable |
| Inspector and utility windows | Screens, sheets or large-screen panes |
| Folder double-click behavior | Explicit Open Home Page and Mark All Read commands; optional pointer behavior on large screens |
| Open Themes/Data/Cache Folder | Theme management and explicit export/share diagnostics; no promise of browsing private app directories |
| Taskbar unread badge | In-app total/count preferences; launcher dots/counts depend on launcher support and cannot promise a portable numeric badge |
| Browser picker / background opening | Default-browser or explicit installed-handler choice; no guarantee of opening a background tab |
| `feed:`, `feeds:` and app scheme | Intent filters plus URL/share entry points; do not claim the app can become the universal HTTP(S) feed handler |
| Velopack and release/test channel | Google Play production/testing tracks; separately signed APK channel if needed; no in-app executable updater in the Play build |
| Hidden preferences | Preserve meaningful separators, font sizes, suppress-refresh-on-launch and diagnostics settings; replace window-title and double-click-only preferences with explicit actions |
| `wnr`, `--db`, `--data` | Retain the Windows CLI as an engine reference; Android gets debug/test drivers and document import/export, with app-private storage rather than arbitrary database paths |

The Windows CLI is not an Android end-user binary. Apple-only exclusions remain excluded: iCloud/CloudKit, AppleScript, Safari extensions, Handoff and Spotlight. Android widgets and a full podcast player would be separate future scope; enclosure links remain required.

Inoreader and Feedly remain on hold indefinitely, following [FUTURE-SYNC-SERVICES.md](FUTURE-SYNC-SERVICES.md). Do not offer them in Add Account or include them in estimates. Resuming requires usable access, confirmed terms and a mobile authentication design; embedded client secrets cannot be treated as confidential.

## Data, refresh and synchronization

Keep an account registry, per-account article stores, settings, icon cache, installed themes and optional extracted content in app-private storage. Define explicit cache limits and eviction; reading already-downloaded feed content must work offline. Full-page extraction and uncached remote images may require connectivity. Starred items and pending mutations must survive cleanup.

Preserve feed/article identity, server IDs, local rename overrides and folder membership semantics. Do not use a URL alone as a universal article identifier. Define Today using the user's local day and test timezone changes. Run common parser/store vectors on Windows and Android to detect drift.

Write local read/star state and an outgoing mutation in one transaction. Drain a durable outbox with retry/backoff, pagination checkpoints and idempotent operations where the protocol permits. Serialize each account's sync, coalesce pending state changes safely, and define how newer local intent wins over stale fetched state. Keep remote subscription operations recoverable and report partial cross-account transfers; never remove the source subscription before destination creation succeeds.

Refresh at launch unless disabled, on explicit request, and at the selected manual/30-minute/1/2/4/8-hour preference. WorkManager periodic execution is inexact and can be delayed by battery restrictions; label background intervals accordingly, with last successful refresh and pending work visible. Use network constraints, avoid overlapping foreground/background refresh, and cancel work when an account is disabled or removed. See Android's [work request guidance](https://developer.android.com/develop/background-work/background-tasks/persistent/getting-started/define-work) and [PeriodicWorkRequest contract](https://developer.android.com/reference/androidx/work/PeriodicWorkRequest).

Notifications describe new articles discovered during refresh, not guaranteed immediate publication alerts. Ask for notification permission when users enable notifications and keep reading functional after denial. Deduplicate by account/article identity, use immutable action identities, and make actions safe after process death.

Exclude credentials and account databases from automatic cloud backup by default until restore, key loss and outbox replay behavior are designed and tested. OPML export remains available. A complete backup/restore format is a separate decision and must not be implied by OPML support.

## Article and credential security

Treat feeds, article HTML, themes and extraction pages as untrusted input. Sanitize active content, enforce content rules and navigation policies, and keep JavaScript preference/special-case behavior compatible with the baseline. Avoid privileged JavaScript bridges in article content. Serve app assets through a controlled local origin; block arbitrary file/content access and unsafe URI launches. Test that scripts cannot read credentials, private files or other accounts' content. Android's [security checklist](https://developer.android.com/privacy-and-security/security-tips) identifies WebView content as an injection boundary.

Validate imported theme paths, expanded archive size and file count. Adapt Apple/Windows font names to Android system, monospace and serif families without modifying the imported files. Keep WebView fallback extraction isolated from authenticated account cookies and bound its execution time; never leave a hidden browser permanently running solely to mirror the desktop preload preference.

Use [Android Keystore](https://developer.android.com/privacy-and-security/keystore) for encryption key protection, storing encrypted credential values separately. Handle device/key invalidation by requesting sign-in. Redact credentials, authorization headers, cookies and sensitive URL parameters from diagnostics. Require HTTPS for authenticated services by default; decide and document any narrowly scoped legacy HTTP/self-hosted exception rather than silently downgrading or bypassing certificate failures.

## Proposed module layout

```text
android/
  app/                 application, navigation, dependency assembly, intents
  core/model/          article/feed/account IDs and shared contracts
  core/parser/         feed, HTML metadata and OPML parsing
  core/database/       Room schema, migrations, FTS and durable outbox
  core/network/        downloads, throttling, discovery and icon fetching
  core/accounts/       local account, repositories and sync orchestration
  core/reader/         rendering, extraction, theme import and content rules
  core/preferences/    settings and credential abstractions
  sync/readerapi/      FreshRSS, The Old Reader and BazQux variants
  sync/feedbin/        Feedbin adapter
  sync/newsblur/       NewsBlur adapter
  feature/             subscriptions, timeline, article, settings, diagnostics
  testing/             shared fixtures, fake servers and integration drivers
```

Keep protocol modules independent of Compose. Inject clocks, connectivity, credential storage and HTTP clients. Record the upstream NetNewsWire revision and WinNewsReader revision used for each ported subsystem. When Android behavior needs clarification beyond repository documentation, inspect NetNewsWire through `gh api repos/Ranchero-Software/NetNewsWire/contents/<path>` and record the exact source; do not guess from platform conventions.

## Implementation phases

Planning estimate: 23–36 engineer-weeks for one experienced Android developer, including validation, excluding service approval waits. This is a budget range, not a delivery promise; revise after Phase 0 and the first service integration.

| Phase | Estimate | Work and exit gate |
| --- | --- | --- |
| 0. Inventory and spikes | 2–3 weeks | Resolve Kotlin versus .NET reuse; build/device spike; benchmark parser/search and extraction; choose minimum SDK; create Android parity checklist with disposition and test for every baseline row |
| 1. Portable local engine | 4–6 weeks | Parser, downloader, database, discovery, icons, OPML, search and local account; golden identities match Windows and data survives restart |
| 2. Daily-use local reader | 4–6 weeks | Phone/adaptive shell, feeds/folders, smart feeds, reading state, undo, multiple selection, offline reading and settings; local beta is usable daily |
| 3. Article experience | 3–4 weeks | Templates, nine themes, theme import, Reader View/fallback, find, content rules, media and font mapping; visual and security cases pass |
| 4. All supported sync services | 5–8 weeks | Reader API variants, Feedbin and NewsBlur; credentials, outbox, retries and account editing; each adapter passes fake-server and real-account round trips |
| 5. Android integration and parity | 3–5 weeks | Background work, notifications, file/share/link entry points, keyboard/pointer support, wide column layout, utilities and multi-window verification |
| 6. Release readiness | 2–4 weeks | Accessibility, migrations, performance/battery checks, signing, store material/privacy, testing tracks and production release rehearsal |

Track work with these initial gates:

- [ ] Approve implementation stack, branding, minimum SDK and repository arrangement.
- [ ] Create `android-parity-matrix.md`: Preserve / Adapt / On hold / Exclude, implementation status, evidence and reason per baseline row; maintain its own counts.
- [ ] Complete shared golden fixture corpus and document parser identity compatibility.
- [ ] Ship an internal local-account build after Phase 2.
- [ ] Ship an internal sync build after Phase 4; complete five-service real-account validation.
- [ ] Close all required parity, accessibility, migration and lifecycle tests.
- [ ] Publish a signed release through the selected channels and verify an upgrade from the previous test build.

## Verification and release gates

Use the same OPML, article fixtures and test accounts on Windows and Android. Do not copy the Windows matrix's Done labels into the Android checklist.

- **Engine:** malformed/large/non-UTF-8 feeds, duplicate identities, redirects, discovery choices, 304s, throttling, cancelled downloads and OPML round trips.
- **State:** undo/redo for read/star and subscription deletion, stable sorted mark-above/below behavior, cross-account counts, local-day boundaries and search prefix semantics.
- **Sync:** every service's login/edit/re-authentication, pagination, folder operations, duplicates, offline changes, conflicting devices, failed partial batches, 401/429/5xx responses and restart during upload. Validate both directions against Windows with disposable accounts.
- **Lifecycle:** process death in article/import/refresh, rotation, folding, resize, notification cold launch, force-stop/relaunch, offline startup, low storage and account removal during work.
- **Rendering/security:** nine themes, all text sizes, light/dark modes, unusual scripts, blocked navigation, archive traversal, extraction timeouts, video restrictions and missing images.
- **Device coverage:** minimum supported Android, current stable Android at release, a constrained-memory phone, tablet/foldable, hardware keyboard/mouse, TalkBack and an OEM device with aggressive battery management.
- **Performance:** establish budgets in Phase 0 using at least 1,000 feeds and 100,000 cached articles; record cold start, scroll smoothness, search latency, refresh traffic, database size and idle battery use on named devices.
- **Distribution:** signed AAB for Play testing/production; optional signed APK with a documented signing identity and update path. Verify current target SDK, permissions and store rules at release rather than assuming today's requirements. Keep signing material in release secrets.

CI runs Android checks independently of Windows builds. For later shared-engine changes, run the repository's required `dotnet test` as well as affected Android checks. This document-only proposal changes no runtime behavior and requires no app build.

Release requires no unresolved required parity rows, no known data-loss or credential-exposure defects, successful database migrations, five-service real-account evidence and a completed manual accessibility pass. Before shipping, add Android-specific setup/help/privacy material and clarify platform differences on the website. Keep the existing Windows feature and parity documents accurate for Windows throughout implementation.

## Decisions to settle in Phase 0

1. Product name, application ID, Play publisher account and whether Android lives under `android/` here or in a separate repository.
2. Final language/runtime after the reuse spike, and how common fixtures and rendering assets are versioned across platforms.
3. Minimum Android version, measurable performance budgets, retention defaults and cache limits.
4. Whether a direct APK channel is needed, and its relationship to Play signing and upgrades.
5. Exact cross-window behavior, legacy HTTP policy and whether complete local backup/restore belongs in the first release.

These decisions do not reopen Feedly/Inoreader or expand the plan into a new sync backend. Once resolved, update the estimates and Android checklist before implementation begins.
