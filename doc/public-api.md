# Public API

What SharpMediaFoundationInterop and SharpMediaFoundationInterop.WPF expose is written down, and a
change to it does not build until it is written down too. Two checks do this, both on every
`dotnet build` of each:

- **The API files.** `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` next to each project list
  its public types and members, the same on both of its target frameworks. Shipped is the API as
  released; Unshipped is what changed since. A public symbol missing from them fails the build with
  RS0016, a listed one that is gone with RS0017.
- **Package validation.** Each package being built is compared with the last release
  (`PackageValidationBaselineVersion` in the project, 0.3.1 now), and every binary or source breaking
  change fails the build with a CP error. It also checks that the target frameworks agree.

## No platform's types

The public API is of no platform's own types. The Win32 interop CsWin32 generates is internal
(`"public": false` in `NativeMethods.json`), in each library that uses it: the WPF library has its
own, for the Direct3D it draws with, rather than the core's. What the libraries hand each other of
a platform's is a native pointer in a type of their own: a decoder that decodes on the GPU is an
`IGpuVideoTransform`, given a `GpuDevice` (an `ID3D11Device` pointer on Windows) and handing out a
`DecodedFrame` (a texture pointer and its slice, or a copy to memory). `InternalsVisibleTo` is for
tests alone.

The bindings of the other platforms are internal too - Android's NDK, Apple's Objective-C, GStreamer
- and so are the classes of every platform made of them: the codecs, captures and audio inputs and
outputs of Windows (Media Foundation, `DeviceCapture`, `ScreenCapture`, `WaveIn`, `WaveOut`),
Android, Apple's systems and Linux are made by `MediaCodecs` and `MediaDevices`, and handed out as the
interfaces they implement (`IMediaVideoTransform`, `IGpuVideoTransform`, `IMediaVideoSource`,
`IAudioInput`, ...). A class of one platform is not to be added to the public API: what it offers is
to be offered by an interface, made by one of the factories. A caller that needs a decoder of its own
- one handing out some of the frames, say - wraps the one a factory makes, implementing the same
interfaces.

## Adding to the API

Build, and accept the RS0016 fix in the IDE ("Add to public API"), or from the command line:

```
dotnet format analyzers src/SharpMediaFoundationInterop/SharpMediaFoundationInterop.csproj --diagnostics RS0016 --severity warn
```

The new lines land in that project's `PublicAPI.Unshipped.txt`.

## Removing or changing the API

RS0017 names what disappeared. Moving its line from Shipped to Unshipped with a `*REMOVED*` prefix
records the removal; a member whose signature changed is a removal of the old line and an addition
of the new one.

If package validation then reports a CP error, the change breaks code built against the last
release. When that is intended, record it:

```
dotnet build src/SharpMediaFoundationInterop/SharpMediaFoundationInterop.csproj -c Release -p:ApiCompatGenerateSuppressionFile=true
```

That rewrites `CompatibilitySuppressions.xml` next to the project. Say in the pull request what broke.

Validation runs as part of the build, because the build packs. `dotnet pack` on its own does not
rebuild and validates whatever binaries are already in `bin`.

## Releasing

1. Move every line of each `PublicAPI.Unshipped.txt` into the `PublicAPI.Shipped.txt` beside it,
   dropping the `*REMOVED*` lines together with the shipped lines they remove.
2. Once the release is on nuget.org, set `PackageValidationBaselineVersion` to it in both projects,
   and delete the `CompatibilitySuppressions.xml` files: what they excused is in the new baseline.

## What is recorded now

Against 0.3.1, `CompatibilitySuppressions.xml` records:

- **The Win32 interop made internal**: the 745 `Windows.Win32` types 0.3.1 made public, and
  `MediaUtils`' helpers of Media Foundation (`CreateSample`, `CreateOutputDataBuffer`,
  `CalculateSampleDuration`, `EncodeAttributeValue`).
- **Windows' classes made internal**: `DeviceCapture`, `ScreenCapture`, `CaptureDevice` and
  `ScreenDevice`, made by `MediaDevices` now. (0.3.1's codecs were of the `Transforms` namespace,
  below.)
- **What changed shape since 0.3.1**: the `Transforms` namespace that is `Codecs` now, the `Wave`
  types, the base types of `DeviceCapture` and `ScreenCapture`, and in the WPF library `VideoControl`,
  `VideoSourceBase` and `IVideoSource.GetVideoSample`.
