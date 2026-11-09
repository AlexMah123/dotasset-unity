namespace DotAsset
{
    // Bump together with package.json "version", and add a section at the top of Version.md.
    // Every export JSON and every run log carries this string.
    public static class DotAssetVersion
    {
        public const string Version = "0.13.0";

        // The shape of every run result, export and audit report. Raised only on a breaking change to them, a key
        // removed or renamed or its meaning changed, never for a new key. Docs/AI-Guide.md "Stability".
        public const int Format = 1;
    }
}
