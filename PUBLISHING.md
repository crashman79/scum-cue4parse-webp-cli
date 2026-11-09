# Publishing this repository

This folder is a sanitized public export of the CUE4Parse WebP CLI. It contains
the extractor code and the general documentation. It does not contain the private
SCUM DB application, game files, extracted assets or AES keys.

## Before you push

Run these checks from the repository root:

```sh
# No long hex strings outside the placeholder docs
grep -rInE '0x[0-9a-fA-F]{16,}' . | grep -v 'wiki_docs/AES-Encryption-Keys.md'

# No obvious credentials
grep -rInE 'password|passwd|secret|api[_-]?key|token' \
  --include='*.cs' --include='*.md' --include='*.json' --include='*.sh' .

# No internal hostnames or local paths
grep -rInE 'sdbhetz|sdbext|scumdb|/home/crashman79|/mnt/scumdb' .
```

The only expected matches are the placeholder examples in
`wiki_docs/AES-Encryption-Keys.md` and the `SCUM_AES_KEY` environment variable
name. If a real key or credential appears, remove it before continuing.

## Create the public repository

Create an empty repository on GitHub or Gitea. Do not import the private SCUM DB
repository.

```sh
cd cue4parse-public
git init -b main
git add .
git commit -m "Initial public release of the CUE4Parse WebP CLI"
git remote add origin <your-public-repo-url>
git push -u origin main
```

Suggested repository name: `cue4parse-webp-cli`.

## What must not be included

- `bin/`, `obj/` and other build output.
- Game `.pak` files, extracted JSON, textures or meshes.
- AES keys, `.env` files, API tokens or passwords.
- The private SCUM DB repository, its database or its deployment scripts.
- Customer or user data.

## License

The project is MIT licensed. See `LICENSE`. CUE4Parse is a separate project with
its own license and maintainers; users clone it separately as a sibling
directory.
