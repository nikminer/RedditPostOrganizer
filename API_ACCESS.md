# Reddit API access review — Reddit Post Organizer

## Status and purpose

This repository contains a Windows desktop application for a single person to prepare image galleries, schedule their own submissions, and view/download their own published image posts in a local calendar. The application has not yet received Reddit API approval or a client ID. No live posting has been performed during development. Automated tests use in-memory HTTP handlers, not Reddit.

## Authentication and endpoints

OAuth authorization-code flow for an **installed app**, using the system browser, no client secret and no Reddit password collection. A temporary listener binds only to 127.0.0.1:8765. Redirect: `http://127.0.0.1:8765/callback`. Random state is checked; repeated query keys are rejected; the listener closes after completion/cancellation/timeout.

Requested scopes:

| Scope | Purpose |
| --- | --- |
| identity | Confirm the authenticated username with `/api/v1/me` |
| read | Read community gallery permissions and verify submission results |
| submit | Upload user-selected media and submit a prepared gallery |
| flair | Retrieve selectable post flair for the destination community |
| history | Read the authenticated user's submitted image posts |

Source: `src/RedditPostOrganizer.Core/RedditApi.cs`. OAuth token/revoke endpoints on www.reddit.com; authenticated data requests on oauth.reddit.com; community `/about`, `/api/flairselector/`, `/user/{authenticated_user}/submitted`. Native gallery uploads use `/api/media/asset.json` and `/api/submit_gallery_post.json`. These gallery endpoints are not fully covered by Reddit's public endpoint documentation; their request structure follows established client implementations (PRAW 8). Availability for the approved installed app, upload readiness, required permissions and error schemas must be validated after approval. The application does not claim gallery publishing is already certified or tested live.

## Behavior and limits

One account, one sequential executor, no parallel posting. Only user-created local scheduled posts can be sent. Imported historical posts are stored as published and never added to the sending queue. The app does not vote, message users, comment, scrape browser pages, bypass CAPTCHA or change accounts to avoid rate limits. Copying is a local editing convenience, not automatic crossposting.

Rate-limit headers and HTTP 429 are respected. Reddit's posting delay takes precedence when longer than the configured retry delay. Authentication failures suspend the queue. A possibly accepted submission is never retried automatically. Exact uploaded media IDs are used for one bounded read-only verification; otherwise the user must review the result. Repeated failures postpone the queue, not increase request frequency.

Import reads the authenticated user's submitted listing with pagination. Only native image posts/galleries are stored; binary downloads are restricted to Reddit image domains over HTTPS, with size limits, signature checks, no bearer tokens and no redirects to arbitrary hosts. The API's available listing is not represented as a guaranteed complete archive.

## Privacy, retention and security

All data stays on the user's Windows PC in `%LOCALAPPDATA%\RedditPostOrganizer` or an explicitly chosen data directory. SQLite contains drafts, imported post metadata, scheduling events, private notes and local tags. Images are copied to local storage. Private notes/tags are not sent to Reddit. There is no application server, analytics or remote database.

Access/refresh tokens are encrypted with Windows DPAPI CurrentUser in `oauth-token.bin`; passwords, codes and tokens are not written to logs or repository files. Disconnect attempts server revocation and always deletes the local token. Account information and secrets are excluded from backup exports. Restoring a backup requires reconnecting Reddit. The optional interface password does not encrypt local images or SQLite. Users can remove local data through the data folder; this never deletes posts on Reddit. Old browser-profile folders from previous versions are not read or included in backup exports.

## Review and acceptance

Run `dotnet test RedditPostOrganizer.sln`; inspect OAuth, upload, import and uncertainty handling in RedditPostOrganizer.Core. Build a self-contained Windows executable with `./build.ps1`. Before enabling production use, provide approval/client ID, test login/revocation, a permitted gallery in an owner-controlled test community, NSFW/flair/captions/order, import pagination, expired authorization and real rate-limit responses. If native gallery endpoints are not approved/available, leave publication disabled and revise the supported scope with Reddit; there is no scraping fallback.

References: [Reddit OAuth](https://github.com/reddit-archive/reddit/wiki/OAuth2), [Reddit API reference](https://www.reddit.com/dev/api/), [Responsible Builder Policy](https://support.reddithelp.com/hc/en-us/articles/42728983564564-Responsible-Builder-Policy).
