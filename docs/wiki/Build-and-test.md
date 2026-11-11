# Build and test

```
dotnet test native/Osas.Core.Tests -p:Platform=x64
dotnet build native/Adms.Desktop/Adms.Desktop.csproj -c Release -p:Platform=x64
cargo test            # in office-core
```

`dotnet build native/Adms.sln` fails on a PRI duplicate on unchanged main: build projects, not the solution.

## Installers
`installer/suite/build-all.ps1 -Version X` packages the working trees of MynaHub, MynaOps and MynaCAD. Keep them clean and on `main` while it runs. `installer/suite/README.md` lists the silent command lines.

The sibling `MynaUI` folder must exist.
