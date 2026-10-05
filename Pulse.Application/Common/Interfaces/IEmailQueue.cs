namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// Fire-and-forget email dispatch. Implementations enqueue the send as a background job
/// so SMTP latency is removed from the calling request path.
/// </summary>
public interface IEmailQueue
{
    void Enqueue(string to, string subject, string htmlBody);
}
