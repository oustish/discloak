# bdxl

bdxl plans, stages and keeps track of backups on BDXL discs (100/128 GB Blu-ray, but any optical or other
write-once medium works). It indexes your folders, works out which files still need a copy, fills discs as
full as possible, writes ready-to-burn disc folders, and remembers which file is on which disc, so you can find
and restore anything years later.

Everything is encrypted. One password protects the catalog and every disc. The password can be changed, there is
an optional recovery key, and the master key can be rotated. Each disc can also be read on its own, without the
catalog, with nothing but the password.

## Pages

**Start here**
- [Getting Started](Getting-Started.md): build, create the catalog, back up the first disc
- [Concepts](Concepts.md): the catalog, locations, pending files, discs, keys and the agent

**How-to guides** (step by step)
- [Backing Up](Guide-Backing-Up.md): the regular backup cycle, scan → plan → stage → burn → verify → burned
- [Restoring Files](Guide-Restoring.md): single files, whole folders, split files, one drive or several
- [Security: Password, Unlock, Recovery Key, Key Rotation](Guide-Security.md)
- [Appending to a Disc (multisession)](Guide-Appending.md): fill the free space of a burned disc later
- [Large Files (split across discs)](Guide-Large-Files.md)
- [Compression](Guide-Compression.md)
- [Several Copies](Guide-Copies.md): two or more copies of important locations, on different discs
- [Maintenance](Guide-Maintenance.md): rescans, moved files, ignore rules, aliases, purge, settings, costs
- [Discs You Already Have](Guide-Existing-Discs.md): bring discs burned without bdxl (or by another catalog) in
- [Disaster Recovery](Guide-Disaster-Recovery.md): lost or damaged catalog, lost or damaged disc, forgotten password

**Reference**
- [Command Reference](Command-Reference.md): every command and option
- [Files and Formats](Files-and-Formats.md): what is in the catalog folder and on a disc
- [Troubleshooting and FAQ](Troubleshooting.md): exit codes, common messages, questions

Design background: [`bdxl/ENCRYPTION.md`](../bdxl/ENCRYPTION.md). Developer notes: [`bdxl/HANDOFF.md`](../bdxl/HANDOFF.md).

## In one minute

```
bdxl init --recovery-key                            # once: a new encrypted catalog in .\catalog
bdxl blanks add 10 --size 96882130944 --reserve 300MB --cost 45
bdxl scan D:\Photos --name Photos                   # index (hash) a folder
bdxl plan                                           # what would go where (changes nothing)
bdxl stage E:\Staging --with-catalog                # write the next disc folder: E:\Staging\BDXL-0001
   ... burn E:\Staging\BDXL-0001 with your burning program (UDF 2.50) ...
bdxl verify BDXL-0001 G:\                           # read the burned disc back and compare
bdxl burned BDXL-0001                               # record it as burned
bdxl restore D:\Recovered "holiday" --mount G:\     # later: get files back
```
