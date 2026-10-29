# Repository layout

| Project | What it holds |
| --- | --- |
| `src/Parser` | RSS, RSS 1.0 (RDF), Atom, JSON Feed, RSS-in-JSON and OPML parsers, plus HTML link and metadata parsers, ported from NetNewsWire's RSParser so the same feed gives the same article IDs |
| `src/Store` | SQLite store for feeds, folders, articles and read/starred status, following NetNewsWire's schema |
| `src/Core` | Downloading (conditional GET, per-host limits, redirects), the feed finder, favicons, OPML export, settings, the account interface with the local "On This PC" account, and article rendering with NetNewsWire's templates and themes |
| `src/Sync.GoogleReader` | FreshRSS, The Old Reader and BazQux sync over the Google Reader API, ported from NetNewsWire's ReaderAPI account |
| `src/Sync.Feedbin` | Feedbin sync, ported from NetNewsWire's Feedbin account |
| `src/Sync.NewsBlur` | NewsBlur sync, ported from NetNewsWire's NewsBlur account |
| `src/App` | The WinUI 3 app (Windows App SDK, unpackaged): sidebar, timeline and article view |
| `src/Cli` | `wnr`, a headless driver for the core |
| `tools` | `make-icon.ps1`, which draws the app icon |
| `site` | The website at [winnewsreader.com](https://winnewsreader.com), published to GitHub Pages by `.github/workflows/pages.yml` when it changes on `main` |
| `tests/Parser.Tests` | NetNewsWire's parser tests ported to xUnit, run against its fixtures |
| `tests/Store.Tests` | Store, folder, OPML round-trip, refresh, download and feed finder tests |
| `tests/Sync.GoogleReader.Tests` | Reader API sync against an in-memory Reader API server |
| `tests/Sync.Feedbin.Tests` | Feedbin sync against an in-memory Feedbin server |
| `tests/Sync.NewsBlur.Tests` | NewsBlur sync against an in-memory NewsBlur server |

Each sync account has its own article store under `%LOCALAPPDATA%\WinNewsReader\Accounts\<id>`. The local "On This PC" account's is `%LOCALAPPDATA%\WinNewsReader\OnThisPC\articles.db`.
