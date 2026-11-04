# Security

Discloak protects backups with encryption, so a weakness in it matters even when nobody has noticed it yet.

## Reporting a vulnerability

Please report it **privately**, not as a public issue or pull request:

- On GitHub: the repository's **Security** tab › **Report a vulnerability** (private vulnerability reporting).

Please include what is affected (the version - `discloak version` - and the command or format), how to reproduce it,
and what an attacker would gain. You will get an answer as soon as possible; this is a project maintained in spare
time, so please allow a few days. Once a fix is released, the report can be made public, with credit if you like.

## What is in scope

- The encryption and its use: the key file and its envelopes (Argon2id, recovery key), the catalog's files and the
  disc's manifests and packs (AES-256-GCM, HKDF), the integrity check (HMAC). The design is in
  [discloak/ENCRYPTION.md](discloak/ENCRYPTION.md) and [docs/Files-and-Formats.md](docs/Files-and-Formats.md).
- Anything a disc or a catalog backup reveals without the password beyond what the documentation says it shows (its
  label, dates, number of sessions and amount of data).
- The `unlock` agent (it holds the master key in memory) and how commands talk to it.
- Data loss caused by Discloak itself: a disc or catalog it writes that it cannot read back.

Out of scope: a weak password chosen by the user, a machine that is already compromised, and the burning program
or file system.

## Supported versions

Fixes go into the newest release. Discs and catalogs written by older versions stay readable by every newer one, so
updating is always the fix.
