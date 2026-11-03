namespace Discloak;

/// <summary>A blank disc the user owns and registered with 'blanks add'.</summary>
public class BlankDisc
{
    public string Id { get; set; } = "";
    public long Capacity { get; set; }             // bytes
    public long Reserve { get; set; }              // bytes deliberately kept free (filesystem overhead, catalog backup, ...)
    public decimal? Cost { get; set; }             // price of this blank disc
    public DateTime AddedUtc { get; set; }

    [JsonIgnore] public long Usable => Capacity - Reserve;
}
