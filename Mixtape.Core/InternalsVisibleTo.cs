using System.Runtime.CompilerServices;

// The engine types are `internal`. Expose them to the app front-ends that consume this core
// without having to make every type public: the existing WinForms app (assembly "Mixtape")
// and the in-progress cross-platform Avalonia app (assembly "Mixtape.App").
[assembly: InternalsVisibleTo("Mixtape")]
[assembly: InternalsVisibleTo("Mixtape.App")]
