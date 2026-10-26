# Privacy Policy — WinForge

_Last updated: 26 October 2026_

WinForge is a free, open-source Windows utility (MIT licensed). The short version:
**it does not collect anything about you.** There are no accounts, no telemetry, no
analytics, no advertising and no servers of our own — the application has no backend.

## 1. What is never collected

- No user accounts, sign-in, e-mail addresses or identifiers of any kind.
- No telemetry, usage statistics, analytics, crash reports or advertising IDs.
- No screenshots, keystrokes or clipboard content.
- No data is sold, shared or transferred to third parties.

## 2. Data stored on your own device

WinForge stores its data locally, in `%LocalAppData%\WHPO`:

- **Settings and preferences** (theme, language, window position, component state).
- **Optimization state and backups:** before a documented Windows setting is changed,
  its previous value is captured so the change can be reverted from the app.
- **Logs** of the actions the app performed, so you can review what changed.
- **Cached downloads:** game cover art, the optimization catalog and language packs.

None of this leaves your computer. Uninstalling WinForge removes the program; the
folder above can be deleted at any time to erase all remaining data.

## 3. Network connections

The application contacts only the following hosts, and only for the purposes listed:

| Host | Purpose |
|---|---|
| `github.com`, `api.github.com`, `raw.githubusercontent.com`, `objects.githubusercontent.com` | Optional update check against GitHub Releases; downloading the feature catalog, language packs and tools the user explicitly requests. |
| `cdn.cloudflare.steamstatic.com`, Epic Games CDN, Battle.net CDN | Cover art for games already installed on your PC, used by the library view. |
| Vendor download pages | Opened in the browser only when you click them. |

These requests carry no user identifiers beyond the IP address and HTTP headers inherent
to any web request. No cookies are used for tracking and no personal data is included.
The settings you change are never transmitted anywhere.

## 4. System changes

WinForge applies standard, documented Windows settings through official APIs, with your
explicit confirmation and with administrator rights where the operating system requires
them. The previous values are saved locally so that every change can be reverted. Nothing
about those changes is reported to us or to anyone else.

## 5. Third parties

- **Windows SmartScreen / Microsoft Defender** evaluate downloaded files at the operating
  system level. This is performed by Windows, not by WinForge.
- **GitHub** hosts the releases and the source code; downloads are subject to GitHub's own
  privacy statement.

## 6. Changes and contact

Any change to this policy is published in this file, in the public repository, with the
date updated above. Questions or concerns can be raised at
<https://github.com/Maximiliano-Fallini/WinForge/issues>.
