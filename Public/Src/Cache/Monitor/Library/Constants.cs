﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BuildXL.Cache.Monitor.App
{
    internal static class Constants
    {
        public enum ResultCode
        {
            Success,

            Cancelled,

            Failure,

            CriticalFailure
        }

        public static string CacheService { get; } = "CacheService";

        public static string ContentAddressableStoreService { get; } = "ContentAddressableStoreService";

        public static string ContentAddressableStoreMasterService { get; } = "ContentAddressableStoreMasterService";

        public static TimeSpan KustoIngestionDelay { get; } = TimeSpan.FromMinutes(10);

        public static string CloudBuildLogEvent { get; } = "CloudBuildLogEvent";

        public static string CloudCacheLogEvent { get; } = "CloudCacheLogEvent";

        public static string DefaultKustoClusterUrl { get; } = "https://cbuild.kusto.windows.net";

        public static string DefaultProdTenantId { get; } = "72f988bf-86f1-41af-91ab-2d7cd011db47";

        public static string DefaultProdAzureAppId { get; } = "22cabbbb-1f32-4057-b601-225bab98348d";

        public static string DefaultTestTenantId { get; } = "975f013f-7f24-47e8-a7d3-abc4752bf346";

        public static string DefaultTestAzureAppId { get; } = "961ae58d-adb0-49c1-a6a2-0b0578c8e9c2";

        public static string DefaultKeyVaultUrl { get; } = "https://cbsecrets.vault.azure.net/";

        public static string DefaultIcmUrl { get; } = "https://prod.microsofticm.com/connector2/ConnectorIncidentManager.svc";

        public static string DefaultIcmCertificateName { get; } = "CacheICMConnector";

        public static Guid DefaultIcmConnectorId { get; } = new Guid("0ec5df2e-e61a-4b79-83cb-51f7adce5a9f");

        public static TimeSpan IcmCertificateCacheTimeToLive { get; } = TimeSpan.FromDays(1);

        public static IReadOnlyDictionary<CloudBuildEnvironment, EnvironmentConfiguration> DefaultEnvironments { get; } =
            new Dictionary<CloudBuildEnvironment, EnvironmentConfiguration>
            {
                {
                    CloudBuildEnvironment.Production,
                    new EnvironmentConfiguration
                    {
                        AzureTenantId = DefaultProdTenantId,
                        AzureAppId = DefaultProdAzureAppId,
                        AzureAppKey = string.Empty,
                        AzureSubscriptionName = "CloudBuild-PROD",
                        AzureSubscriptionId = "7965fc55-7602-4cf6-abe4-e081cf119567",
                        KustoClusterUrl = DefaultKustoClusterUrl,
                        KustoDatabaseName = "CloudBuildProd",
                    }
                },
                {
                    CloudBuildEnvironment.Test,
                    new EnvironmentConfiguration
                    {
                        AzureTenantId = DefaultTestTenantId,
                        AzureAppId = DefaultTestAzureAppId,
                        AzureAppKey = string.Empty,
                        AzureSubscriptionName = "CloudBuild_Test",
                        AzureSubscriptionId = "bf933bbb-8131-491c-81d9-26d7b6f327fa",
                        KustoClusterUrl = DefaultKustoClusterUrl,
                        KustoDatabaseName = "CloudBuildCBTest",
                    }
                },
                // Removing until we actually support/care about this
                //{
                //    CloudBuildEnvironment.ContinuousIntegration,
                //    new EnvironmentConfiguration
                //    {
                //        KustoDatabaseName = "CloudBuildCI",
                //        AzureSubscriptionName = "CloudBuild_Test",
                //        AzureSubscriptionId = "bf933bbb-8131-491c-81d9-26d7b6f327fa",
                //        AzureKeyVaultSubscriptionId = "bf933bbb-8131-491c-81d9-26d7b6f327fa",
                //    }
                //},
            };

        public static TimeSpan RedisScaleTimePerShard { get; } = TimeSpan.FromMinutes(20);
    }
}
