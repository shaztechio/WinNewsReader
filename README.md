# WinNewsReader

A native Windows feed reader aiming for feature parity with the [NetNewsWire](https://github.com/Ranchero-Software/NetNewsWire) Mac app. See [docs/PLAN.md](docs/PLAN.md) for the plan and [docs/parity-matrix.md](docs/parity-matrix.md) for the feature inventory.

<img src="src/App/Assets/AppIcon.png" width="96" align="right" alt="">

Status: Phase 2 (the Windows app) has started. Phase 1 (the core engine) is done. The app has the three-pane window over the local account and FreshRSS sync, and Phase 3's article themes and Reader View are in; the `wnr` command-line tool drives the same engine.

## Layout

| Project | What it holds |
| --- | --- |
| `src/Parser` | RSS, RSS 1.0 (RDF), Atom, JSON Feed, RSS-in-JSON and OPML parsers, plus HTML link and metadata parsers, ported from NetNewsWire's RSParser so the same feed gives the same article IDs |
| `src/Store` | SQLite store for feeds, folders, articles and read/starred status, following NetNewsWire's schema |
| `src/Core` | Downloading (conditional GET, per-host limits, redirects), the feed finder, favicons, OPML export, settings, the account interface with the local "On This PC" account, and article rendering with NetNewsWire's templates and themes |
| `src/Sync.GoogleReader` | FreshRSS sync over the Google Reader API, ported from NetNewsWire's ReaderAPI account |
| `src/App` | The WinUI 3 app (Windows App SDK, unpackaged): sidebar, timeline and article view |
| `src/Cli` | `wnr`, a headless driver for the core |
| `tools` | `make-icon.ps1`, which draws the app icon |
| `site` | The website at [winnewsreader.com](https://winnewsreader.com), published to GitHub Pages by `.github/workflows/pages.yml` when it changes on `main` |
| `tests/Parser.Tests` | NetNewsWire's parser tests ported to xUnit, run against its fixtures |
| `tests/Store.Tests` | Store, folder, OPML round-trip, refresh, download and feed finder tests |
| `tests/Sync.GoogleReader.Tests` | FreshRSS sync against an in-memory Reader API server |

## Build and test

Needs the .NET 10 SDK on Windows 10 1809 or later. The app also needs the WebView2 runtime, which Windows 11 includes.

```bash
dotnet test
```

## Install

There is no public release yet. The installer is built with [Velopack](https://velopack.io) (see [docs/RELEASING.md](docs/RELEASING.md)):
it installs for the current user without administrator rights, sets up the WebView2 runtime if needed, registers `feed:` links and
`.nnwtheme` files, and updates itself from winnewsreader.com. It isn't code-signed yet, so Windows SmartScreen will warn about it.

## Try it

Run the app:

```bash
dotnet run --project src/App
```

The first launch adds NetNewsWire's starter feeds. The app refreshes at launch and every hour. The toolbar is laid out like NetNewsWire 7's, in three groups over the three panes. Over the sidebar: Toggle Sidebar (Ctrl+Alt+S) on the left, and Refresh and a **+** button for a new feed or folder on the right. Above it all is a menu bar laid out like NetNewsWire's: File, Edit, View, Go, Article, Window and Help, with each item's shortcut beside it. Help (F1) opens the help at [winnewsreader.com/help](https://winnewsreader.com/help/). Over the timeline: Mark All as Read and Hide Read Articles for the selected feed. Over the article: Mark Read, Star, Next Unread, Reader View, Share (Windows' share sheet) and Open in Browser, then the search box. Ctrl-click or Shift-click articles to select several (Ctrl+A selects them all); the article pane shows them as a stack, and Mark Read, Star, Share, Open in Browser and the right-click menu act on them all. The right-click menu also has Mark Above as Read and Mark Below as Read, Select “Feed” in Sidebar and Mark All as Read in “Feed”, as in NetNewsWire. **File → Preferences…** (Ctrl+,) has Appearance (Same as Windows, Light or Dark, articles included), Unread Counts (number, dot or none, for the sidebar and for the badge on the taskbar icon, which shows the total of all unread articles, as 99+ past 99), article text size and theme, Enable JavaScript, the browser that opens links, how often to refresh (manually, or every 30 minutes to 8 hours), and an Accounts tab to turn accounts on and off, add FreshRSS, edit an account's name, server, username or password (a blank password keeps the saved one), and remove accounts. Account names are kept different (a second FreshRSS account is “FreshRSS 2”), and the same server and username can't be added twice, as in NetNewsWire. The app remembers its window, pane widths, open folders and selection between runs. Drag a feed onto a folder to move it, or use **Move to** on its right-click menu; Ctrl-drag copies a local feed into a second folder. The search box (Ctrl+Alt+F) searches every article in every account, by title, text and author, matching the start of each word; Escape or choosing a sidebar row ends the search. **Get Info** on the right-click menu, or Ctrl+I, opens the Inspector for the selected feed or folder: rename it, see its home page and feed URLs, and turn on Always Use Reader View or Show Notifications for New Articles, which are also on the feed's right-click menu. With notifications on, each new unread article in the feed shows a Windows notification while the app is running; click it to read the article, or use Mark as Read or Mark as Starred. A notification goes away once its article is read, and the app clears its notifications when it closes. A renamed feed keeps its name through refreshes, and in FreshRSS the subscription is renamed on the server too. Dragging a feed onto another account subscribes to it there and removes it here, or keeps both with Ctrl, as in NetNewsWire. To sync with FreshRSS, choose **File → Add FreshRSS Account…** and enter the server address, your username and the API password from FreshRSS's profile settings; the password is kept in Windows Credential Manager. The keyboard follows NetNewsWire's, with Ctrl for Cmd, and works from any pane, including while reading an article:

- Space pages through the article, then goes to the next unread one. `n` is next unread, `-` previous unread.
- `r` toggles read, `s` toggles star, `k` marks all as read, `m` marks unread and moves on, `l` marks all read and moves on, `o` marks older articles read.
- `b` or Enter opens the article in the browser. `a` and `z` move to the previous and next feed.
- Left and Right move between the sidebar, the timeline and the article. In the sidebar, `,` and `.` collapse and expand a folder, and `;` and `'` collapse and expand all.
- Ctrl+R refresh, Ctrl+N new feed, Ctrl+K mark all as read, Ctrl+Shift+U and Ctrl+Shift+L toggle read and star, Ctrl+Shift+H hide read articles, Ctrl+Shift+F hide read feeds, Ctrl+' clean up, Ctrl+1, 2 and 3 for the smart feeds.

Ctrl+Z undoes marking read or starred and deleting feeds and folders (Ctrl+Y redoes). Find in Article is Ctrl+F (F3 and Shift+F3 step through matches) and F11 toggles full screen. Feeds show their own icon, or their site's largest touch icon, or its favicon, cached in `%LOCALAPPDATA%\WinNewsReader\Favicons`. Articles render with NetNewsWire's templates and themes: pick one from **View → Article Theme**, and the text size from **View → Text Size**. To add a theme, use **Install Theme…** (a zipped theme, or the `Info.plist` inside a `.nnwtheme` folder) or **Install Theme from URL…** (a zipped theme on the web), or put its `.nnwtheme` folder in the folder **Open Themes Folder** opens. Themes are written for the Mac and name Apple's fonts, so when one loads, its fonts are swapped for Windows ones: the system font (`-apple-system`) becomes Segoe UI Variable, Menlo becomes Cascadia Mono or Consolas, and `ui-serif` becomes Georgia. The theme's files aren't changed. **View → Enable JavaScript** turns off scripts in articles (they stay off for a few sites that break with them, as in NetNewsWire). **Reader View** (Ctrl+Shift+R) fetches the article's page and shows just the article; a feed's **Always Use Reader View** menu item turns it on for every article in that feed. Pass `-- --data <folder>` to use a different data folder.

Or use the command-line tool:

```bash
dotnet run --project src/Cli -- subscribe daringfireball.net
```

```bash
dotnet run --project src/Cli -- refresh
```

```bash
dotnet run --project src/Cli -- unread
```

`subscribe` takes a feed URL or a web page; for a page it finds the feed the way NetNewsWire does. Add `--folder <name>` to put it in a folder. `import` and `export` read and write OPML with folders. Run `dotnet run --project src/Cli -- help` for every command. The app and `wnr` share the local account's data, in `%LOCALAPPDATA%\WinNewsReader\OnThisPC\articles.db`; `wnr` takes `--db <path>` to use another database.

## Credits

Parser code and test fixtures, and the article template, stylesheets, scripts and built-in themes in `src/Core/ArticleRendering/Resources`, are ported from NetNewsWire by Brent Simmons and contributors, under the MIT license ([src/Parser/LICENSE-NetNewsWire.txt](src/Parser/LICENSE-NetNewsWire.txt)). The NewsFax theme's ModeSeven font is freeware; see its `License.md`. Reader View uses [SmartReader](https://github.com/Strumenta/SmartReader), a C# port of Mozilla's Readability, under the Apache License 2.0. This project isn't affiliated with or endorsed by NetNewsWire.
