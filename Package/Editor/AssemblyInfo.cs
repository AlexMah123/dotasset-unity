using System.Runtime.CompilerServices;

// The EditMode tests in tests/editmode call the YAML rewriters' text helpers, which stay internal.
[assembly: InternalsVisibleTo("DotAsset.Tests")]
// The Unity CLI adapter in Editor/UnityCli, compiled only with com.unity.pipeline, runs through EditorRun.
[assembly: InternalsVisibleTo("DotAsset.Editor.UnityCli")]
