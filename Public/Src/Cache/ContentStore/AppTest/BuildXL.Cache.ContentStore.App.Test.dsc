// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

import * as XUnit from "Sdk.Managed.Testing.XUnit";
import * as ManagedSdk from "Sdk.Managed";

namespace AppTest {
    @@public
    export const dll = BuildXLSdk.cacheTest({
        assemblyName: "BuildXL.Cache.ContentStore.App.Test",
        sources: globR(d`.`, "*.cs"),
        assemblyBindingRedirects: BuildXLSdk.cacheBindingRedirects(),
        appConfig: f`App.Config`,
        references: [
            ...addIf(BuildXLSdk.isFullFramework,
                NetFx.System.Xml.dll,
                NetFx.System.Xml.Linq.dll,
                NetFx.System.Runtime.Serialization.dll,
                NetFx.Netstandard.dll
            ),

            Hashing.dll,
            Interfaces.dll,
            Library.dll,

            InterfacesTest.dll,
            Test.dll,
            
            importFrom("BuildXL.Utilities").Native.dll,
            importFrom("BuildXL.Utilities").Interop.dll,

            ...BuildXLSdk.fluentAssertionsWorkaround
        ],
        runtimeContent: [
            {
                subfolder: r`app`,
                contents: [
                    App.exe
                ]
            },
        ]
    });
}
