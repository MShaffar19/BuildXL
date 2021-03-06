﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.Serialization;
using System.Text;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;

namespace BuildXL.Cache.ContentStore.Service
{
    /// <summary>
    /// Configuration used to place a file from a running instance of CASaaS.
    /// </summary>
    [DataContract]
    public class PlaceConfiguration : DistributedServiceConfiguration
    {
        /// <summary>
        /// Constructor for PlaceConfiguration.
        /// </summary>
        public PlaceConfiguration(
            ContentHash contentHash,
            AbsolutePath destinationPath,
            uint grpcPort,
            string cacheName,
            AbsolutePath cachePath)
            : base(dataRootPath: null, gracefulShutdownSeconds: 5, grpcPort: grpcPort, stampId: null, ringId: null, cacheName: cacheName, cachePath: cachePath, grpcPortFileName: null)
        {
            ContentHash = contentHash;
            DestinationPath = destinationPath;
        }

        /// <summary>
        /// Hash for the content to be placed.
        /// </summary>
        [DataMember]
        public ContentHash ContentHash { get; private set; }

        /// <summary>
        /// Path to where the content should be placed.
        /// </summary>
        [DataMember]
        public AbsolutePath DestinationPath { get; private set; }

        /// <inheritdoc />
        public override string GetVerb()
        {
            return "placefile";
        }

        /// <inheritdoc />
        public override string GetCommandLineArgs(LocalServerConfiguration? localContentServerConfiguration = null, string? scenario = null, bool logAutoFlush = false, bool passMaxConfigurations = false)
        {
            var args = new StringBuilder(base.GetCommandLineArgs(localContentServerConfiguration, scenario, logAutoFlush, false));

            args.AppendFormat(" /hash:{0}", ContentHash.ToHex());
            args.AppendFormat(" /hashType:{0}", ContentHash.HashType.ToString());
            args.AppendFormat(" /path:{0}", DestinationPath.Path);

            return args.ToString();
        }
    }
}
