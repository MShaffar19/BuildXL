// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as GrpcSdk from "Sdk.Protocols.Grpc";

namespace Grpc {
    @@public
    export const dll = BuildXLSdk.library({
        assemblyName: "BuildXL.Cache.Roxis.Grpc",
        sources: [
            ...globR(d`.`,"*.cs"),
            ...GrpcSdk.generateCSharp({rpc: [f`RoxisService.proto`]}).sources,
        ],
        references: [
            importFrom("RuntimeContracts").pkg,
            ...importFrom("BuildXL.Cache.ContentStore").getGrpcPackages(true),
            ...BuildXLSdk.bclAsyncPackages,
        ],
        internalsVisibleTo: [
            "BuildXL.Cache.Roxis.Test",
        ],
        skipDocumentationGeneration: true,
        nullable: true
    });
}
