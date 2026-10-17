using CupboardTracker.Domain;

namespace CupboardTracker.Application.Scans;

public interface IDeviceRepository
{
    Task<Device?> GetAsync(string deviceId, CancellationToken cancellationToken);

    Task UpsertAsync(Device device, CancellationToken cancellationToken);
}
