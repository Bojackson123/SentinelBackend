// PumpBackend.Infrastructure/Dps/DpsEnrollmentService.cs
namespace SentinelBackend.Infrastructure.Dps;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SentinelBackend.Application.Dps;
using SentinelBackend.Application.Interfaces;

public class DpsEnrollmentService : IDpsEnrollmentService
{
    private readonly DpsOptions _options;

    public DpsEnrollmentService(IOptions<DpsOptions> options)
    {
        _options = options.Value;
    }

    public string DeriveDeviceKey(string registrationId)
    {
        var groupKeyBytes = Convert.FromBase64String(_options.EnrollmentGroupPrimaryKey);
        using var hmac = new HMACSHA256(groupKeyBytes);
        var derivedKey = hmac.ComputeHash(Encoding.UTF8.GetBytes(registrationId));
        return Convert.ToBase64String(derivedKey);
    }
}