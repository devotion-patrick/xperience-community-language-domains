# Xperience by Kentico: Dancing Goat Sample Project

This project implements a company website of a fictional coffee shop franchise to demonstrate the Xperience solution's content management and digital marketing features.

## Installation and setup

Follow the instructions in the [Installation](https://docs.xperience.io/x/DQKQC) documentation
to troubleshoot any installation or configuration issues.

### Required: generate the local TLS certificate

The sample is configured for HTTPS on `*.dancinggoat.localtest.me:61154` (see `appsettings.json` → `Kestrel:Endpoints:Https:Certificate`). The certificate file is **not** committed to the repo (it's per-developer and `*.pfx` is `.gitignore`d). You must generate it once before the app will start.

**1.** Install [mkcert](https://github.com/FiloSottile/mkcert) — it runs a local CA and trusts it in your OS:

```powershell
# Pick one
choco install mkcert
scoop install mkcert
winget install FiloSottile.mkcert
```

**2.** Trust the local CA on your machine (one-time, prompts for admin):

```powershell
mkcert -install
```

**3.** From the `examples/DancingGoat` directory, generate the cert covering all the hostnames the sample serves:

```powershell
mkcert -pkcs12 -p12-file dancinggoat.localtest.me.pfx `
  "*.dancinggoat.localtest.me" `
  "dancinggoat.localtest.me" `
  "localhost"
```

mkcert defaults the PFX password to `changeit`, which matches what `appsettings.json` expects. If you override the password (`-p12-password yourpassword`), update `Kestrel:Endpoints:Https:Certificate:Password` accordingly — but prefer using user-secrets so the password doesn't sit in tracked config:

```powershell
dotnet user-secrets set "Kestrel:Endpoints:Https:Certificate:Password" "yourpassword"
```

**4.** Confirm the file is at `examples/DancingGoat/dancinggoat.localtest.me.pfx`. `dotnet run` will now start cleanly with HTTPS, and `https://en.dancinggoat.localtest.me:61154/` opens with a trusted padlock.

> **Why mkcert and not the bundled `dotnet dev-certs` cert?** ASP.NET Core's dev-cert is hard-coded for `localhost` and won't validate against `*.dancinggoat.localtest.me`. mkcert lets you issue arbitrary hostnames against a local CA your machine trusts, without putting any private key on the network.

## Project notes

### Content type and reusable field schema code files

[Content type](https://docs.xperience.io/x/gYHWCQ) and [reusable field schema](https://docs.xperience.io/x/D4_OD) code files under 

- `./Models/Reusable` 
- `./Models/WebPage`
- `./Models/Schema`

are generated using Xperience's [code generators](https://docs.xperience.io/x/5IbWCQ).

If you change the site's content model (add or remove fields, define new content types or schemas, etc.), you can run the following commands from the root of the Dancing Goat project to regenerate the files.

For _reusable field schemas_:

```powershell
dotnet run --no-build -- --kxp-codegen --location "./Models/Schema/" --type ReusableFieldSchemas --namespace "DancingGoat.Models"
```

This command regenerates the interfaces for all reusable field schemas in the project. Note that the specified `--namespace` must match the namespace where content type code files that reference the schemas are generated. You will get uncompilable code otherwise.

For _reusable_ content types:

```powershell
dotnet run --no-build -- --kxp-codegen --location "./Models/Reusable/{name}/" --type ReusableContentTypes --include "DancingGoat.*" --namespace "DancingGoat.Models"
```

This command generates code files for content types with the `DancingGoat` namespace under the `./Models/Reusable` directory.

For _page_ content types:

```powershell
dotnet run --no-build -- --kxp-codegen --location "./Models/WebPage/{name}/" --type PageContentTypes --include "DancingGoat.*" --namespace "DancingGoat.Models"
```

This command generates code files for content types with the `DancingGoat` namespace under the `./Models/WebPage` directory.

You can adapt these examples for use in projects with a different folder structure by modifying the `location` parameter accordingly.
