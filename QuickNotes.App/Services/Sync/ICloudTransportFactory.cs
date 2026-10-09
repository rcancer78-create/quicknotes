using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Typed factory for cloud object-store transports. Avoids a service locator
/// while keeping S3 construction out of view-models.
/// </summary>
public interface ICloudTransportFactory
{
    ICloudObjectStoreTransport Create(SyncCloudSettings settings, IS3CredentialsProvider credentials);
}

public sealed class S3CloudTransportFactory : ICloudTransportFactory
{
    public ICloudObjectStoreTransport Create(SyncCloudSettings settings, IS3CredentialsProvider credentials)
        => new S3ObjectStoreTransport(settings, credentials);
}

public sealed class UnavailableCloudTransportFactory : ICloudTransportFactory
{
    public ICloudObjectStoreTransport Create(SyncCloudSettings settings, IS3CredentialsProvider credentials)
        => new UnavailableCloudObjectStoreTransport();
}
