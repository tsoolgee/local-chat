# Third-party code

## Downloader 3.1.2
Source of [bezzad/Downloader](https://github.com/bezzad/Downloader) at tag `v3.1.2` (MIT, see `Downloader/LICENSE`),
compiled into LocalChat.exe. It downloads one file over several connections.

Local change, marked `LocalChat patch`:
- `Request.cs`: response headers are kept in a case-insensitive dictionary. Without it the library misses
  `content-range` (lowercase, as sent by the Hugging Face CDN) on .NET Framework and falls back to a single connection.
