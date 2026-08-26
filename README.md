# ArgonFetch Plugins

Plugins for [ArgonFetch](https://github.com/ArgonFetch/ArgonFetch), and the repository index they
are served from.

yt-dlp fetches things the ordinary way, and most links need nothing more. A plugin exists for the
ones that do - a link that has to become a different link before anything can be downloaded, a
link that lists a collection, or a link some other piece of code fetches on its own.

## Installing these

Point ArgonFetch at this repository's index and name what you want:

```jsonc
"Plugins": {
  "Repositories": [ "https://raw.githubusercontent.com/ArgonFetch/ArgonFetchPlugins/repo/index.json" ],
  "Install": [ "spotify" ]     // or "spotify@1.0.0" to pin it
}
```

The list is desired state: what is named is installed, what is not is removed. Its order is also
precedence - where two plugins claim the same link, the one listed first wins.

## What is here

| Plugin | Does |
|---|---|
| `spotify` | Describes a track from Spotify and fetches the same recording from where it actually is. Lists albums and playlists. |

## Writing your own

Start from [ArgonFetchPluginTemplate](https://github.com/ArgonFetch/ArgonFetchPluginTemplate).

## How this is published

`main` holds the source. CI builds each plugin, zips it, hashes it and pushes the archives plus an
`index.json` to an orphan `repo` branch, which is served raw from GitHub. Nothing needs hosting,
and forking this repository gives you your own repository of plugins for free.

MIT licensed.
