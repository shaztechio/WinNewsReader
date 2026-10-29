# Using WinNewsReader

A tour of what the app does and how to drive it. The same material, with screenshots, is on the [help page](https://winnewsreader.com/help/). Shortcuts follow NetNewsWire's, with Ctrl for Cmd.

The first launch adds NetNewsWire's starter feeds. The app refreshes at launch and every hour. It remembers its window, pane widths, open folders and selection between runs. To use a different data folder, start it with `--data <folder>`.

## The window

- **Menu bar** along the top, laid out like NetNewsWire's: File, Edit, View, Go, Article, Window and Help, with each item's shortcut beside it. Help (F1) opens the help site.
- **Toolbar**, in three groups over the three panes:
  - over the sidebar: Toggle Sidebar (Ctrl+Alt+S), Refresh, and a **+** button for a new feed or folder
  - over the timeline: Mark All as Read and Hide Read Articles for the selected feed
  - over the article: Mark Read, Star, Next Unread, Reader View, Share (Windows' share sheet) and Open in Browser, then the search box
- **Status bar:** a gear menu for the selected sidebar row, a line that cycles through the last refresh, the unread count and the feed and folder counts when clicked, and the article theme menu on the right.
- **Column layout** (View → Use Column Layout): the timeline becomes a table across the top, with the article below it, both full width. Click a column heading to sort by it, and again to reverse.

## Reading

- Ctrl-click or Shift-click articles to select several (Ctrl+A selects all). The article pane shows them as a stack, and Mark Read, Star, Share, Open in Browser and the right-click menu act on all of them.
- The article right-click menu also has Mark Above as Read, Mark Below as Read, Select “Feed” in Sidebar and Mark All as Read in “Feed”.
- **Reader View** (Ctrl+Shift+R) fetches the article's page and shows just the article. A feed's **Always Use Reader View** turns it on for every article in that feed.
- **Find in Article** is Ctrl+F (F3 and Shift+F3 step through matches). F11 toggles full screen.
- **Search** (Ctrl+Alt+F) covers every article in every account, by title, text and author, matching the start of each word. Escape, or choosing a sidebar row, ends the search.
- **Undo:** Ctrl+Z undoes marking read or starred and deleting feeds and folders; Ctrl+Y redoes.
- **Themes:** View → Article Theme and View → Text Size. Add a theme with **Install Theme…** (a zipped theme, or the `Info.plist` inside a `.nnwtheme` folder), **Install Theme from URL…**, or by putting its `.nnwtheme` folder in the folder **Open Themes Folder** opens. Themes are written for the Mac and name Apple's fonts, so when one loads its fonts are swapped for Windows ones: `-apple-system` becomes Segoe UI Variable, Menlo becomes Cascadia Mono or Consolas, and `ui-serif` becomes Georgia. The theme's files aren't changed.
- **View → Enable JavaScript** turns off scripts in articles (they stay off for a few sites that break with them, as in NetNewsWire).
- Feeds show their own icon, or their site's largest touch icon, or its favicon, cached in `%LOCALAPPDATA%\WinNewsReader\Favicons`.

## Feeds and folders

- Drag a feed onto a folder to move it, or use **Move to** on its right-click menu. Ctrl-drag copies a local feed into a second folder.
- A renamed feed keeps its name through refreshes. On a sync account the subscription is renamed on the server too.
- **Get Info** (right-click, or Ctrl+I) opens the Inspector for a feed or folder: rename it, see its home page and feed URLs, and turn on Always Use Reader View or Show Notifications for New Articles. With notifications on, each new unread article shows a Windows notification while the app is running; click it to read the article, or use Mark as Read or Mark as Starred. A notification goes away once its article is read.
- Dragging a feed onto another account subscribes to it there and removes it here, or keeps both with Ctrl, as in NetNewsWire.

## Accounts

Choose **File → Add Account…**, pick a service and sign in. Passwords are kept in Windows Credential Manager, not in a file.

| Service | Sign in with |
| --- | --- |
| Feedbin, The Old Reader, BazQux | Email and password |
| NewsBlur | Username and password |
| FreshRSS | Your server's address, username, and the API password from FreshRSS's profile settings (`/api/greader.php` is added to the address for you) |

Preferences → Accounts turns accounts on and off, and edits or removes them. Editing changes an account's name, server, username or password; a blank password keeps the saved one. Account names must differ (a second FreshRSS account is “FreshRSS 2”), and the same login can't be added twice, as in NetNewsWire. Inoreader and Feedly are not supported yet; see [FUTURE-SYNC-SERVICES.md](FUTURE-SYNC-SERVICES.md).

## Preferences

**File → Preferences…** (Ctrl+,) has Appearance (Same as Windows, Light or Dark, articles included), Unread Counts (number, dot or none, for the sidebar and for the taskbar badge, which shows the total of all unread articles, as 99+ past 99), article text size and theme, Enable JavaScript, the browser that opens links, how often to refresh (manually, or every 30 minutes to 8 hours), the Accounts tab, and an Advanced tab for updates.

## Keyboard

Works from any pane, including while reading an article.

- Space pages through the article, then goes to the next unread one. `n` is next unread, `-` previous unread.
- `r` toggles read, `s` toggles star, `k` marks all as read, `m` marks unread and moves on, `l` marks all read and moves on, `o` marks older articles read.
- `b` or Enter opens the article in the browser. `a` and `z` move to the previous and next feed.
- Left and Right move between the sidebar, the timeline and the article. In the sidebar, `,` and `.` collapse and expand a folder, and `;` and `'` collapse and expand all.
- Ctrl+R refresh, Ctrl+N new feed, Ctrl+K mark all as read, Ctrl+Shift+U and Ctrl+Shift+L toggle read and star, Ctrl+Shift+H hide read articles, Ctrl+Shift+F hide read feeds, Ctrl+' clean up, Ctrl+1, 2 and 3 for the smart feeds.

## The command-line tool

`wnr` is a headless driver for the same engine, handy for testing.

```bash
dotnet run --project src/Cli -- subscribe daringfireball.net
dotnet run --project src/Cli -- refresh
dotnet run --project src/Cli -- unread
```

`subscribe` takes a feed URL or a web page; for a page it finds the feed the way NetNewsWire does. Add `--folder <name>` to put it in a folder. `import` and `export` read and write OPML with folders. Run `dotnet run --project src/Cli -- help` for every command. The app and `wnr` share the local account's data, in `%LOCALAPPDATA%\WinNewsReader\OnThisPC\articles.db`; `wnr` takes `--db <path>` to use another database.
