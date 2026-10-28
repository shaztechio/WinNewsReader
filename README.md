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

It refreshes at launch and every hour. Add feeds with **New** (or Ctrl+N), or import an OPML file from the same menu. The shortcuts follow NetNewsWire's, with Ctrl for Cmd: Ctrl+R refresh, Ctrl+K mark all as read, Ctrl+Shift+U mark read or unread, Ctrl+Shift+L star, Ctrl+/ next unread, Ctrl+1, 2 and 3 for the smart feeds. Pass `-- --db <path>` to open a different database.

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
