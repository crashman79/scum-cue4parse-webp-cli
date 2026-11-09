# Security

## No keys in the repository

This project does not contain AES keys, game files, extracted assets or
credentials. The SCUM AES key is read from the `SCUM_AES_KEY` environment
variable at runtime.

Do not open an issue, pull request or discussion with a key in it. Treat any
committed key as public immediately.

## Key handling

- Keep keys in your shell environment, a local password manager or a local
  `.env` file that is gitignored.
- Do not put keys in build scripts that are committed.
- Do not paste keys into logs, screenshots or bug reports.
- Use a key that matches the exact game build you are extracting.

## Reporting a problem

If you find a security issue in this code, open a private report through the
hosting platform or contact the maintainer directly. Do not include keys, game
files or personal data in the report.

## Scope

This is a local extraction tool. It does not run a service, accept remote input
or store user accounts. The main risks are local file handling and accidental
secret disclosure.
