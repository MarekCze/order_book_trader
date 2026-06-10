namespace OrderFlow.Infrastructure.Dbn;

public sealed class DbnFormatException : Exception
{
    public DbnFormatException(string message) : base(message)
    {
    }
}
