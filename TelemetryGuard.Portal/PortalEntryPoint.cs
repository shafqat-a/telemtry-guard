namespace TelemetryGuard.Portal;

/// <summary>Public marker type for WebApplicationFactory&lt;PortalEntryPoint&gt;.
///
/// BUILD PITFALL (two halves — both are real, and the second is easy to miss):
///  1. Do NOT add `public partial class Program` here the way TelemetryGuard.Api
///     does. The test projects reference both assemblies, and two public `Program`
///     types in the global namespace make every existing
///     WebApplicationFactory&lt;Program&gt; call ambiguous (CS0433).
///  2. Do NOT grant InternalsVisibleTo to TelemetryGuard.Tests.Unit /
///     .Tests.Integration either. This project's Program.cs uses top-level
///     statements, which emit an INTERNAL `Program` class in the global namespace;
///     IVT makes it accessible in the test assemblies and produces exactly the same
///     CS0433 against TelemetryGuard.Api's `public partial class Program`.
///     TelemetryGuard.Training.csproj carries this same warning in a comment for
///     the same reason — follow that precedent.
/// The consequence, which the rest of this task assumes: every portal type a test
/// touches is `public`, not `internal`.</summary>
public sealed class PortalEntryPoint;
