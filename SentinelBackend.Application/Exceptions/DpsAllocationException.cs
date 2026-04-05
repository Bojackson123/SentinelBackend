// PumpBackend.Application/Exceptions/DpsAllocationException.cs
namespace SentinelBackend.Application.Exceptions;

public class DpsAllocationException : Exception
{
    public DpsAllocationException(string message) : base(message) { }
}