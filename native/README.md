# Local D2XX Library

Vendor binaries are intentionally excluded from Git.

Place a legally obtained `ftd2xx.dll` at:

```text
native/local/ftd2xx.dll
```

The application project copies that local file to `native/ftd2xx.dll` under its build output. It is loaded only after the operator selects **D2XX inspection transport** and enumerates devices.

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

For a portable, self-contained Windows x86 package, place the inspected local DLL
at `native/local/ftd2xx.dll` and double-click `Build Portable Inspector.bat` in the
repository root. The build creates `artifacts/MyPlasmInspector-win-x86.zip` without
committing the DLL or generated package. The target computer needs neither the .NET
SDK nor the .NET runtime.

The package launcher checks for both `MyPlasm Inspector.exe` and
`native/ftd2xx.dll` before starting. It does not require elevation; installing a
missing FTDI driver may require it. The portable application remains device
enumeration only and does not open a controller or transmit controller bytes.
