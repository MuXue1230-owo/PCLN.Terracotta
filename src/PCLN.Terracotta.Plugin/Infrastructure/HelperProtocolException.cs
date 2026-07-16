namespace Cn.Pcln.Terracotta.Infrastructure;

public sealed class HelperProtocolException : Exception
{
    public HelperProtocolException(string message)
        : base(message)
    {
    }

    public HelperProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
