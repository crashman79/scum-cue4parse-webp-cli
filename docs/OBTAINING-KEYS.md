# Obtaining and supplying the AES key

SCUM's PAK files are encrypted. CUE4Parse needs the AES key for the exact game
build you are extracting. This repository does **not** include any keys, and it
never should.

## The short version

Set `SCUM_AES_KEY` before running the extractor:

```sh
export SCUM_AES_KEY="0xYOUR_KEY_HERE"
```

Windows PowerShell:

```powershell
$env:SCUM_AES_KEY = "0xYOUR_KEY_HERE"
```

Windows Command Prompt:

```bat
set SCUM_AES_KEY=0xYOUR_KEY_HERE
```

The key is read from the environment. If it is missing, the extractor stops with
a clear error instead of using a baked-in value.

## Where the key comes from

The key is tied to a game build. A key that worked for an older build will not
work after a game update. Get the key for the build you are extracting from one
of these places:

- Your own game installation and the tooling you use to inspect it.
- A community key list for that exact build, if one is available.
- Your own extraction notes from a previous build.

This repository does not ship a key list and does not tell you where to download
one. That is deliberate. Use a key you are entitled to use, and keep it out of
the repository.

## Check the build

Before extracting, confirm the build number of the game files. If the key and
the build do not match, CUE4Parse will fail to decrypt the PAK index or produce
garbage. Re-check the key source and the game version.

## Keep the key out of Git

Do not commit:

- `SCUM_AES_KEY` in a script, config file or `.env` file.
- A key in a shell history file that is checked in.
- A key in a log, screenshot or issue report.

If a key is accidentally committed, treat it as public. Rotate or replace it in
your local setup, and remove it from the repository history before publishing.

## Output directory

`SCUM_OUTPUT_DIR` controls where extracted files go. It is optional; the default
is `Output/Exports` next to the executable.

```sh
export SCUM_OUTPUT_DIR="$PWD/out"
```

## More background

The CUE4Parse wiki has a general explanation of AES keys and the `SubmitKey` API:

- https://github.com/FabianFG/CUE4Parse
- `wiki_docs/AES-Encryption-Keys.md` in this repository

Those docs use placeholders. They do not contain a working key.
