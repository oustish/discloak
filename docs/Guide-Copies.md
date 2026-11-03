# Several Copies

[Home](Home.md) › Several Copies

By default every file is stored once. A disc that becomes unreadable then loses the files that exist nowhere else.
For data that matters, ask for more copies: discloak keeps planning them until each file is on that many **different**
discs. Two copies of one file never go on the same disc.

## Steps

1. Choose the number per location (or for all of them):
   ```
   discloak copies set Photos 2          # the location (alias) 'Photos' wants 2 copies
   discloak copies set --default 1       # every other location: 1 (the default)
   discloak copies list                  # the numbers and, per location, how many files have fewer burned copies
   ```
2. Plan and stage as usual. `plan` counts the extra copies ("incl. N further copies of files already on a disc");
   each copy lands on a different disc:
   ```
   discloak plan
   discloak stage E:\Staging --max-discs 2
   ```
3. Burn, verify and record each disc (`burned`). A copy counts as made only when its disc is burned:
   `copies list` and `status` show what is still missing.
4. Keep the copies apart: store the discs of one copy in one place and those of the other copy somewhere else.

## How it behaves

- **Which number applies:** a location's own number, else the default. A file whose content also exists in another
  location (identical files are stored once) gets the higher number of the two.
- **Planning counts staged discs too**, so the next `stage` does not plan a copy that is already waiting to be
  burned. `status`, `copies list` and `find` count **burned** discs only.
- **Never two copies on one disc**, also not when appending a session (`stage --append`): a disc that already holds
  a file does not get another copy of it.
- **Split files:** every part is stored as many times as wanted, each time on a different disc.
- **Lost or destroyed discs** stop counting: `lost` / `destroyed` say how many files now have fewer copies than
  wanted, and the next `plan` / `stage` makes them again (as long as the files are still on their source).
- **Restoring** takes each file from whichever of its discs is mounted; you need only one copy. If a file comes back
  damaged from one mounted disc, `restore` takes it from another mounted disc that has a copy.
- **Lowering the number** removes nothing from discs; the extra copies simply stay.
- The number of copies goes with the location when it is renamed (`alias rename`).

## Cost

Each copy needs its own discs: 2 copies of 6 TB is about 126 BDXL 100 GB discs instead of 63. Asking for 2 copies
only for the irreplaceable locations (photos, documents) and 1 for the rest (media you could get again) is often
the right balance.
