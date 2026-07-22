namespace Chilink;

public sealed class ChilinkException : Exception
{
    public ChilinkException(string message)
        : base(message)
    {
    }

    public ChilinkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
