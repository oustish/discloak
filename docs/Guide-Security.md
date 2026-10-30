# Security: Password, Unlock, Recovery Key, Key Rotation

[Home](Home.md) › Security

## How it fits together

- A random **master key** encrypts the catalog and every disc.
- `bdxl.key` holds that key wrapped by your **password** (and, optionally, by a **recovery key**). It is safe to
  keep openly; it is in the catalog folder, in every catalog backup and on every encrypted disc.
- Changing the password only rewrites `bdxl.key`. Discs already burned keep the `bdxl.key` of their day, so
  **an old disc still opens with the password it was staged with**.
- The newest password plus the newest disc (or a catalog backup) always opens everything.

## Where the password comes from

In this order:

1. the agent, if the catalog is unlocked (`bdxl unlock`);
2. the environment variable `BDXL_PASSWORD`, or the variable named by `--password-env VAR`;
3. typed in (hidden), up to 3 attempts.

Scripts can use the variable. Interactive use is safer with `unlock`, because the password does not sit in the
environment.

Password rules: at least 12 characters, at least 3 of lowercase / uppercase / digits / special characters.

## Unlocking for a while

```
bdxl unlock                       # asks once; default: 30 min without use, 8 h at most
bdxl unlock --idle 1h --max 4h    # other times for this unlock
bdxl unlock --status              # what is unlocked, until when
bdxl lock                         # lock this catalog now
bdxl lock --all                   # lock every catalog; the agent ends
```

The catalog locks itself:
- after the idle time without any command using it (each command restarts the clock);
- at the maximum time;
- when Windows is locked (Win+L), when the computer goes to sleep, and when you sign out.

Defaults are settings of the catalog:

```
bdxl settings set unlock-idle 30m
bdxl settings set unlock-max 8h
bdxl settings set lock-on-win-l yes
bdxl settings set lock-on-sleep yes
```

The agent keeps keys in memory only, never on disk, and only your Windows user can talk to it. It runs from a copy of
the program in `%LOCALAPPDATA%\bdxl\agent`, so rebuilding or updating bdxl while it runs is fine. Its log
(`%LOCALAPPDATA%\bdxl\agent\agent.log`) never contains key material. To keep a single command from using the agent,
set `BDXL_AGENT=off`.

## Changing the password

```
bdxl password change
```

1. Type the current password (or it comes from `BDXL_PASSWORD` / the agent).
2. **Create the new password in your password manager and save the entry first.** Paste it at `New password:`.
3. At `Repeat it, copied from where you saved it`, copy it again **from the saved entry**, not from memory or from
   the clipboard you still have. If the entry was saved wrong (an old value, a typo, an unsaved database), the two
   differ and nothing is changed.
4. Answer `y` to "Is exactly this password (N characters) saved …?". The length helps to compare with the entry.
5. Done. Nothing but `bdxl.key` changed; the catalog stays unlocked if it was.

With `BDXL_NEW_PASSWORD` / `--new-password-env VAR` (scripts) there are no prompts: check the stored copy afterwards
with `bdxl password check` (below).

Discs burned before keep opening with the old password. That is expected for write-once media; to make sure an old
password that leaked cannot open anything *new*, rotate the key (below). Keep the old password (KeePass keeps it
in the entry's history) for as long as you have discs made before the change.

## Checking a password

Not sure which password is the right one (for the catalog, or for an older disc)? Check it; nothing is changed and
the agent is not used, so the password itself is tested:

```
bdxl password check              # the catalog's bdxl.key
bdxl password check G:\          # a disc (its bdxl.key is the one from the day it was staged)
bdxl password check E:\bk\catalog-2026-10-30    # a catalog backup
bdxl password check --recovery   # the recovery key instead
```

It answers `YES: it opens …` (exit code 0) or `NO: this password does not open …` (exit code 1), and tells when that
`bdxl.key`'s password was set, e.g. `its password was set 2026-10-29 21:14`. With that date, look for the password
manager entry, or the version of the entry in its history, that was current then.

Good habit: after every password change, run `bdxl password check` once with the password pasted from the manager.

## The recovery key

```
bdxl recovery-key create     # make one (replaces an existing one); shown once: write it down
bdxl recovery-key remove     # only the password opens the catalog from now on
```

It is 32 random bytes shown as 11 groups of 5 characters, e.g. `7F9YQ-9HNGB-…`. Case and dashes do not matter, and a
typo is detected (checksum) before anything is tried.

**Forgot the password?**

```
set BDXL_RECOVERY_KEY=7F9YQ-9HNGB-YPQSA-...          (or leave it out and type it when asked)
bdxl password change --recovery
```

Enter the new password as described in [Changing the password](#changing-the-password). The catalog opens with it again, and through the catalog every disc. A disc on its
own (without the catalog) opens with the password it was staged with, or with the recovery key if the disc was
staged after the recovery key was made.

## Rotating the master key

When you suspect the password (or the computer) was compromised:

```
bdxl password change --rotate-key
```

1. Unlocks with the current password (or `--recovery`).
2. Asks for a new password.
3. Creates a new master key. The catalog is re-encrypted with it, and everything staged from now on uses it.
4. Older keys stay in the catalog's *key ring*, so every older disc stays readable, and `stage --append` /
   `restage` of an older disc keep using that disc's own key.
5. If you had a recovery key, **a new one is shown**: the old one cannot protect the new key. Keep the new one;
   destroy the old one if a leak is why you rotated.
6. The agent is locked; `bdxl unlock` again with the new password.

What a leaked old password can still open: the discs (and their `_catalog` backups) staged *before* the rotation,
nothing after it. A burned disc can only be moved to the new key by `restage` + burning it again.

## Good practice

- Keep the recovery key on paper, away from the computer and the discs.
- Keep the catalog folder on a disk that is itself backed up, set a [catalog mirror](Guide-Mirror.md) on another drive,
  and use `stage --with-catalog` now and then.
- Ask for two copies of what matters ([Several Copies](Guide-Copies.md)) and store the two sets in different places.
- Use `bdxl lock --all` (or Win+L) when you leave the computer.
