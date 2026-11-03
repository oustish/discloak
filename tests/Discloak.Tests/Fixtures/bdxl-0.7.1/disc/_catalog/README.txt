BDXL catalog backup, created 2026-11-03 23:29 UTC.

files.jsonl.enc, discs.jsonl.enc, blanks.jsonl.enc, settings.json.enc   the catalog, ENCRYPTED with the catalog's
    master key (AES-256-GCM, bdxl's BDXLENC2 format)
catalog.hash.json          SHA-256 of each file above and an HMAC over that list
bdxl.key                   the master key, wrapped by the password (Argon2id) and, if one was made, by the recovery key
*.repair.json, *.par       repair data: per-block SHA-256 + Reed-Solomon parity blocks (GF(256), Cauchy matrix),
    computed over the stored bytes: checking and repairing need no password

Check:    bdxl catalog backup verify <this folder>
Repair:   bdxl catalog backup verify <this folder> --repair
Restore:  bdxl catalog backup restore <this folder> --catalog <catalog folder> [--force] [--recovery]
