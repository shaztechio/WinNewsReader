# WinNewsReader

A native Windows feed reader aiming for feature parity with the [NetNewsWire](https://github.com/Ranchero-Software/NetNewsWire) Mac app. See [docs/PLAN.md](docs/PLAN.md) for the plan and [docs/parity-matrix.md](docs/parity-matrix.md) for the feature inventory.

<img src="src/App/Assets/AppIcon.png" width="96" align="right" alt="">

Status: Phase 2 (the Windows app) has started. Phase 1 (the core engine) is done. The app has the three-pane window over the local account; the `wnr` command-line tool drives the same engine.

## Layout

| Project | What it holds |
| --- | --- |
| `src/Parser` | RSS, RSS 1.0 (RDF), Atom, JSON Feed, RSS-in-JSON and OPML parsers, plus HTML link and metadata parsers, ported from NetNewsWire's RSParser so the same feed gives the same article IDs |
| `src/Store` | SQLite store for feeds, folders, articles and read/starred status, following NetNewsWire's schema |
| `src/Core` | Downloading (conditional GET, per-host limits, redirects), the feed finder, OPML export, and the local "On This PC" account |
| `src/App` | The WinUI 3 app (Windows App SDK, unpackaged): sidebar, timeline and article view |
| `src/Cli` | `wnr`, a headless driver for the core |
| `tools` | `make-icon.ps1`, which draws the app icon |
| `site` | The website at [winnewsreader.com](https://winnewsreader.com), published to GitHub Pages by `.github/workflows/pages.yml` when it changes on `main` |
| `tests/Parser.Tests` | NetNewsWire's parser tests ported to xUnit, run against its fixtures |
| `tests/Store.Tests` | Store, folder, OPML round-trip, refresh, download and feed finder tests |

## Build and test

Needs the .NET 10 SDK on Windows 10 1809 or later. The app also needs the WebView2 runtime, which Windows 11 includes.

```bash
dotnet test
```

## Try it

Run the app:

```bash
dotnet run --project src/App
```

The first launch adds NetNewsWire's starter feeds. The app refreshes at launch and every hour. Add feeds with **New** (or Ctrl+N), or import an OPML file from the same menu. The filter button hides read articles in the selected feed, and the **…** menu has Hide Read Feeds, the sort order and Clean Up. The app remembers its window, pane widths, open folders and selection between runs. The keyboard follows NetNewsWire's, with Ctrl for Cmd, and works from any pane, including while reading an article:

- Space pages through the article, then goes to the next unread one. `n` is next unread, `-` previous unread.
- `r` toggles read, `s` toggles star, `k` marks all as read, `m` marks unread and moves on, `l` marks all read and moves on, `o` marks older articles read.
- `b` or Enter opens the article in the browser. `a` and `z` move to the previous and next feed.
- Left and Right move between the sidebar, the timeline and the article. In the sidebar, `,` and `.` collapse and expand a folder, and `;` and `'` collapse and expand all.
- Ctrl+R refresh, Ctrl+N new feed, Ctrl+K mark all as read, Ctrl+Shift+U and Ctrl+Shift+L toggle read and star, Ctrl+Shift+H hide read articles, Ctrl+Shift+F hide read feeds, Ctrl+' clean up, Ctrl+1, 2 and 3 for the smart feeds.

Feeds show their site's favicon, cached in `%LOCALAPPDATA%\WinNewsReader\Favicons`. Pass `-- --db <path>` to open a different database.

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

`subscribe` takes a feed URL or a web page; for a page it finds the feed the way NetNewsWire does. Add `--folder <name>` to put it in a folder. `import` and `export` read and write OPML with folders. Run `dotnet run --project src/Cli -- help` for every command. The app and `wnr` share their data, in `%LOCALAPPDATA%\WinNewsReader\OnThisPC\articles.db` unless you pass `--db <path>`.

## Credits

Parser code and test fixtures are ported from NetNewsWire by Brent Simmons and contributors, under the MIT license ([src/Parser/LICENSE-NetNewsWire.txt](src/Parser/LICENSE-NetNewsWire.txt)). This project isn't affiliated with or endorsed by NetNewsWire.
