# Local D2XX Library

Vendor binaries are intentionally excluded from Git.

The locally verified original DLL is x86, file version `3.01.19`, SHA-256
`381117C743766E3A696609BB29CA075772AA603CFF196E16C3854C06EE1AB254`.
It remains a local dependency and must not be committed or redistributed.

Place a legally obtained `ftd2xx.dll` at:

```text
native/local/ftd2xx.dll
```

The application project copies that local file to `native/ftd2xx.dll` under its
build output. It is loaded only after the operator explicitly clicks
**Inspect D2XX Devices**.

Inspect the file before use:

```powershell
dotnet run --project tools/MyPlasm.Inspector.PeInspector -- native/local/ftd2xx.dll
```

To check compatibility with an explicitly selected application architecture:

```powershell
dotnet run --project tools/MyPlasm.Inspector.PeInspector -- native/local/ftd2xx.dll --architecture x86
```

The utility reports PE architecture, file version, SHA-256, selected application architecture, and load compatibility. Do not force-add the DLL or copy it into tracked source or evidence folders.

Run the app with a matching process architecture when required, for example:

```powershell
dotnet run --project src/MyPlasm.Inspector.App -p:Platform=x86
```

The corresponding .NET 8 desktop runtime architecture must be installed. An architecture mismatch is reported before native loading is attempted.

## Portable x86 package

For a portable, self-contained Windows x86 package, place the inspected local
DLL at `native/local/ftd2xx.dll` and double-click
`Build Portable Inspector.bat` in the repository root. The build creates
`artifacts/MyPlasmInspector-win-x86-diagnostic.zip` without committing the DLL
or generated package. The target computer needs neither the .NET SDK nor the
.NET runtime.

The portable builder refuses any DLL that differs from the confirmed identity:
x86, file version `3.01.19`, size `206144` bytes, and SHA-256
`381117C743766E3A696609BB29CA075772AA603CFF196E16C3854C06EE1AB254`.
It also requires a clean Git worktree so `package-manifest.json` can bind the
package to one exact source commit. Build and ZIP validation happen in staging;
the prior validated package is retained unless the replacement passes all
checks.

The package launcher checks for both `MyPlasm Inspector.exe` and
`native/ftd2xx.dll` before starting. It does not require elevation; installing
a missing FTDI driver may require it. The portable application remains device
enumeration only and does not open a controller or transmit controller bytes.
