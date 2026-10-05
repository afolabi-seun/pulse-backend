namespace Pulse.Application.Common.Interfaces;

public interface IThresholdRepository
{
    Task<Dictionary<string, string>> LoadAllAsync(CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
}
