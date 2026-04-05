namespace SentinelBackend.Application.Interfaces;

public interface IDpsEnrollmentService
{
    /// <summary>
    /// Derives a per-device symmetric key from the enrollment group key.
    /// Call this at manufacturing time to generate the key to flash onto the device.
    /// </summary>
    string DeriveDeviceKey(string registrationId);
}