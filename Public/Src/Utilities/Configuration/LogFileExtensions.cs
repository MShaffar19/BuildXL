// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// The names of various log files we produce during the build
    /// </summary>
    public static class LogFileExtensions
    {
        /// <summary>
        /// Ther default prefix for all log files.
        /// </summary>
        public const string DefaultLogPrefix = "BuildXL";

        /// <summary>
        /// The log file produced by BuildXL
        /// </summary>
        public const string Log = ".log";

        /// <summary>
        /// The binary execution log
        /// </summary>
        public const string Execution = ".xlg";

        /// <summary>
        /// A file containing only the warning messages from BuildXL.log
        /// </summary>
        public const string Warnings = ".wrn";

        /// <summary>
        /// A file containing only the error messages from BuildXL.log
        /// </summary>
        public const string Errors = ".err";

        /// <summary>
        /// The log file used by the cache
        /// </summary>
        public const string Cache = ".cache.log";

        /// <summary>
        /// A file containing execution statistics
        /// </summary>
        public const string Stats = ".stats";

        /// <summary>
        /// A file containing execution performance statistics in json
        /// </summary>
        public const string StatsPrf = ".statsprf.json";

        /// <summary>
        /// A file containing execution statistics
        /// </summary>
        public const string Status = ".status.csv";

        /// <summary>
        /// A file containing the trace
        /// </summary>
        public const string Trace = ".trace";

        /// <summary>
        /// A file contains cache miss logs
        /// </summary>
        public const string CacheMissLog = ".CacheMiss.log";

        /// <summary>
        /// A file contains cache miss logs
        /// </summary>
        public const string SurvivingPipProcessChildrenDumpDirectory = "SurvivingPipProcessChildrenDumps";

        /// <summary>
        /// A file with the Pip outputs logged
        /// </summary>
        public const string PipOutputLog = ".PipOutput.log";

        /// <summary>
        /// Custom dev log with less info
        /// </summary>
        public const string DevLog = ".Dev.log";

        /// <summary>
        /// A file containing rpc log entries
        /// </summary>
        public const string RpcLog = ".DistributionRpc.log"; 
        
        /// <summary>
        /// A file containing plugin log entries
        /// </summary>
        public const string PluginLog = ".Plugin.log";

        /// <summary>
        /// A folder for cache miss information
        /// </summary>
        public const string FingerprintsLogDirectory = "Fingerprints";

        /// <summary>
        /// Suffix for the folder containing the FingerprintStore for cache lookup fingerprints.
        /// </summary>
        public const string CacheLookupFingerprintStore = ".CacheLookup";
    }
}
