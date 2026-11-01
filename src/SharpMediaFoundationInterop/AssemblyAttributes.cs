using System.Runtime.CompilerServices;

// Of no marshalling the runtime does: every call to native code - Media Foundation's and Direct3D's COM of the source
// generators, each platform's library of LibraryImport - is marshalled by code generated as it is compiled, so the
// library is trimmed and compiled ahead of time alike. A DllImport, or a by-ref of one, would fail as it is called.
[assembly: DisableRuntimeMarshalling]
