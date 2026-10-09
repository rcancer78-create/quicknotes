using System;
using QuickNotes.App.Data;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Composition;

/// <summary>
/// Owns the lazy production Sync/cloud graph: one scheduler, one clock, one
/// mutation coordinator identity, and one transport/engine pair until settings reset.
/// Dispose is idempotent.
/// </summary>
public sealed class SyncCloudServices : IDisposable
{
    private readonly Func<QuickNotesDbContext> _contextFactory;
    private readonly SettingsService _settings;
    private readonly IAttachmentStorageService _attachments;
    private readonly ILocalMutationCoordinator _mutationCoordinator;
    private readonly INoteHistoryService _history;
    private readonly ISyncEngine? _injectedEngine;
    private readonly ICloudUsageService? _injectedUsage;
    private readonly bool _ownsScheduler;

    private readonly object _graphLock = new();
    private ISyncEngine? _engine;
    private ICloudObjectStoreTransport? _transport;
    private ICloudUsageService? _usage;
    private ICloudCleanupService? _cleanup;
    private ICloudRetentionService? _retention;
    private ISyncPasswordRotationService? _rotation;
    private ISyncCloudCoordinator? _coordinator;
    private SyncCloudExclusiveLock? _cloudLock;
    private bool _disposed;

    public SyncCloudServices(
        Func<QuickNotesDbContext> contextFactory,
        SettingsService settings,
        IAttachmentStorageService attachments,
        ILocalMutationCoordinator mutationCoordinator,
        INoteHistoryService history,
        IS3CredentialsStorage credentials,
        ISyncPasswordStorage password,
        IDeviceIdProvider deviceId,
        ICloudTransportFactory transportFactory,
        ISyncClock clock,
        ISyncConflictService conflicts,
        ISyncScheduler scheduler,
        bool ownsScheduler,
        ISyncEngine? injectedEngine = null,
        ICloudUsageService? injectedUsage = null)
    {
        _contextFactory = contextFactory;
        _settings = settings;
        _attachments = attachments;
        _mutationCoordinator = mutationCoordinator;
        _history = history;
        Credentials = credentials;
        Password = password;
        DeviceId = deviceId;
        TransportFactory = transportFactory;
        Clock = clock;
        Conflicts = conflicts;
        Scheduler = scheduler;
        _ownsScheduler = ownsScheduler;
        _injectedEngine = injectedEngine;
        _injectedUsage = injectedUsage;
        _engine = injectedEngine;
        _usage = injectedUsage;
    }

    public IS3CredentialsStorage Credentials { get; }
    public ISyncPasswordStorage Password { get; }
    public IDeviceIdProvider DeviceId { get; }
    public ICloudTransportFactory TransportFactory { get; }
    public ISyncClock Clock { get; }
    public ISyncConflictService Conflicts { get; }
    public ISyncScheduler Scheduler { get; }
    public ILocalMutationCoordinator MutationCoordinator => _mutationCoordinator;
    public INoteHistoryService History => _history;

    public ISyncEngine? CurrentEngine
    {
        get { lock (_graphLock) return _engine; }
    }

    public ICloudObjectStoreTransport? CurrentTransport
    {
        get { lock (_graphLock) return _transport; }
    }

    public ICloudUsageService? CurrentUsage
    {
        get { lock (_graphLock) return _usage; }
    }

    public ISyncEngine GetOrCreateEngine()
    {
        ThrowIfDisposed();
        if (_injectedEngine != null)
        {
            return _injectedEngine;
        }

        lock (_graphLock)
        {
            ThrowIfDisposed();
            if (_engine != null)
            {
                return _engine;
            }

            EnsureGraphLocked();
            return _engine!;
        }
    }

    public ICloudUsageService GetOrCreateUsage()
    {
        ThrowIfDisposed();
        if (_injectedUsage != null)
        {
            return _injectedUsage;
        }

        lock (_graphLock)
        {
            ThrowIfDisposed();
            if (_usage != null)
            {
                return _usage;
            }

            EnsureGraphLocked();
            return _usage!;
        }
    }

    public ICloudCleanupService GetOrCreateCleanup()
    {
        ThrowIfDisposed();
        lock (_graphLock)
        {
            ThrowIfDisposed();
            if (_cleanup != null)
            {
                return _cleanup;
            }

            EnsureGraphLocked();
            return _cleanup!;
        }
    }

    public ICloudRetentionService GetOrCreateRetention()
    {
        ThrowIfDisposed();
        lock (_graphLock)
        {
            ThrowIfDisposed();
            if (_retention != null)
            {
                return _retention;
            }

            EnsureGraphLocked();
            return _retention!;
        }
    }

    public ISyncPasswordRotationService GetOrCreateRotation()
    {
        ThrowIfDisposed();
        lock (_graphLock)
        {
            ThrowIfDisposed();
            if (_rotation != null)
            {
                return _rotation;
            }

            EnsureGraphLocked();
            return _rotation!;
        }
    }

    public ISyncCloudCoordinator GetOrCreateCoordinator()
    {
        ThrowIfDisposed();
        lock (_graphLock)
        {
            ThrowIfDisposed();
            if (_coordinator != null)
            {
                return _coordinator;
            }

            EnsureGraphLocked();
            return _coordinator!;
        }
    }

    public void ResetAfterSettingsChange()
    {
        lock (_graphLock)
        {
            TearDownGraphLocked();
        }
    }

    internal int DisposeCount { get; private set; }

    public void Dispose()
    {
        lock (_graphLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeCount++;
        }

        try
        {
            if (_ownsScheduler)
            {
                Scheduler.Dispose();
            }
        }
        catch
        {
        }

        lock (_graphLock)
        {
            TearDownGraphLocked();
        }
    }

    private void EnsureGraphLocked()
    {
        var settings = _settings.CurrentSettings.CloudSync ?? new SyncCloudSettings();
        var crypto = new SyncCryptoService();
        var exporter = new SyncPackageExporter(crypto, DeviceId);
        var importer = new SyncPackageImporter(crypto);
        var credsProvider = Credentials as IS3CredentialsProvider ?? new StorageCredentialsProvider(Credentials);
        var transport = _transport ?? TransportFactory.Create(settings, credsProvider);
        _transport = transport;
        var keyHelper = new SyncObjectKeyHelper(settings);
        _cloudLock ??= new SyncCloudExclusiveLock();
        _usage ??= new CloudUsageService(transport, settings, Credentials);
        _cleanup ??= new CloudCleanupService(transport, crypto, keyHelper, _usage, _cloudLock);
        _retention ??= new CloudRetentionService(transport, keyHelper);
        _rotation ??= new SyncPasswordRotationService(
            transport,
            crypto,
            keyHelper,
            Password,
            settings,
            _usage,
            _cloudLock);
        _coordinator ??= new SyncCloudCoordinator(
            transport,
            exporter,
            importer,
            DeviceId,
            settings,
            Credentials,
            keyHelper);
        if (_engine == null)
        {
            var blobService = new SyncAttachmentBlobService(
                transport,
                crypto,
                _attachments,
                keyHelper,
                settings,
                _usage);
            _engine = new SyncEngine(
                transport,
                exporter,
                importer,
                DeviceId,
                settings,
                Credentials,
                keyHelper: keyHelper,
                dbFactory: _contextFactory,
                blobService: blobService,
                exclusiveLock: _cloudLock,
                passwordStorage: Password,
                mutationCoordinator: _mutationCoordinator);
        }
    }

    private void TearDownGraphLocked()
    {
        if (_engine is IDisposable disposableEngine && !ReferenceEquals(_engine, _injectedEngine))
        {
            disposableEngine.Dispose();
        }
        else if (_transport != null && (_injectedEngine == null))
        {
            _transport.Dispose();
        }

        if (_usage is IDisposable disposableUsage && !ReferenceEquals(_usage, _injectedUsage))
        {
            disposableUsage.Dispose();
        }

        _transport = null;
        if (_injectedEngine == null)
        {
            _engine = null;
        }

        if (_injectedUsage == null)
        {
            _usage = null;
        }

        _cleanup = null;
        _retention = null;
        _rotation = null;
        _coordinator = null;
        _cloudLock = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SyncCloudServices));
        }
    }
}
