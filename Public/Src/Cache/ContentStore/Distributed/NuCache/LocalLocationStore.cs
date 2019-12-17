// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.NuCache.EventStreaming;
using BuildXL.Cache.ContentStore.Distributed.Redis;
using BuildXL.Cache.ContentStore.Distributed.Stores;
using BuildXL.Cache.ContentStore.Distributed.Tracing;
using BuildXL.Cache.ContentStore.Distributed.Utilities;
using BuildXL.Cache.ContentStore.Extensions;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Native.IO;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Tracing;
using DateTimeUtilities = BuildXL.Cache.ContentStore.Utils.DateTimeUtilities;
using static BuildXL.Cache.ContentStore.Utils.DateTimeUtilities;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities;
using BuildXL.Cache.ContentStore.Interfaces.Synchronization.Internal;
using System.Runtime.CompilerServices;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// Top level class that represents a local location store.
    /// </summary>
    /// <remarks>
    /// Local location store is a mediator between a content location database and a central store.
    /// </remarks>
    public sealed class LocalLocationStore : StartupShutdownBase
    {
        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(LocalLocationStore));

        /// <nodoc />
        public CounterCollection<ContentLocationStoreCounters> Counters { get; } = new CounterCollection<ContentLocationStoreCounters>();

        /// <nodoc />
        public Role? CurrentRole { get; private set; }

        /// <nodoc />
        public ContentLocationDatabase Database { get; }

        /// <nodoc />
        public IGlobalLocationStore GlobalStore { get; }

        /// <nodoc />
        public MachineReputationTracker MachineReputationTracker { get; private set; }

        /// <summary>
        /// Testing purposes only. Used to override machine id given by global store.
        /// </summary>
        internal MachineId? OverrideMachineId { get; set; }

        /// <nodoc />
        public MachineId LocalMachineId => OverrideMachineId ?? GlobalStore.LocalMachineId;

        /// <nodoc />
        public ContentLocationEventStore EventStore { get; private set; }

        private ILocalContentStore _localContentStore;

        internal ClusterState ClusterState { get; } = new ClusterState();
        private readonly IClock _clock;

        private readonly LocalLocationStoreConfiguration _configuration;
        private CheckpointManager _checkpointManager;

        /// <nodoc />
        public CentralStorage CentralStorage { get; private set; }

        private CentralStorage _innerCentralStorage;

        // The (optional) distributed central storage which wraps the inner central storage
        internal DistributedCentralStorage DistributedCentralStorage { get; private set; }

        // Fields that are initialized in StartupCoreAsync method.
        private Timer _heartbeatTimer;

        // Local volatile caches for preventing resending the same events over and over again.
        private readonly VolatileSet<ContentHash> _recentlyAddedHashes;
        private readonly VolatileSet<ContentHash> _recentlyTouchedHashes;

        // Normally content registered with the machine already will be skipped or re-registered lazily. If content was recently removed (evicted),
        // we need to eagerly update the content because other machines may have already received updated db with content unregistered for the this machine.
        // This tracks recent removals so the corresponding content can be registered eagerly with global store.
        private readonly VolatileSet<ContentHash> _recentlyRemovedHashes;

        private DateTime _lastCheckpointTime;
        private Task<BoolResult> _pendingProcessCheckpointTask;

        private DateTime _lastRestoreTime;
        private string _lastCheckpointId;
        private bool _reconciled;

        private readonly SemaphoreSlim _databaseInvalidationGate = new SemaphoreSlim(1);

        private readonly SemaphoreSlim _heartbeatGate = new SemaphoreSlim(1);

        /// <summary>
        /// Initialization for local location store may take too long if we restore the first checkpoint in there.
        /// So the initialization process is split into two pieces: core initialization and post-initialization that should be checked in every public method.
        /// </summary>
        private Task<BoolResult> _postInitializationTask;

        private readonly Interfaces.FileSystem.AbsolutePath _reconcileFilePath;

        private Func<OperationContext, ContentHash, Task<ResultBase>> _proactiveReplicationTaskFactory;
        private CancellationTokenSource _lastProactiveReplicationTokenSource;
        private readonly object _proactiveReplicationLockObject = new object();

        /// <nodoc />
        public LocalLocationStore(
            IClock clock,
            IGlobalLocationStore globalStore,
            LocalLocationStoreConfiguration configuration)
        {
            Contract.RequiresNotNull(clock);
            Contract.RequiresNotNull(globalStore);
            Contract.RequiresNotNull(configuration);

            _clock = clock;
            _configuration = configuration;
            GlobalStore = globalStore;

            _recentlyAddedHashes = new VolatileSet<ContentHash>(clock);
            _recentlyTouchedHashes = new VolatileSet<ContentHash>(clock);
            _recentlyRemovedHashes = new VolatileSet<ContentHash>(clock);

            ValidateConfiguration(configuration);

            _reconcileFilePath = configuration.Checkpoint.WorkingDirectory / "reconcileMarker.txt";

            _innerCentralStorage = CreateCentralStorage(configuration.CentralStore);

            configuration.Database.TouchFrequency = configuration.TouchFrequency;
            Database = ContentLocationDatabase.Create(clock, configuration.Database, () => ClusterState.InactiveMachines);
        }

        /// <summary>
        /// Checks whether the reconciliation for the store is up to date
        /// </summary>
        public bool IsReconcileUpToDate()
        {
            if (!File.Exists(_reconcileFilePath.Path))
            {
                return false;
            }

            var contents = File.ReadAllText(_reconcileFilePath.Path);
            var parts = contents.Split('|');
            if (parts.Length != 2 || parts[0] != _configuration.GetCheckpointPrefix())
            {
                return false;
            }

            var reconcileTime = DateTimeUtilities.FromReadableTimestamp(parts[1]);
            if (reconcileTime == null)
            {
                return false;
            }

            return reconcileTime.Value.IsRecent(_clock.UtcNow, _configuration.LocationEntryExpiry.Multiply(0.75));
        }

        /// <summary>
        /// Marks reconciliation for the store is up to date with the current time stamp
        /// </summary>
        public void MarkReconciled(bool reconciled = true)
        {
            if (reconciled)
            {
                File.WriteAllText(_reconcileFilePath.Path, $"{_configuration.GetCheckpointPrefix()}|{_clock.UtcNow.ToReadableString()}");
            }
            else
            {
                FileUtilities.DeleteFile(_reconcileFilePath.Path);
            }
        }

        /// <summary>
        /// Gets a value indicating whether the store supports storing and retrieving blobs.
        /// </summary>
        public bool AreBlobsSupported => GlobalStore.AreBlobsSupported;

        private ContentLocationEventStore CreateEventStore(LocalLocationStoreConfiguration configuration, string subfolder)
        {
            return ContentLocationEventStore.Create(
                configuration.EventStore,
                new ContentLocationDatabaseAdapter(Database, ClusterState),
                GlobalStore.LocalMachineLocation.ToString(),
                CentralStorage,
                configuration.Checkpoint.WorkingDirectory / "reconciles" / subfolder
                );
        }

        private CentralStorage CreateCentralStorage(CentralStoreConfiguration configuration)
        {
            // TODO: Validate configuration before construction (bug 1365340)
            Contract.Requires(configuration != null);

            switch (configuration)
            {
                case LocalDiskCentralStoreConfiguration localDiskConfig:
                    return new LocalDiskCentralStorage(localDiskConfig);
                case BlobCentralStoreConfiguration blobStoreConfig:
                    return new BlobCentralStorage(blobStoreConfig);
                default:
                    throw new NotSupportedException();
            }
        }

        /// <nodoc />
        public void PreStartupInitialize(Context context, ILocalContentStore localStore, IDistributedContentCopier copier, Func<OperationContext, ContentHash, Task<ResultBase>> proactiveCopyTaskFactory)
        {
            Contract.Requires(!StartupStarted, $"{nameof(PreStartupInitialize)} must be called before {nameof(StartupAsync)}");
            context.Debug($"Reconciliation enabled: {_configuration.EnableReconciliation}. Local content store provided: {localStore != null}");
            _localContentStore = localStore;

            _innerCentralStorage = CreateCentralStorage(_configuration.CentralStore);

            _proactiveReplicationTaskFactory = proactiveCopyTaskFactory;

            if (_configuration.DistributedCentralStore != null)
            {
                DistributedCentralStorage = new DistributedCentralStorage(_configuration.DistributedCentralStore, copier, fallbackStorage: _innerCentralStorage);
                CentralStorage = DistributedCentralStorage;
            }
            else
            {
                CentralStorage = _innerCentralStorage;
            }
        }

        private static void ValidateConfiguration(LocalLocationStoreConfiguration configuration)
        {
            Contract.Assert(configuration.Database != null, "Database configuration must be provided.");
            Contract.Assert(configuration.EventStore != null, "Event store configuration must be provided.");
            Contract.Assert(configuration.Checkpoint != null, "Checkpointing configuration must be provided.");
            Contract.Assert(configuration.CentralStore != null, "Central store configuration must be provided.");
        }

        /// <summary>
        /// Gets the counters with high level statistics associated with a current instance.
        /// </summary>
        public CounterSet GetCounters(Context context)
        {
            var counters = Counters.ToCounterSet();
            counters.Merge(Database.Counters.ToCounterSet(), "LocalDatabase.");
            counters.Merge(CentralStorage.Counters.ToCounterSet(), "CentralStorage.");

            if (EventStore != null)
            {
                counters.Merge(EventStore.GetCounters(), "EventStore.");
            }

            counters.Merge(GlobalStore.GetCounters(new OperationContext(context)), "GlobalStore.");
            return counters;
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            await _innerCentralStorage.StartupAsync(context).ThrowIfFailure();

            if (DistributedCentralStorage != null)
            {
                await DistributedCentralStorage.StartupAsync(context, new DistributedCentralStorageLocationStoreAdapter(this)).ThrowIfFailure();
            }

            _checkpointManager = new CheckpointManager(Database, GlobalStore, CentralStorage, _configuration.Checkpoint, Counters);

            EventStore = CreateEventStore(_configuration, "main");

            await GlobalStore.StartupAsync(context).ThrowIfFailure();

            await Database.StartupAsync(context).ThrowIfFailure();

            await EventStore.StartupAsync(context).ThrowIfFailure();

            MachineReputationTracker = new MachineReputationTracker(context, _clock, _configuration.ReputationTrackerConfiguration, ResolveMachineLocation, ClusterState);

            // Configuring a heartbeat timer. The timer is used differently by a master and by a worker.
            _heartbeatTimer = new Timer(
                _ =>
                {
                    var nestedContext = context.CreateNested();
                    HeartbeatAsync(nestedContext).FireAndForget(nestedContext);
                }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);


            if (_configuration.InlinePostInitialization)
            {
                // Perform the initial process state which starts the heartbeat timer and initializes role and checkpoint state
                // The initial processing step should be done asynchronously, because otherwise the startup may take way too much time (like, minutes).
                _postInitializationTask = Task.FromResult(await HeartbeatAsync(context, inline: true));
            }
            else
            {
                // Perform the initial process state which starts the heartbeat timer and initializes role and checkpoint state
                // The initial processing step should be done asynchronously, because otherwise the startup may take way too much time (like, minutes).
                _postInitializationTask = Task.Run(() => HeartbeatAsync(context, inline: true));
            }

            Database.DatabaseInvalidated = OnContentLocationDatabaseInvalidation;

            return BoolResult.Success;
        }

        private MachineLocation ResolveMachineLocation(MachineId machineId)
        {
            if (ClusterState.TryResolve(machineId, out var result))
            {
                return result;
            }

            throw new InvalidOperationException($"Unable to resolve machine location for machine id '{machineId}'.");
        }

        /// <inheritdoc />
        protected override async Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            Tracer.Info(context, "Shutting down local location store.");

            BoolResult result = BoolResult.Success;
            if (_postInitializationTask != null)
            {
                var postInitializationResult = await EnsureInitializedAsync();
                if (!postInitializationResult && !postInitializationResult.IsCancelled)
                {
                    result &= postInitializationResult;
                }
            }

#pragma warning disable AsyncFixer02
            _heartbeatTimer?.Dispose();
#pragma warning restore AsyncFixer02

            if (EventStore != null)
            {
                result &= await EventStore.ShutdownAsync(context);
            }

            result &= await Database.ShutdownAsync(context);

            CurrentRole = null;

            result &= await GlobalStore.ShutdownAsync(context);

            if (DistributedCentralStorage != null)
            {
                result &= await DistributedCentralStorage.ShutdownAsync(context);
            }

            result &= await _innerCentralStorage.ShutdownAsync(context);

            return result;
        }

        /// <summary>
        /// Releases current master role (other workers now can pick it up) and changes the current role to a newly acquired one.
        /// </summary>
        internal async Task ReleaseRoleIfNecessaryAsync(OperationContext operationContext)
        {
            CurrentRole = await GlobalStore.ReleaseRoleIfNecessaryAsync(operationContext);
        }

        /// <summary>
        /// Restore checkpoint.
        /// </summary>
        internal async Task<BoolResult> ProcessStateAsync(OperationContext context, CheckpointState checkpointState, bool inline, bool forceRestore = false)
        {
            return await RunOutOfBandAsync(
                inline,
                ref _pendingProcessCheckpointTask,
                context.CreateOperation(Tracer, async () =>
                {
                    BoolResult result = BoolResult.Success;

                    var oldRole = CurrentRole;
                    var newRole = checkpointState.Role;
                    var switchedRoles = oldRole != newRole;

                    if (switchedRoles)
                    {
                        Tracer.Debug(context, $"Switching Roles: New={newRole}, Old={oldRole}.");

                        // Local database should be immutable on workers and only master is responsible for collecting stale records
                        Database.SetDatabaseMode(isDatabaseWriteable: newRole == Role.Master);
                    }

                    // Always restore when switching roles
                    bool shouldRestore = switchedRoles;

                    // Restore if this is a worker and the last restore time is past the restore interval
                    shouldRestore |= (newRole == Role.Worker && ShouldSchedule(
                                          _configuration.Checkpoint.RestoreCheckpointInterval,
                                          _lastRestoreTime));

                    if (shouldRestore || forceRestore)
                    {
                        result = await RestoreCheckpointStateAsync(context, checkpointState);
                        if (!result)
                        {
                            return result;
                        }

                        _lastRestoreTime = _clock.UtcNow;

                        // Update the checkpoint time to avoid uploading a checkpoint immediately after restoring on the master
                        _lastCheckpointTime = _lastRestoreTime;
                    }

                    var updateResult = await UpdateClusterStateAsync(context);

                    if (!updateResult)
                    {
                        return updateResult;
                    }

                    if (newRole == Role.Master)
                    {
                        // Start receiving events from the given checkpoint
                        result = EventStore.StartProcessing(context, checkpointState.StartSequencePoint);
                    }
                    else
                    {
                        // Stop receiving events.
                        result = EventStore.SuspendProcessing(context);
                    }

                    if (!result)
                    {
                        return result;
                    }

                    if (newRole == Role.Master)
                    {
                        // Only create a checkpoint if the machine is currently a master machine and was a master machine
                        if (ShouldSchedule(_configuration.Checkpoint.CreateCheckpointInterval, _lastCheckpointTime))
                        {
                            result = await CreateCheckpointAsync(context);
                            if (!result)
                            {
                                return result;
                            }

                            _lastCheckpointTime = _clock.UtcNow;
                        }
                    }

                    // Successfully, applied changes for role. Set it as the current role.
                    CurrentRole = newRole;

                    return result;
                }));
        }

        internal async Task<BoolResult> HeartbeatAsync(OperationContext context, bool inline = false, bool forceRestore = false)
        {
            var result = await context.PerformOperationAsync(Tracer, () => processStateCoreAsync());

            if (result.Succeeded)
            {
                // A post initialization process may fail due to a transient issue, like a storage failure or an inconsistent checkpoint's state.
                // The transient error can go away and the system may recover itself by calling this method again.

                // In this case we need to reset _postInitializationTask and move its state from "failure" to "success"
                // and unblock all the public operations that will fail if post-initialization task is unsuccessful.

                _postInitializationTask = BoolResult.SuccessTask;
            }

            return result;

            async Task<BoolResult> processStateCoreAsync()
            {
                try
                {
                    using (SemaphoreSlimToken.TryWait(_heartbeatGate, 0, out var acquired))
                    {
                        // This makes sure that the heartbeat is only run once for each call to this function. It is a
                        // non-blocking check.
                        if (!acquired)
                        {
                            return BoolResult.Success;
                        }

                        var checkpointState = await GlobalStore.GetCheckpointStateAsync(context);

                        if (!checkpointState)
                        {
                            // The error is already logged.
                            return checkpointState;
                        }

                        return await ProcessStateAsync(context, checkpointState.Value, inline, forceRestore);
                    }
                }
                finally
                {
                    if (!ShutdownStarted)
                    {
                        // Reseting the timer at the end to avoid multiple calls if it at the same time.
                        _heartbeatTimer.Change(_configuration.Checkpoint.HeartbeatInterval, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }


        internal Task<BoolResult> UpdateClusterStateAsync(OperationContext context)
        {
            var startMaxMachineId = ClusterState.MaxMachineId;

            int postDbMaxMachineId = startMaxMachineId;
            int postGlobalMaxMachineId = startMaxMachineId;

            return context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    if (_configuration.Database.StoreClusterState)
                    {
                        Database.UpdateClusterState(context, ClusterState, write: false);
                        postDbMaxMachineId = ClusterState.MaxMachineId;
                    }

                    var updateResult = await GlobalStore.UpdateClusterStateAsync(context, ClusterState);
                    postGlobalMaxMachineId = ClusterState.MaxMachineId;

                    // Update the local database with new machines if the cluster state was updated from the global store
                    if (updateResult && (CurrentRole == Role.Master) && _configuration.Database.StoreClusterState)
                    {
                        Database.UpdateClusterState(context, ClusterState, write: true);
                    }

                    return updateResult;
                },
                extraEndMessage: result => $"[MaxMachineId=({startMaxMachineId} -> (Db={postDbMaxMachineId}, Global={postGlobalMaxMachineId}))]");
        }

        private bool ShouldSchedule(TimeSpan interval, DateTime lastTime)
        {
            return (_clock.UtcNow - lastTime) >= interval;
        }

        private Task<BoolResult> RunOutOfBandAsync(bool inline, ref Task<BoolResult> pendingTask, PerformAsyncOperationBuilder<BoolResult> operation, [CallerMemberName] string caller = null)
        {
            if (_configuration.InlinePostInitialization || inline)
            {
                operation.AppendStartMessage(extraStartMessage: "inlined=true");
                return operation.RunAsync(caller);
            }

            if (pendingTask != null)
            {
                if (pendingTask.IsCompleted)
                {
                    var resultTask = pendingTask;
                    pendingTask = null;
                    return resultTask;
                }

                return BoolResult.SuccessTask;
            }

            pendingTask = Task.Run(() => operation.RunAsync(caller));
            return BoolResult.SuccessTask;
        }

        internal async Task<BoolResult> CreateCheckpointAsync(OperationContext context)
        {
            // Need to obtain the sequence point first to avoid race between the sequence point and the database's state.
            EventSequencePoint currentSequencePoint = EventStore.GetLastProcessedSequencePoint();
            if (currentSequencePoint == null || currentSequencePoint.SequenceNumber == null)
            {
                Tracer.Debug(context.TracingContext, "Could not create a checkpoint because the sequence point is missing. Apparently, no events were processed at this time.");
                return BoolResult.Success;
            }

            return await _checkpointManager.CreateCheckpointAsync(context, currentSequencePoint);
        }

        private async Task<BoolResult> RestoreCheckpointStateAsync(OperationContext context, CheckpointState checkpointState)
        {
            var latestCheckpoint = _checkpointManager.GetLatestCheckpointInfo(context);
            var latestCheckpointAge = _clock.UtcNow - latestCheckpoint?.checkpointTime;

            // Only skip if this is the first restore and it is sufficiently recent
            // NOTE: _lastRestoreTime will be set since skipping this operation will return successful result.
            var shouldSkipRestore = _lastRestoreTime == default
                && latestCheckpoint != null
                && _configuration.Checkpoint.RestoreCheckpointAgeThreshold != default
                && latestCheckpoint.Value.checkpointTime.IsRecent(_clock.UtcNow, _configuration.Checkpoint.RestoreCheckpointAgeThreshold);

            if (latestCheckpointAge > _configuration.LocationEntryExpiry)
            {
                Tracer.Debug(context, $"Checkpoint {latestCheckpoint.Value.checkpointId} age is {latestCheckpointAge}, which is larger than location expiry {_configuration.LocationEntryExpiry}");
            }

            var latestCheckpointId = latestCheckpoint?.checkpointId ?? "null";
            if (shouldSkipRestore)
            {
                Tracer.Debug(context, $"First checkpoint {checkpointState} will be skipped. LatestCheckpointId={latestCheckpointId}, LatestCheckpointAge={latestCheckpointAge}, Threshold=[{_configuration.Checkpoint.RestoreCheckpointAgeThreshold}]");
                Counters[ContentLocationStoreCounters.RestoreCheckpointsSkipped].Increment();
                return BoolResult.Success;
            }
            else if (_lastRestoreTime == default)
            {
                Tracer.Debug(context, $"First checkpoint {checkpointState} will not be skipped. LatestCheckpointId={latestCheckpointId}, LatestCheckpointAge={latestCheckpointAge}, Threshold=[{_configuration.Checkpoint.RestoreCheckpointAgeThreshold}]");
            }

            if (checkpointState.CheckpointAvailable)
            {
                if (_lastCheckpointId != checkpointState.CheckpointId)
                {
                    Tracer.Debug(context, $"Restoring the checkpoint '{checkpointState}'.");
                    var possibleCheckpointResult = await _checkpointManager.RestoreCheckpointAsync(context, checkpointState);
                    if (!possibleCheckpointResult)
                    {
                        return possibleCheckpointResult;
                    }

                    _lastCheckpointId = checkpointState.CheckpointId;
                }
                else
                {
                    Tracer.Debug(context, $"Checkpoint '{checkpointState}' already restored.");
                }

                if (_localContentStore != null && !_reconciled && _configuration.EnableReconciliation)
                {
                    _reconciled = true;

                    // Trigger reconciliation after receiving first checkpoint
                    if (_configuration.InlinePostInitialization)
                    {
                        return await ReconcileAsync(context);
                    }
                    else
                    {
                        Task.Run(() => ReconcileAsync(context), context.Token).FireAndForget(context);
                    }
                }

                // Make sure to only have one thread doing proactive replication, in case last proactive replication is still happening.
                if (_configuration.EnableProactiveReplication)
                {
                    if (_configuration.InlineProactiveReplication)
                    {
                        var result = await ProactiveReplicationAsync(context);

                        if (!result.Succeeded)
                        {
                            return new BoolResult(result);
                        }
                    }
                    else
                    {
                        var cts = new CancellationTokenSource();

                        // Make sure that two different threads don't go into a race condition, potentially having two threads doing proactive replication
                        lock (_proactiveReplicationLockObject)
                        {
                            _lastProactiveReplicationTokenSource?.Cancel();
                            _lastProactiveReplicationTokenSource?.Dispose();
                            _lastProactiveReplicationTokenSource = cts;
                        }

                        WithOperationContext(
                            context,
                            cts.Token,
                            opContext => ProactiveReplicationAsync(opContext)
                            ).FireAndForget(context);
                    }
                }
            }

            return BoolResult.Success;
        }

        /// <summary>
        /// Gets the list of <see cref="ContentLocationEntry"/> for every hash specified by <paramref name="contentHashes"/> from a given <paramref name="origin"/>.
        /// </summary>
        public async Task<GetBulkLocationsResult> GetBulkAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes, GetBulkOrigin origin)
        {
            Contract.Requires(contentHashes != null);

            var postInitializationResult = await EnsureInitializedAsync();
            if (!postInitializationResult)
            {
                return new GetBulkLocationsResult(postInitializationResult);
            }

            if (contentHashes.Count == 0)
            {
                return new GetBulkLocationsResult(CollectionUtilities.EmptyArray<ContentHashWithSizeAndLocations>(), origin);
            }

            var result = await context.PerformOperationAsync(
                Tracer,
                () => GetBulkCoreAsync(context, contentHashes, origin),
                traceOperationStarted: false,
                extraEndMessage: r => $"GetBulk({origin}) => [{r.GetShortHashesTraceString()}]");

            return result;
        }

        private Task<GetBulkLocationsResult> GetBulkCoreAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes, GetBulkOrigin origin)
        {
            if (origin == GetBulkOrigin.Local)
            {
                // Query local db
                return GetBulkFromLocalAsync(context, contentHashes);
            }
            else
            {
                // Query global store
                return GetBulkFromGlobalAsync(context, contentHashes);
            }
        }

        internal Task<GetBulkLocationsResult> GetBulkFromGlobalAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes)
        {
            Counters[ContentLocationStoreCounters.GetBulkGlobalHashes].Add(contentHashes.Count);

            return context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    var entries = await GlobalStore.GetBulkAsync(context, contentHashes);
                    if (!entries)
                    {
                        return new GetBulkLocationsResult(entries);
                    }

                    return await ResolveLocationsAsync(context, entries.Value, contentHashes, GetBulkOrigin.Global);
                },
                Counters[ContentLocationStoreCounters.GetBulkGlobal],
                traceErrorsOnly: true); // Intentionally tracing errors only.
        }

        private Task<GetBulkLocationsResult> GetBulkFromLocalAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes)
        {
            Counters[ContentLocationStoreCounters.GetBulkLocalHashes].Add(contentHashes.Count);

            return context.PerformOperationAsync(
                Tracer,
                () =>
                {
                    var entries = new List<ContentLocationEntry>(contentHashes.Count);
                    var touchEventHashes = new List<ContentHash>();
                    var now = _clock.UtcNow;

                    foreach (var hash in contentHashes)
                    {
                        if (TryGetContentLocations(context, hash, out var entry))
                        {
                            if ((entry.LastAccessTimeUtc.ToDateTime() + _configuration.TouchFrequency < now)
                                && _recentlyTouchedHashes.Add(hash, _configuration.TouchFrequency))
                            {
                                // Entry was not touched recently so no need to send a touch event
                                touchEventHashes.Add(hash);
                            }
                        }
                        else
                        {
                            // NOTE: Entries missing from the local db are not touched. They referring to content which is no longer
                            // in the system or content which is new and has not been propagated through local db
                            entry = ContentLocationEntry.Missing;
                        }

                        entries.Add(entry);
                    }

                    if (touchEventHashes.Count != 0)
                    {
                        EventStore.Touch(context, LocalMachineId, touchEventHashes, _clock.UtcNow).ThrowIfFailure();
                    }

                    return ResolveLocationsAsync(context, entries, contentHashes, GetBulkOrigin.Local);
                },
                traceErrorsOnly: true, // Intentionally tracing errors only.
                counter: Counters[ContentLocationStoreCounters.GetBulkLocal]);
        }

        private async Task<GetBulkLocationsResult> ResolveLocationsAsync(OperationContext context, IReadOnlyList<ContentLocationEntry> entries, IReadOnlyList<ContentHash> contentHashes, GetBulkOrigin origin)
        {
            Contract.Requires(entries.Count == contentHashes.Count);

            var results = new List<ContentHashWithSizeAndLocations>(entries.Count);
            bool hasUnknownLocations = false;

            for (int i = 0; i < entries.Count; i++)
            {
                // TODO: Its probably possible to do this by getting the max machine id in the locations set rather than enumerating all of them (bug 1365340)
                var entry = entries[i];
                foreach (var machineId in entry.Locations.EnumerateMachineIds())
                {
                    if (!ClusterState.TryResolve(machineId, out _))
                    {
                        hasUnknownLocations = true;
                    }
                }

                var contentHash = contentHashes[i];
                results.Add(new ContentHashWithSizeAndLocations(contentHash, entry.ContentSize, GetMachineList(contentHash, entry)));
            }

            // Machine locations are resolved lazily by MachineList.
            // If we faced at least one unknown machine location we're forcing an update of a cluster state to make the resolution successful.
            if (hasUnknownLocations)
            {
                // Update cluster. Query global to ensure that we have all machines ids (even those which may not be added
                // to local db yet.)
                var result = await UpdateClusterStateAsync(context);
                if (!result)
                {
                    return new GetBulkLocationsResult(result);
                }
            }

            return new GetBulkLocationsResult(results, origin);
        }

        private IReadOnlyList<MachineLocation> GetMachineList(ContentHash hash, ContentLocationEntry entry)
        {
            if (entry.IsMissing)
            {
                return CollectionUtilities.EmptyArray<MachineLocation>();
            }

            return new MachineList(
                entry.Locations,
                machineId =>
                {
                    return ResolveMachineLocation(machineId);
                },
                MachineReputationTracker,
                randomize: true);
        }

        /// <summary>
        /// Gets content locations for a given <paramref name="hash"/> from a local database.
        /// </summary>
        private bool TryGetContentLocations(OperationContext context, ContentHash hash, out ContentLocationEntry entry)
        {
            using (Counters[ContentLocationStoreCounters.DatabaseGet].Start())
            {
                if (Database.TryGetEntry(context, hash, out entry))
                {
                    return true;
                }

                return false;
            }
        }

        private enum RegisterAction
        {
            EagerGlobal,
            RecentInactiveEagerGlobal,
            RecentRemoveEagerGlobal,
            LazyEventOnly,
            LazyTouchEventOnly,
            SkippedDueToRecentAdd,
            SkippedDueToRedundantAdd,
        }

        private enum RegisterCoreAction
        {
            Skip,
            Events,
            Global,
        }

        private static RegisterCoreAction ToCoreAction(RegisterAction action)
        {
            switch (action)
            {
                case RegisterAction.EagerGlobal:
                case RegisterAction.RecentInactiveEagerGlobal:
                case RegisterAction.RecentRemoveEagerGlobal:
                    return RegisterCoreAction.Global;
                case RegisterAction.LazyEventOnly:
                case RegisterAction.LazyTouchEventOnly:
                    return RegisterCoreAction.Events;
                case RegisterAction.SkippedDueToRecentAdd:
                case RegisterAction.SkippedDueToRedundantAdd:
                    return RegisterCoreAction.Skip;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, $"Unexpected action '{action}'.");
            }
        }

        private RegisterAction GetRegisterAction(OperationContext context, ContentHash hash, DateTime now)
        {
            if (_configuration.SkipRedundantContentLocationAdd && _recentlyRemovedHashes.Contains(hash))
            {
                // Content was recently removed. Eagerly register with global store.
                Counters[ContentLocationStoreCounters.LocationAddRecentRemoveEager].Increment();
                return RegisterAction.RecentRemoveEagerGlobal;
            }

            if (ClusterState.LastInactiveTime.IsRecent(now, _configuration.RecomputeInactiveMachinesExpiry.Multiply(5)))
            {
                // The machine was recently inactive. We should eagerly register content for some amount of time (a few heartbeats) because content may be currently filtered from other machines
                // local db results due to inactive machines filter.
                Counters[ContentLocationStoreCounters.LocationAddRecentInactiveEager].Increment();
                return RegisterAction.RecentInactiveEagerGlobal;
            }

            if (_configuration.SkipRedundantContentLocationAdd && _recentlyAddedHashes.Contains(hash))
            {
                // Content was recently added for the machine by a prior operation
                Counters[ContentLocationStoreCounters.RedundantRecentLocationAddSkipped].Increment();
                return RegisterAction.SkippedDueToRecentAdd;
            }

            // Query local db and only eagerly update global store if replica count is below threshold
            if (TryGetContentLocations(context, hash, out var entry))
            {
                // TODO[LLS]: Is it ok to ignore a hash already registered to the machine. There is a subtle race condition (bug 1365340)
                // here if the hash is deleted and immediately added to the machine
                if (_configuration.SkipRedundantContentLocationAdd
                    && entry.Locations[LocalMachineId.Index]) // content is registered for this machine
                {
                    // If content was touched recently, we can skip. Otherwise, we touch via event
                    if (entry.LastAccessTimeUtc.ToDateTime().IsRecent(now, _configuration.TouchFrequency))
                    {
                        Counters[ContentLocationStoreCounters.RedundantLocationAddSkipped].Increment();
                        return RegisterAction.SkippedDueToRedundantAdd;
                    }
                    else
                    {
                        Counters[ContentLocationStoreCounters.LazyTouchEventOnly].Increment();
                        return RegisterAction.LazyTouchEventOnly;
                    }
                }

                // The entry is not on the machine, we definitely need to register the content location via event stream
                if (entry.Locations.Count >= _configuration.SafeToLazilyUpdateMachineCountThreshold)
                {
                    Counters[ContentLocationStoreCounters.LocationAddQueued].Increment();
                    return RegisterAction.LazyEventOnly;
                }
            }

            Counters[ContentLocationStoreCounters.LocationAddEager].Increment();
            return RegisterAction.EagerGlobal;
        }

        private Task<BoolResult> EnsureInitializedAsync()
        {
            Contract.Assert(_postInitializationTask != null);

            return _postInitializationTask;
        }

        /// <summary>
        /// Notifies a central store that content represented by <paramref name="contentHashes"/> is available on a current machine.
        /// </summary>
        public async Task<BoolResult> RegisterLocalLocationAsync(OperationContext context, IReadOnlyList<ContentHashWithSize> contentHashes, bool touch)
        {
            Contract.Requires(contentHashes != null);

            var postInitializationResult = await EnsureInitializedAsync();
            if (!postInitializationResult)
            {
                return postInitializationResult;
            }

            if (contentHashes.Count == 0)
            {
                return BoolResult.Success;
            }

            string extraMessage = string.Empty;
            return await context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    var eventContentHashes = new List<ContentHashWithSize>(contentHashes.Count);
                    var eagerContentHashes = new List<ContentHashWithSize>(contentHashes.Count);
                    var actions = new List<RegisterAction>(contentHashes.Count);
                    var now = _clock.UtcNow;

                    // Select which hashes are not already registered for the local machine and those which must eagerly go to the global store
                    foreach (var contentHash in contentHashes)
                    {
                        var registerAction = GetRegisterAction(context, contentHash.Hash, now);
                        actions.Add(registerAction);

                        var coreAction = ToCoreAction(registerAction);
                        if (coreAction == RegisterCoreAction.Skip)
                        {
                            continue;
                        }

                        // In both cases (RegisterCoreAction.Events and RegisterCoreAction.Global)
                        // we need to send an events over event hub.
                        eventContentHashes.Add(contentHash);

                        if (coreAction == RegisterCoreAction.Global)
                        {
                            eagerContentHashes.Add(contentHash);
                        }
                    }

                    var registerActionsMessage = string.Join(", ", contentHashes.Select((c, i) => $"{new ShortHash(c.Hash)}={actions[i]}"));
                    extraMessage = $"Register actions(Eager={eagerContentHashes.Count.ToString()}, Event={eventContentHashes.Count.ToString()}): [{registerActionsMessage}]";

                    if (eagerContentHashes.Count != 0)
                    {
                        // Update global store
                        await GlobalStore.RegisterLocalLocationAsync(context, eagerContentHashes).ThrowIfFailure();
                    }

                    if (eventContentHashes.Count != 0)
                    {
                        // Send add events
                        EventStore.AddLocations(context, LocalMachineId, eventContentHashes, touch).ThrowIfFailure();
                    }

                    // Register all recently added hashes so subsequent operations do not attempt to re-add
                    if (_configuration.SkipRedundantContentLocationAdd)
                    {
                        foreach (var hash in eventContentHashes)
                        {
                            _recentlyAddedHashes.Add(hash.Hash, _configuration.TouchFrequency);
                        }

                        // Only eagerly added hashes should invalidate recently removed hashes.
                        foreach (var hash in eagerContentHashes)
                        {
                            _recentlyRemovedHashes.Invalidate(hash.Hash);
                        }
                    }

                    return BoolResult.Success;
                },
                Counters[ContentLocationStoreCounters.RegisterLocalLocation],
                traceOperationStarted: false,
                extraEndMessage: _ => extraMessage);
        }

        /// <nodoc />
        public async Task<BoolResult> TouchBulkAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes)
        {
            Contract.Requires(contentHashes != null);

            var postInitializationResult = await EnsureInitializedAsync();
            if (!postInitializationResult)
            {
                return postInitializationResult;
            }

            if (contentHashes.Count == 0)
            {
                return BoolResult.Success;
            }

            return context.PerformOperation(
                Tracer,
                () =>
                {
                    var touchEventHashes = new List<ContentHash>();
                    var now = _clock.UtcNow;

                    foreach (var hash in contentHashes)
                    {
                        if (_recentlyAddedHashes.Contains(hash) || !_recentlyTouchedHashes.Add(hash, _configuration.TouchFrequency))
                        {
                            continue;
                        }

                        if (TryGetContentLocations(context, hash, out var entry) && (entry.LastAccessTimeUtc.ToDateTime() + _configuration.TouchFrequency) > now)
                        {
                            continue;
                        }

                        // Entry was not touched recently so no need to send a touch event
                        touchEventHashes.Add(hash);
                    }

                    if (touchEventHashes.Count != 0)
                    {
                        return EventStore.Touch(context, LocalMachineId, touchEventHashes, now);
                    }

                    return BoolResult.Success;
                },
                Counters[ContentLocationStoreCounters.BackgroundTouchBulk]);
        }

        /// <nodoc />
        public async Task<BoolResult> TrimBulkAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes)
        {
            Contract.Requires(contentHashes != null);

            var postInitializationResult = await EnsureInitializedAsync();
            if (!postInitializationResult)
            {
                return postInitializationResult;
            }

            if (contentHashes.Count == 0)
            {
                return BoolResult.Success;
            }

            foreach (var contentHashesPage in contentHashes.GetPages(100))
            {
                context.TraceDebug($"LocalLocationStore.TrimBulk({contentHashesPage.GetShortHashesTraceString()})");
            }

            return context.PerformOperation(
                Tracer,
                () =>
                {
                    if (_configuration.SkipRedundantContentLocationAdd)
                    {
                        foreach (var hash in contentHashes)
                        {
                            // Content has been removed. Ensure that subsequent additions will not be skipped
                            _recentlyAddedHashes.Invalidate(hash);
                            _recentlyRemovedHashes.Add(hash, _configuration.TouchFrequency);
                        }
                    }

                    // Send remove event for hashes
                    return EventStore.RemoveLocations(context, LocalMachineId, contentHashes);
                },
                Counters[ContentLocationStoreCounters.TrimBulkLocal]);
        }

        /// <summary>
        /// Computes content hashes with effective last access time sorted in LRU manner.
        /// </summary>
        public IEnumerable<ContentHashWithLastAccessTimeAndReplicaCount> GetHashesInEvictionOrder(
            Context context,
            IReadOnlyList<ContentHashWithLastAccessTimeAndReplicaCount> contentHashesWithInfo,
            bool reverse)
        {
            // Counter for successful eviction candidates. Different than total number of eviction candidates, because this only increments when candidate is above minimum eviction age
            int evictionCount = 0;

            // contentHashesWithInfo is literally all data inside the content directory. The Purger wants to remove
            // content until we are within quota. Here we return batches of content to be removed.

            // contentHashesWithInfo is sorted by (local) LastAccessTime in descending order (Least Recently Used).
            if (contentHashesWithInfo.Count != 0)
            {
                var first = contentHashesWithInfo[0];
                var last = contentHashesWithInfo[contentHashesWithInfo.Count - 1];

                context.Debug($"{nameof(GetHashesInEvictionOrder)} start with contentHashesWithInfo.Count={contentHashesWithInfo.Count}, firstAge={first.Age(_clock)}, lastAge={last.Age(_clock)}");
            }

            var operationContext = new OperationContext(context);

            // Ideally, we want to remove content we know won't be used again for quite a while. We don't have that
            // information, so we use an evictability metric. Here we obtain and sort by that evictability metric.

            // Assume that EffectiveLastAccessTime will always have a value.
            var comparer = Comparer<ContentHashWithLastAccessTimeAndReplicaCount>.Create((c1, c2) => (reverse ? -1 : 1) * c1.EffectiveLastAccessTime.Value.CompareTo(c2.EffectiveLastAccessTime.Value));

            Func<List<ContentHashWithLastAccessTimeAndReplicaCount>, IEnumerable<ContentHashWithLastAccessTimeAndReplicaCount>> intoEffectiveLastAccessTimes =
                page => GetEffectiveLastAccessTimes(
                            operationContext,
                            page.SelectList(v => new ContentHashWithLastAccessTime(v.ContentHash, v.LastAccessTime))).ThrowIfFailure();

            // We make sure that we select a set of the newer content, to ensure that we at least look at newer
            // content to see if it should be evicted first due to having a high number of replicas. We do this by
            // looking at the start as well as at middle of the list.

            // NOTE(jubayard, 12/13/2019): observe that the comparer is the one that decides whether we sort in
            // ascending or descending order by evictability. The oldest and newest elude to the content's actual age,
            // not the evictability metric.
            var oldestContentSortedByEvictability = contentHashesWithInfo
                .Take(contentHashesWithInfo.Count / 2)
                .ApproximateSort(comparer, intoEffectiveLastAccessTimes, _configuration.EvictionPoolSize, _configuration.EvictionWindowSize, _configuration.EvictionRemovalFraction, _configuration.EvictionDiscardFraction);

            var newestContentSortedByEvictability = contentHashesWithInfo
                .SkipOptimized(contentHashesWithInfo.Count / 2)
                .ApproximateSort(comparer, intoEffectiveLastAccessTimes, _configuration.EvictionPoolSize, _configuration.EvictionWindowSize, _configuration.EvictionRemovalFraction, _configuration.EvictionDiscardFraction);

            return NuCacheCollectionUtilities.MergeOrdered(oldestContentSortedByEvictability, newestContentSortedByEvictability, comparer)
                .Where((candidate, index) => IsPassEvictionAge(context, candidate, _configuration.EvictionMinAge, index, ref evictionCount));
        }

        private bool IsPassEvictionAge(Context context, ContentHashWithLastAccessTimeAndReplicaCount candidate, TimeSpan evictionMinAge, int index, ref int evictionCount)
        {
            if (candidate.Age(_clock) >= evictionMinAge)
            {
                evictionCount++;
                return true;
            }

            context.Debug($"Previous successful eviction attempts = {evictionCount}, Total eviction attempts previously = {index}, minimum eviction age = {evictionMinAge.ToString()}, pool size = {_configuration.EvictionPoolSize}." +
                $" Candidate replica count = {candidate.ReplicaCount}, effective age = {candidate.EffectiveAge(_clock)}, age = {candidate.Age(_clock)}.");
            return false;
        }

        /// <summary>
        /// Returns effective last access time for all the <paramref name="contentHashes"/>.
        /// </summary>
        /// <remarks>
        /// Effective last access time is computed based on entries last access time considering content's size and replica count.
        /// This method is used in distributed eviction.
        /// </remarks>
        public Result<IReadOnlyList<ContentHashWithLastAccessTimeAndReplicaCount>> GetEffectiveLastAccessTimes(
            OperationContext context,
            IReadOnlyList<ContentHashWithLastAccessTime> contentHashes)
        {
            Contract.Requires(contentHashes != null);
            Contract.Requires(contentHashes.Count > 0);

            var postInitializationResult = EnsureInitializedAsync().GetAwaiter().GetResult();
            if (!postInitializationResult)
            {
                return new Result<IReadOnlyList<ContentHashWithLastAccessTimeAndReplicaCount>>(postInitializationResult);
            }

            // This is required because the code inside could throw.
            return context.PerformOperation(
                Tracer,
                () =>
                {
                    var effectiveLastAccessTimes = new List<ContentHashWithLastAccessTimeAndReplicaCount>();
                    double logInverseMachineRisk = -Math.Log(_configuration.MachineRisk);

                    foreach (var contentHash in contentHashes)
                    {
                        DateTime lastAccessTime = contentHash.LastAccessTime;
                        int replicaCount = 1;
                        DateTime? effectiveLastAccessTime = null;

                        if (TryGetContentLocations(context, contentHash.Hash, out var entry))
                        {
                            // Use the latest last access time between LLS and local last access time
                            DateTime distributedLastAccessTime = entry.LastAccessTimeUtc.ToDateTime();
                            lastAccessTime = distributedLastAccessTime > lastAccessTime ? distributedLastAccessTime : lastAccessTime;

                            // TODO[LLS]: Maybe some machines should be primary replicas for the content and not prioritize deletion (bug 1365340)
                            // just because there are many replicas
                            replicaCount = entry.Locations.Count;

                            // Incorporate both replica count and size into an evictability metric.
                            // It's better to eliminate big content (more bytes freed per eviction) and it's better to eliminate content with more replicas (less chance
                            // of all replicas being inaccessible).
                            // A simple model with exponential decay of likelihood-to-use and a fixed probability of each replica being inaccessible shows that the metric
                            //   evictability = age + (time decay parameter) * (-log(risk of content unavailability) * (number of replicas) + log(size of content))
                            // minimizes the increase in the probability of (content wanted && all replicas inaccessible) / per bytes freed.
                            // Since this metric is just the age plus a computed quantity, it can be intrepreted as an "effective age".
                            TimeSpan totalReplicaPenalty = TimeSpan.FromMinutes(_configuration.ContentLifetime.TotalMinutes * (Math.Max(1, replicaCount) * logInverseMachineRisk + Math.Log(Math.Max(1, entry.ContentSize))));
                            effectiveLastAccessTime = lastAccessTime - totalReplicaPenalty;

                            Counters[ContentLocationStoreCounters.EffectiveLastAccessTimeLookupHit].Increment();
                        }
                        else
                        {
                            Counters[ContentLocationStoreCounters.EffectiveLastAccessTimeLookupMiss].Increment();
                        }

                        effectiveLastAccessTimes.Add(new ContentHashWithLastAccessTimeAndReplicaCount(contentHash.Hash, lastAccessTime, replicaCount, effectiveLastAccessTime: effectiveLastAccessTime ?? lastAccessTime));
                    }

                    return Result.Success<IReadOnlyList<ContentHashWithLastAccessTimeAndReplicaCount>>(effectiveLastAccessTimes);
                }, Counters[ContentLocationStoreCounters.GetEffectiveLastAccessTimes],
                traceOperationStarted: false);
        }

        /// <summary>
        /// Forces reconciliation process between local content store and LLS.
        /// </summary>
        public async Task<ReconciliationResult> ReconcileAsync(OperationContext context)
        {
            Contract.Requires(_localContentStore != null);

            var token = context.Token;
            var postInitializationResult = await EnsureInitializedAsync();
            if (!postInitializationResult)
            {
                return new ReconciliationResult(postInitializationResult);
            }

            return await context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    if (IsReconcileUpToDate())
                    {
                        return new ReconciliationResult(addedCount: 0, removedCount: 0, totalLocalContentCount: -1);
                    }

                    token.ThrowIfCancellationRequested();

                    var totalAddedContent = 0;
                    var totalRemovedContent = 0;
                    var allLocalStoreContentCount = 0;
                    ShortHash? lastProcessedHash = null;
                    var isFinished = false;

                    while (!isFinished)
                    {
                        var delayTask = Task.Delay(_configuration.ReconciliationCycleFrequency, context.Token);

                        await context.PerformOperationAsync(
                            Tracer,
                            operation: performReconciliationCycleAsync,
                            caller: "PerformReconciliationCycleAsync",
                            counter: Counters[ContentLocationStoreCounters.ReconciliationCycles]).ThrowIfFailure();

                        if (!isFinished)
                        {
                            await delayTask;
                        }
                    }

                    MarkReconciled();
                    return new ReconciliationResult(addedCount: totalAddedContent, removedCount: totalRemovedContent, totalLocalContentCount: allLocalStoreContentCount);

                    async Task<BoolResult> performReconciliationCycleAsync()
                    {
                        // Pause events in main event store while sending reconciliation events via temporary event store
                        // to ensure reconciliation does cause some content to be lost due to apply reconciliation changes
                        // in the wrong order. For instance, if a machine has content [A] and [A] is removed during reconciliation.
                        // It is possible that remove event could be sent before reconciliation event and the final state
                        // in the database would still have missing content [A].
                        using (EventStore.PauseSendingEvents())
                        {
                            var allLocalStoreContentInfos = await _localContentStore.GetContentInfoAsync(token);
                            token.ThrowIfCancellationRequested();

                            var allLocalStoreContent = allLocalStoreContentInfos
                                .Select(c => (hash: new ShortHash(c.ContentHash), size: c.Size))
                                .OrderBy(c => c.hash)
                                .SkipWhile(hashWithSize => lastProcessedHash.HasValue && hashWithSize.hash < lastProcessedHash.Value)
                                .ToList();

                            allLocalStoreContentCount = allLocalStoreContent.Count;

                            var dbContent = Database.EnumerateSortedHashesWithContentSizeForMachineId(context, LocalMachineId, startingPoint: lastProcessedHash);
                            token.ThrowIfCancellationRequested();

                            // Diff the two views of the local machines content (left = local store, right = content location db)
                            // Then send changes as events
                            var diffedContent = NuCacheCollectionUtilities.DistinctDiffSorted(leftItems: allLocalStoreContent, rightItems: dbContent, t => t.hash);
                            diffedContent = diffedContent.Take(_configuration.ReconciliationMaxCycleSize);

                            var addedContent = new List<ShortHashWithSize>();
                            var removedContent = new List<ShortHash>();

                            foreach (var diffItem in diffedContent)
                            {
                                lastProcessedHash = diffItem.item.hash;

                                if (diffItem.mode == MergeMode.LeftOnly)
                                {
                                    // Content is not in DB but is in the local store need to send add event
                                    addedContent.Add(new ShortHashWithSize(diffItem.item.hash, diffItem.item.size));
                                }
                                else
                                {
                                    // Content is in DB but is not local store need to send remove event
                                    removedContent.Add(diffItem.item.hash);
                                }
                            }

                            Counters[ContentLocationStoreCounters.Reconcile_AddedContent].Add(addedContent.Count);
                            Counters[ContentLocationStoreCounters.Reconcile_RemovedContent].Add(removedContent.Count);
                            totalAddedContent += addedContent.Count;
                            totalRemovedContent += removedContent.Count;

                            // Only call reconcile if content needs to be updated for machine
                            if (addedContent.Count != 0 || removedContent.Count != 0)
                            {
                                // Create separate event store for reconciliation events so they are dispatched first before
                                // events in normal event store which may be queued during reconciliation operation.
                                var reconciliationEventStore = CreateEventStore(_configuration, "reconcile");

                                try
                                {
                                    await reconciliationEventStore.StartupAsync(context).ThrowIfFailure();

                                    await reconciliationEventStore.ReconcileAsync(context, LocalMachineId, addedContent, removedContent).ThrowIfFailure();
                                }
                                finally
                                {
                                    await reconciliationEventStore.ShutdownAsync(context).ThrowIfFailure();
                                }
                            }

                            // Corner case where they are equal and we have finished should be very unlikely.
                            isFinished = (addedContent.Count + removedContent.Count) < _configuration.ReconciliationMaxCycleSize;

                            return BoolResult.Success;
                        }
                    }
                },
                Counters[ContentLocationStoreCounters.Reconcile]);
        }

        /// <nodoc />
        public Task<BoolResult> InvalidateLocalMachineAsync(OperationContext operationContext)
        {
            // Unmark reconcile since the machine is invalidated
            MarkReconciled(reconciled: false);

            return GlobalStore.InvalidateLocalMachineAsync(operationContext);
        }

        /// <nodoc />
        public async Task<BoolResult> PutBlobAsync(OperationContext context, ContentHash hash, byte[] blob)
        {
            return await GlobalStore.PutBlobAsync(context, hash, blob);
        }

        /// <nodoc />
        public Task<GetBlobResult> GetBlobAsync(OperationContext context, ContentHash hash)
        {
            return GlobalStore.GetBlobAsync(context, hash);
        }

        private void OnContentLocationDatabaseInvalidation(OperationContext context, Failure<Exception> failure)
        {
            OnContentLocationDatabaseInvalidationAsync(context, failure).FireAndForget(context);
        }

        private async Task OnContentLocationDatabaseInvalidationAsync(OperationContext context, Failure<Exception> failure)
        {
            Contract.Requires(failure != null);

            using (SemaphoreSlimToken.TryWait(_databaseInvalidationGate, 0, out var acquired))
            {
                // If multiple threads fail at the same time (i.e. corruption error), this cheaply deduplicates the
                // restores, avoids redundant logging. This is a non-blocking check.
                if (!acquired)
                {
                    return;
                }

                Tracer.Error(context, $"Content location database has been invalidated. Forcing a restore from the last checkpoint. Error: {failure.DescribeIncludingInnerFailures()}");

                // We can safely ignore errors, because there is nothing more we can do here.
                await HeartbeatAsync(context, forceRestore: true).IgnoreFailure();
            }
        }

        /// <summary>
        /// Adapts <see cref="LocalLocationStore"/> to interface needed for content locations (<see cref="DistributedCentralStorage.ILocationStore"/>) by
        /// <see cref="NuCache.DistributedCentralStorage"/>
        /// </summary>
        private class DistributedCentralStorageLocationStoreAdapter : DistributedCentralStorage.ILocationStore
        {
            public MachineId LocalMachineId => _store.LocalMachineId;
            public ClusterState ClusterState => _store.ClusterState;

            private readonly LocalLocationStore _store;

            public DistributedCentralStorageLocationStoreAdapter(LocalLocationStore store)
            {
                _store = store;
            }

            public Task<GetBulkLocationsResult> GetBulkAsync(OperationContext context, IReadOnlyList<ContentHash> contentHashes)
            {
                return _store.GetBulkFromGlobalAsync(context, contentHashes);
            }

            public Task<BoolResult> RegisterLocalLocationAsync(OperationContext context, IReadOnlyList<ContentHashWithSize> contentInfo)
            {
                return _store.GlobalStore.RegisterLocalLocationAsync(context, contentInfo);
            }
        }

        private Task<ProactiveReplicationResult> ProactiveReplicationAsync(OperationContext context)
        {
            return context.PerformOperationAsync(
                Tracer,
                async () =>
                {
                    var localContent = (await _localContentStore.GetContentInfoAsync(context.Token))
                        .OrderByDescending(info => info.LastAccessTimeUtc) // GetHashesInEvictionOrder expects entries to already be ordered by last access time.
                        .Select(info => new ContentHashWithLastAccessTimeAndReplicaCount(info.ContentHash, info.LastAccessTimeUtc))
                        .ToArray();

                    var contents = GetHashesInEvictionOrder(context, localContent, reverse: true);

                    var succeeded = 0;
                    var failed = 0;
                    Task delayTask = Task.FromResult(0);
                    foreach (var content in contents)
                    {
                        context.Token.ThrowIfCancellationRequested();

                        if (Database.TryGetEntry(context, content.ContentHash, out var entry))
                        {
                            if (entry.Locations.Count < _configuration.ProactiveCopyLocationsThreshold)
                            {
                                await delayTask;
                                delayTask = Task.Delay(_configuration.DelayForProactiveReplication);

                                var result = await _proactiveReplicationTaskFactory(context, content.ContentHash);

                                if (result.Succeeded)
                                {
                                    Counters[ContentLocationStoreCounters.ProactiveReplication_Succeeded].Increment();
                                    succeeded++;
                                }
                                else
                                {
                                    Counters[ContentLocationStoreCounters.ProactiveReplication_Failed].Increment();
                                    failed++;
                                }

                                if ((succeeded + failed) >= _configuration.ProactiveReplicationCopyLimit)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    return new ProactiveReplicationResult(succeeded, failed, localContent.Length);
                },
                counter: Counters[ContentLocationStoreCounters.ProactiveReplication]);
        }

        private class ContentLocationDatabaseAdapter : IContentLocationEventHandler
        {
            private readonly ContentLocationDatabase _database;
            private readonly ClusterState _clusterState;

            /// <nodoc />
            public ContentLocationDatabaseAdapter(ContentLocationDatabase database, ClusterState clusterState)
            {
                _database = database;
                _clusterState = clusterState;
            }

            /// <inheritdoc />
            public void ContentTouched(OperationContext context, MachineId sender, IReadOnlyList<ShortHash> hashes, UnixTime accessTime)
            {
                _clusterState.MarkMachineActive(sender);

                foreach (var hash in hashes.AsStructEnumerable())
                {
                    _database.ContentTouched(context, hash, accessTime);
                }
            }

            /// <inheritdoc />
            public void LocationAdded(OperationContext context, MachineId sender, IReadOnlyList<ShortHashWithSize> hashes, bool reconciling, bool updateLastAccessTime)
            {
                _clusterState.MarkMachineActive(sender);

                foreach (var hashWithSize in hashes.AsStructEnumerable())
                {
                    _database.LocationAdded(context, hashWithSize.Hash, sender, hashWithSize.Size, reconciling, updateLastAccessTime);
                }
            }

            /// <inheritdoc />
            public void LocationRemoved(OperationContext context, MachineId sender, IReadOnlyList<ShortHash> hashes, bool reconciling)
            {
                _clusterState.MarkMachineActive(sender);
                foreach (var hash in hashes.AsStructEnumerable())
                {
                    _database.LocationRemoved(context, hash, sender, reconciling);
                }
            }
        }
    }
}
