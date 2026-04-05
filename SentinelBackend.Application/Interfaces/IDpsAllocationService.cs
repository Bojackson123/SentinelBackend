// PumpBackend.Application/Interfaces/IDpsAllocationService.cs
namespace SentinelBackend.Application.Interfaces;

using SentinelBackend.Contracts.Dps;

public interface IDpsAllocationService
{
    Task<DpsAllocationResponse> AllocateAsync(
        string registrationId,
        IEnumerable<string> linkedHubs,
        CancellationToken cancellationToken = default
    );
}