# GT bridge tests

The first automated tests this bridge has ever had (#3365).

## What they cover, and what they cannot

The GT bridge is one flat Web SDK project whose entry point is a file of
top-level statements, so it cannot be referenced as a library. The test project
therefore compiles in the handful of files that are **pure** - no COM, no SQL
connection - and covers exactly those:

- `DocumentTypes` - the measured `dok_Typ` codes. These decide whether a
  retried order creates a second ZK and whether stock is released twice, so a
  change to one of them is a change to that.
- `PriceLevel` - the operator's 1-based level against the Sfera object model's
  0-based id, and that the read columns and the write target name one level.
- `ImageUrlSignature` - that an unconfigured bridge signs nothing and refuses
  nothing, and that the query suffix is either empty or whole.

Everything else in this bridge - every document write, every stock movement,
every kontrahent upsert - goes through COM against a running Subiekt GT. None
of it is reachable from here, and this project does not pretend otherwise. The
sibling nexo bridge has four test projects because it has layers to test; this
one has one file of constants and two of string rendering.

## Running them

They need the .NET SDK and a Windows host (the project targets net8.0 and the
bridge is Windows-only anyway):

    cd bridge-gt-tests
    dotnet test

They do NOT need Subiekt, SQL Server, or a running bridge.

## Not wired to CI

Neither bridge in this repository has CI. These tests are a gate somebody runs,
not one that runs itself - stated here rather than implied by their existence.

## Deploying the /gt-image gate

Signing image URLs (#3365) invalidates every URL OpenLinker stored before the
gate existed - they carry no signature and answer 404 once it is deployed.

It self-heals on the next catalogue sweep (20 minutes at the default cadence)
and nothing already published to a marketplace is affected, because
marketplaces copy the bytes rather than hot-linking. To skip the window,
trigger a product sweep straight after deploying.
