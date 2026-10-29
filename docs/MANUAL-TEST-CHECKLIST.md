# Manual test checklist

The rows in [parity-matrix.md](parity-matrix.md) marked **Unchecked** are built but have only been tested by the author, or not driven in the real app. This list turns them into steps to do by hand, plus the real-account checks for the sync services. Tick a box when it works. When a row is confirmed, change its status in the matrix from Unchecked to Done and update the counts at the top.

Run the app from a build (`dotnet build src/App`, then the exe in `src/App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/`), or install the latest test release. Use `--data <folder>` to point it at a throwaway data folder so your real feeds stay untouched.

## 1. Sync accounts against the real services

The sync code is tested only against fake servers, so each real service needs one pass. Use a spare account where you can.

For each of **Feedbin, NewsBlur, The Old Reader, BazQux, FreshRSS** (File → Add Account…):

- [ ] Wrong password shows an error in the sheet and adds nothing
- [ ] Right password adds the account, with its feeds and folders in the sidebar
- [ ] Refresh brings articles; unread counts match the service's own site
- [ ] Mark an article read here, refresh the service's site: it is read there too
- [ ] Star an article here: starred there; star one there, refresh here: starred here
- [ ] Mark one unread on the site, refresh: unread here
- [ ] Subscribe to a new feed (with a folder): it appears on the site
- [ ] Rename a feed; rename a folder: both changed on the site
- [ ] Move a feed to another folder: moved on the site
- [ ] Remove a feed; remove a folder: gone from the site
- [ ] Preferences → Accounts → Edit: change the name, then the password; the account keeps its feeds
- [ ] Remove the account: its data and saved password go (check Credential Manager)
- [ ] Add the same login twice: refused

Service-specific:

- [ ] Feedbin: a feed in two folders shows in both; copying a feed to a second folder works; removing it from one folder keeps the other
- [ ] NewsBlur: leave the app idle for a long time, then refresh: it logs in again without asking
- [ ] The Old Reader: removing a folder with feeds unsubscribes them (the label may remain on the site)
- [ ] FreshRSS: a title with `&` or `<` reads correctly

## 2. Things that need a mouse

- [ ] **SB-04** Drag a feed to a folder, between folders, and to another account; drag a folder to another account (moves with its feeds)
- [ ] **SB-05** Drag a link or a feed address from a browser onto a sidebar row: the Add Feed sheet picks that row's account and folder
- [ ] **SB-09** Double-click a feed opens its home page; with `DoubleClickFeedMarksRead` in settings.json it marks all read instead (**PR-17**)
- [ ] **SB-10** Right-click empty sidebar space: New Feed and New Folder
- [ ] **SB-14** Ctrl+click and Shift+click several sidebar rows: right-click offers Mark All as Read and Delete
- [ ] **TL-09** Drag one article to a browser (opens the link); drag several (lines of text)
- [ ] **MW-04** Hover a link in an article: its address shows in the status bar

## 3. Sidebar and feeds

- [ ] **SB-06** F2 (and Rename…) edits the name in place; Enter commits, Esc cancels
- [ ] **SB-07** Delete asks first; Ctrl+Z brings it back
- [ ] **FD-01** New Feed with nothing selected offers the account and folder last used (needs a second account to be a real test)
- [ ] **FD-04** Import Subscriptions with two accounts: it asks which; into a sync account it subscribes feed by feed
- [ ] **FD-05** Export Subscriptions with two accounts: it asks which; the file re-imports to the same tree

## 4. Article view and other windows

- [ ] **AV-03** Double-click a real `.nnwtheme` file: it installs and shows in View → Article Theme
- [ ] **AV-09** An article with footnotes: clicking a footnote opens a popover
- [ ] **UW-01** Current Activity shows a refresh while it is running
- [ ] **UW-03** Error Log shows a feed that fails to parse (subscribe to a page that isn't a feed)
- [ ] **NT-03** With the app installed: click a new-article toast; it opens the article, including when the app was not running

## 5. Keyboard shortcuts

Press each with the right window focused. Menu items already work; this checks the keys.

- [ ] Ctrl+Alt+N new window · Ctrl+W close window · Ctrl+0 main window
- [ ] Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z) undo and redo
- [ ] Ctrl+Shift+C copy article URL · Ctrl+Alt+C copy external URL
- [ ] Ctrl+F find in article · F3 / Shift+F3 next and previous match
- [ ] Ctrl+Alt+T show or hide the toolbar · F11 full screen
- [ ] Ctrl+Alt+K mark above as read · Ctrl+Shift+K mark below as read
- [ ] Ctrl+Right open in browser (article focused) · Ctrl+Shift+Right open inverted
- [ ] Ctrl+6, Ctrl+7, Ctrl+8, Ctrl+9 Dinosaurs, Current Activity, Activity Log, Error Log

## 6. Preferences and hidden settings

- [ ] **PR-11** Preferences → Advanced and Help → Check for Updates… check the update feed (needs an installed copy; see [RELEASING.md](RELEASING.md))
- [ ] **PR-16** `ArticleTitleInWindowTitle` in settings.json puts the article title in the window title
- [ ] **PR-18** `SuppressRefreshOnLaunch` stops the refresh at launch

## 7. Installer (a real install of Setup.exe)

The installer is unsigned, so SmartScreen warns first.

- [ ] Setup.exe installs per user, bootstraps WebView2 if missing, and starts the app
- [ ] The app appears in Apps and Features and in the Start menu; uninstall removes it and its registry entries but leaves `%LOCALAPPDATA%\WinNewsReader`
- [ ] `feed:` and `feeds:` links, and `.nnwtheme` files, open in the app; it appears under Default apps
- [ ] A second launch hands over to the running window instead of opening another
- [ ] An update published later is found, downloaded and applied

## 8. Layout checks added recently

- [ ] View → Use Column Layout: the table runs across the top and the article is below, both full width; dragging the bar changes the table's height; turning it off restores the three panes
- [ ] Status bar: the gear has an arrow and opens the selection's menu; the theme button is on the right, shows the theme's name and an arrow, and its menu changes the theme
- [ ] The menu bar's File, Edit, View, Go, Article, Window and Help menus match [the shortcuts in the matrix](parity-matrix.md)
