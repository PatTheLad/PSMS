using PSMS.Core.Models;
using PSMS.Core.Models.Admin;

namespace PSMS.Core.Abstractions;

/// <summary>Extended Events–based live profiler (cross-platform SQL Server).</summary>
public interface IExtendedEventsService
{
    IReadOnlyList<XEventTemplate> GetTemplates();

    Task<IReadOnlyList<XEventSessionInfo>> GetSessionsAsync(
        ConnectionDefinition connection, string? password, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> StartSessionAsync(
        ConnectionDefinition connection, string? password, StartProfilerSessionRequest request, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> StopSessionAsync(
        ConnectionDefinition connection, string? password, string sessionName, CancellationToken cancellationToken = default);

    Task<AdminOperationResult> DropSessionAsync(
        ConnectionDefinition connection, string? password, string sessionName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProfilerEvent>> ReadEventsAsync(
        ConnectionDefinition connection, string? password, string sessionName, CancellationToken cancellationToken = default);

    Task<string> ScriptSessionAsync(
        ConnectionDefinition connection, string? password, StartProfilerSessionRequest request, CancellationToken cancellationToken = default);
}
