using System.Runtime.CompilerServices;

// The EditMode tests in tests/editmode call the YAML rewriters' text helpers, which stay internal.
[assembly: InternalsVisibleTo("DotAsset.Tests")]
