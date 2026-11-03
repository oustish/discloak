namespace Discloak;

public sealed class UserException : Exception
{
    public UserException(string message) : base(message) { }
}
