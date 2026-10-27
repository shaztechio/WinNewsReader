# WinNewsReader

A native Windows feed reader aiming for feature parity with the [NetNewsWire](https://github.com/Ranchero-Software/NetNewsWire) Mac app. See [docs/PLAN.md](docs/PLAN.md) for the plan and [docs/parity-matrix.md](docs/parity-matrix.md) for the feature inventory.

Status: Phase 1 (core engine) in progress. There's no UI yet; the `wnr` command-line tool drives the engine.

## Layout

| Project | What it holds |
| --- | --- |
| `src/Parser` | RSS, RSS 1.0 (RDF), Atom, JSON Feed, RSS-in-JSON and OPML parsers, ported from NetNewsWire's RSParser so the same feed gives the same article IDs |
| `src/Store` | SQLite store for feeds, articles and read/starred status, following NetNewsWire's schema |
| `src/Core` | Downloading (conditional GET, per-host limits, redirects) and the local "On This PC" account |
| `src/Cli` | `wnr`, a headless driver for the core |
| `tests/Parser.Tests` | NetNewsWire's parser tests ported to xUnit, run against its fixtures |
| `tests/Store.Tests` | Store, refresh and download tests |

## Build and test

Needs the .NET 10 SDK.

```bash
dotnet test
```

## Try it

```bash
dotnet run --project src/Cli -- subscribe https://daringfireball.net/feeds/main
```

```bash
dotnet run --project src/Cli -- refresh
```

```bash
dotnet run --project src/Cli -- unread
```

Run `dotnet run --project src/Cli -- help` for every command. Data goes to `%LOCALAPPDATA%\WinNewsReader\OnThisPC\articles.db` unless you pass `--db <path>`.

## Credits

Parser code and test fixtures are ported from NetNewsWire by Brent Simmons and contributors, under the MIT license ([src/Parser/LICENSE-NetNewsWire.txt](src/Parser/LICENSE-NetNewsWire.txt)). This project isn't affiliated with or endorsed by NetNewsWire.
