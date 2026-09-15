using System.Text;
using SweepV.Core.Cleanup;

namespace SweepV.Core.Tests
{
    public class CatalogFileTests
    {
        private static CatalogFile LoadShippedFile()
        {
            using var stream = typeof(CleanupCatalog).Assembly.GetManifestResourceStream("SweepV.Core.Cleanup.cleanup-targets.json");
            Assert.NotNull(stream);
            return CleanupCatalog.Load(stream);
        }

        [Fact]
        public void ShippedFile_AllEntriesAreValid()
        {
            var errors = LoadShippedFile().Targets.SelectMany(CleanupCatalog.Validate).ToList();
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }

        [Fact]
        public void ShippedFile_IdsAreUnique()
        {
            var duplicates = LoadShippedFile().Targets.GroupBy(t => t.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.Empty(duplicates);
        }

        [Fact]
        public void DefaultCatalog_IncludesAllFileEntriesAndSystemActions()
        {
            var catalog = CleanupCatalog.CreateDefault();
            Assert.Equal(LoadShippedFile().Targets.Count + SystemActions.Create().Count, catalog.Count);
        }

        [Theory]
        [InlineData("""{ "id": "x", "name": "X", "description": "X", "category": "windows", "paths": ["%USERPROFILE%"] }""")]
        [InlineData("""{ "id": "x", "name": "X", "description": "X", "category": "windows", "paths": ["C:/Temp"] }""")]
        [InlineData("""{ "id": "x", "name": "X", "description": "X", "category": "windows", "paths": ["%APPDATA%/../.."] }""")]
        [InlineData("""{ "id": "x", "name": "X", "description": "X", "category": "windows", "paths": ["%APPDATA%/*"] }""")]
        [InlineData("""{ "id": "x", "name": "X", "description": "X", "category": "nope", "paths": ["%APPDATA%/Foo"] }""")]
        [InlineData("""{ "id": "Bad Id", "name": "X", "description": "X", "category": "windows", "paths": ["%APPDATA%/Foo"] }""")]
        public void Validate_RejectsDangerousOrMalformedEntries(string entryJson)
        {
            var file = CleanupCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "targets": [ {{entryJson}} ] }""")));
            Assert.NotEmpty(CleanupCatalog.Validate(file.Targets[0]));
        }

        [Fact]
        public void ExpandPath_ResolvesTokensAndWildcards()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path.Combine(root, "Profile 1", "Cache"));
            Directory.CreateDirectory(Path.Combine(root, "Default", "Cache"));
            try
            {
                var relative = Path.GetRelativePath(Path.GetTempPath(), root);
                var expanded = CleanupCatalog.ExpandPath($"%TEMP%/{relative}/*/Cache").ToList();

                Assert.Equal(2, expanded.Count);
                Assert.All(expanded, p => Assert.True(Directory.Exists(p)));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
