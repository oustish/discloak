namespace Bdxl;

/// <summary>
/// A burned disc that gets one more session ('stage --append', 'plan --append'). The room for the session is the
/// disc's size minus what earlier sessions took (OccupiedBytes); the disc's reserve is kept free again, because
/// every session needs its own manifest, disc.json, catalog backup and the medium's session overhead.
/// --mount: the disc is in the drive - its disc.json must name it, and the drive's free space, if it reports any,
/// caps the room.
/// </summary>
public sealed class AppendTarget
{
    public required DiscRecord Disc { get; init; }
    public required long Room { get; init; }       // bytes the session's folder may take
    public string? Mount { get; init; }
    public long Reserve => Disc.Reserve ?? 0;
    public long Free => Math.Max(0, Room - Reserve);   // bytes for files
    public int Session => Disc.Sessions + 1;

    /// <summary>A bin of exactly the free space, for the packer.</summary>
    public BlankDisc AsBlank() => new() { Id = Disc.Label, Capacity = Free };

    /// <summary>Space the disc still has for files, from the catalog alone (null = size unknown).</summary>
    public static long? FreeFor(DiscRecord d) =>
        d.Capacity is long capacity ? Math.Max(0, capacity - Occupied(d) - (d.Reserve ?? 0)) : null;

    /// <summary>Space the disc's sessions take; without OccupiedBytes (imported discs) the first session is assumed to have used its whole reserve.</summary>
    public static long Occupied(DiscRecord d) => d.OccupiedBytes > 0 ? d.OccupiedBytes : d.UsedBytes + (d.Reserve ?? 0);

    public static AppendTarget Open(Ctx c, string label)
    {
        var disc = c.Arc.FindDisc(label);
        if (disc.State == "staged")
            throw new UserException($"{disc.Label} is not burned yet. To put more on it: 'discard {disc.Label}' and stage again.");
        if (disc.State != "burned") throw new UserException($"{disc.Label} is {disc.State}.");
        if (disc.PendingAppend is { } pending)
            throw new UserException($"Session {pending.Number} of {disc.Label} is staged but not burned yet. Burn it and run " +
                                    $"'burned {disc.Label}', or take it back with 'discard {disc.Label}', first.");

        long? room = disc.Capacity is long capacity ? capacity - Occupied(disc) : null;
        string? mount = c.Opt.Get("mount") is { } mountText ? Path.GetFullPath(mountText) : null;
        if (mount != null)
        {
            DiscInfo? info;
            try { info = JsonStore.ReadObject<DiscInfo>(Path.Combine(mount, DiscFiles.InfoName)); }
            catch (JsonException) { info = null; }
            if (info == null || !info.Label.Equals(disc.Label, StringComparison.OrdinalIgnoreCase))
                throw new UserException($"The disc in {mount} is " + (info == null ? "not a bdxl disc (no readable disc.json)" : info.Label) +
                                        $", not {disc.Label}.");
            if (info.KeyId != null && !info.KeyId.Equals(c.Key.KeyId, StringComparison.OrdinalIgnoreCase))
                throw new UserException($"The disc in {mount} is encrypted with another key (key id {info.KeyId}, this catalog's is {c.Key.KeyId}). " +
                                        DiscFiles.OtherCatalogHint(mount));

            long? driveFree = null;
            try { driveFree = DiscFiles.FindDrive(mount)?.AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (driveFree is long free && free >= (1L << 20))
                room = room is long r ? Math.Min(r, free) : free;
            else
                Console.WriteLine("Note: the drive reports no free space. Many burning programs leave an open disc read-only for " +
                                  "Windows, so the catalog's figure is used; if the disc was finalized, nothing more can be burned onto it.");
        }
        if (room == null)
            throw new UserException($"The size of {disc.Label} is not known. Put it in the drive and add --mount <path> to use the free space the drive reports.");
        return new AppendTarget { Disc = disc, Room = Math.Max(0, room.Value), Mount = mount };
    }
}
