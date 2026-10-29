# Future sync services: Inoreader and Feedly

Feedbin, NewsBlur, The Old Reader, BazQux and FreshRSS are built and need no key from anyone. Inoreader and Feedly are not built, because each needs API access that isn't free or isn't simply given out. This page records what we know, so the work can start when access exists. Matrix rows AC-04 (Inoreader) and AC-08 (Feedly) are marked "Keys".

## How NetNewsWire does it

NetNewsWire registered one app with each service. The app ID and key (Inoreader) and client ID and secret (Feedly) are build-time secrets, not in the repository: `Modules/Secrets/Sources/Secrets/SecretKey.swift.gyb` lists `INOREADER_APP_ID`, `INOREADER_APP_KEY`, `FEEDLY_CLIENT_ID` and `FEEDLY_CLIENT_SECRET`, read from environment variables at build time and lightly scrambled in the binary. Users only sign in with their own account; they never see the keys. We would do the same, with GitHub Actions repository secrets feeding the release workflow. A build from source without the secrets would simply not offer these two services.

## Inoreader

**What it needs:** an App ID and an App Key, registered at <https://www.inoreader.com/developers/register-app> (also Preferences → Developer in an Inoreader account). Docs: <https://www.inoreader.com/developers/app-auth>.

**Not free.** Inoreader's Applications page says API access is granted to developers creating publicly available apps on platforms where Inoreader is unavailable. For all other use cases, an Inoreader Pro plan is required. WinNewsReader is free and public, but Inoreader has a web app that runs on Windows, so we may not qualify for the free route. The page does not say whether the Pro plan is needed only by the registering developer or by every user. Not yet confirmed either way.

**Other points**
- The docs say requiring each user to enter their own App ID and Key is not the correct design, hence the shared build-time key.
- Rate limits are per app, in zones (zone 1 is reads), reported on every response: <https://www.inoreader.com/developers/rate-limiting>. One key shared by all users could reach them.
- The API is the Reader API we already have for FreshRSS, The Old Reader and BazQux, with two extra request headers, `AppId` and `AppKey`. NetNewsWire's `ReaderAPIVariant.inoreader` also has some special handling and reads the rate-limit headers.

**Next steps**
1. Write to Inoreader support: free and open-source Windows port of NetNewsWire, no native Windows Inoreader client, sync through the user's own account. Ask whether it qualifies for free API access, and whether users need Pro too.
2. If not, decide whether a Pro plan is worth it for one service. Ask whether the key keeps working if the plan lapses.
3. Once there are keys: add them as the repository secrets above, add an `Inoreader` variant to `ReaderApiVariant` that sends the headers, add it to `AccountKinds.Addable` under Web, and add tests against the fake Reader API server.

## Feedly

**What it needs:** Feedly Cloud API access: a client ID and secret for OAuth, as NetNewsWire has. Docs: <https://developers.feedly.com/reference/introduction> and <https://developers.feedly.com/reference/authorization>.

**Not self-service.** No public sign-up form for an app registration was found. The docs describe generating an access token from a page in the user's account, which is only offered on some paid or Enterprise plans, and say to contact Feedly if the page isn't available. An older developer page mentions a developer token for hobbyists with a Pro plan and a sandbox before production access, but it may be out of date. Feedly's contact address was hidden in the search results, so use the contact link on the developer site.

**Next steps**
1. Write to Feedly: open-source Windows port of NetNewsWire, reading feeds through each user's own Feedly account, project site winnewsreader.com, the redirect URI we'd use. Ask for production Cloud API access and a client ID and secret.
2. Check whether users need a paid Feedly plan to use it.
3. If granted: add the secrets, build a Feedly account (OAuth sign-in in a browser, token refresh, the streams and markers API, which is different from the Reader API, so a new project like Feedbin and NewsBlur), then add it to the Add Account list.

## Requests sent

- 2026-10-29: access requests sent to Inoreader's support team and to Feedly's sales team, describing WinNewsReader as a free, open-source Windows port of NetNewsWire. Inoreader was asked whether it qualifies for free API access, whether users need Pro, and what the rate limits are. Feedly was asked how to apply for production Cloud API access, and which plan users need.
- Replies: none yet. Record them here, and update the matrix rows AC-04 (Inoreader) and AC-08 (Feedly).

## Status

Neither is started. Inoreader is the smaller piece of work once keys exist, since it reuses the Reader API client. Feedly is a new client. Neither blocks anything else.
