BDXL disc (encrypted) - how to read it
=======================================

disc.json             label, date, sessions, format ("bdxl-pack-1"), packSize and chunkSize; catalogId + keyId
                      name the key in bdxl.key; createdBy = the bdxl build that wrote it (plain JSON).
                      Nothing about the files.
bdxl.key              the master key, wrapped (plain JSON, safe to keep openly)
packs.sha256          SHA-256 of every pack: check the disc with any sha256sum tool, no password needed
data/NNN/NNNNNN.pack  the data: every file, laid end to end in one stream, cut into packs of equal size
                      (64 MiB of data each), each pack encrypted on its own
manifest.enc          which file lies where in that stream: one JSON object per line, encrypted
manifest.sN.enc       (a disc with several sessions) the files session N added - read every manifest
_catalog/             (optional) a backup of the whole catalog, see its own README.txt

With bdxl:  bdxl init --key-from <this disc> --catalog <new folder>   then   bdxl scandisc <this disc>
            and  bdxl restore <folder> "*" --mount <this disc>      (or: catalog backup restore <disc>/_catalog)

Without bdxl, all of this is standard cryptography:

1. The master keys (32 bytes each) from bdxl.key, envelope "kind": "password":
     wrapping key = Argon2id (RFC 9106, version 0x13) of the password (UTF-8), with "salt", parallelism 1,
                    "memoryKiB" KiB of memory, "passes" iterations, 32 bytes of output
     AES-256-GCM decryption of "wrapped" (ciphertext + the last 16 bytes: tag) with that key, nonce "nonce",
     associated data the ASCII text:
       "ring": true    bdxl-key-2|<catalogId>|<the envelope's keyId>|password   and the plaintext is a key
                       ring: for every key, oldest first, its 8-byte key id + the 32-byte key; the last one
                       is the newest (a catalog gets a new key when its key is rotated; keys are never dropped)
       otherwise       bdxl-key-1|<catalogId>|<the file's keyId>|password   and the plaintext is one key
   Every pack and .enc file names its key by the key id in its header (below), so take that key from the ring.
   Envelope "kind": "recovery" (if present): wrapping key = HKDF-SHA256 (RFC 5869) of the 32-byte recovery key,
   salt "salt", info "bdxl recovery key", 32 bytes; the rest as above with ...|recovery. The recovery key is
   written as Crockford base32 (34 bytes: the key + the first 2 bytes of its SHA-256), dashes ignored.
   (byte arrays in bdxl.key are base64)

2. A pack (data/NNN/NNNNNN.pack, pack number n = the file name, from 1):
     "BDXLPAK1" (8 bytes) | key id (8) | salt (32) | nonce prefix (8) | 64 chunks of: ciphertext (1 MiB) | tag (16)
   pack key   = HKDF-SHA256 (master key, salt = the pack's salt, info = "bdxl-enc2 pack"), 32 bytes
   chunk c (from 0): AES-256-GCM with nonce = prefix (8) + c (uint32 LE), associated data = n (uint32 LE) + c (uint32 LE)
   The data stream is the plaintext of pack 1, then pack 2, ...: byte X of it is in pack X / 64 MiB + 1.

3. manifest*.enc (format BDXLENC2):
     "BDXLENC2" (8 bytes) | key id (8) | salt (32) | nonce prefix (8) | chunks...
     chunk: length L (uint32, little endian) | ciphertext (L bytes) | tag (16)
   key        = HKDF-SHA256 (master key, salt, info = "bdxl-enc2 manifest"); "bdxl-enc2 catalog" for _catalog/*.enc
   chunk n (from 0): AES-256-GCM with nonce = prefix (8) + n (uint32 LE), associated data = n (uint32 LE) +
                1 byte: 1 for the last chunk of the file, else 0
   plaintext of a chunk = 1 mode byte + data; mode 0 = data as it is, 1 = zstd-compressed (RFC 8878)
   The plaintext is JSON lines, one per stored file, then empty lines (padding):
     "source"      the file's real path (alias/relative path)
     "at", "size"  where its stored bytes are in the data stream, and how many
     "plainSize", "sha256"   size and SHA-256 of the original content
     "compression", "codec"  if present: the stored bytes are compressed at that level with that codec;
                 "zstd" (or no codec: discs staged before bdxl 0.6.0) = zstd frames (RFC 8878; 'zstd -d' reads them)
     "modified"    the file's time when it was staged
   A file split across discs: "part"/"parts"/"offset" (where the part belongs), "fileSize"/"fileSha256" (the
   whole file); join the parts in order.